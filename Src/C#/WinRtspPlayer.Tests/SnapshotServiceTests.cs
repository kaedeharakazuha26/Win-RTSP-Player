using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Media;
using Xunit;

namespace WinRtspPlayer.Tests;

public class SnapshotServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinRtspSnapshotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_SucceedsDirectlyWithoutPausingPlayingCamera()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        byte[] jpeg = CreateMinimalJpeg(width: 1280, height: 720);

        var runner = new FakeSnapshotRunner((_, _, _, _, _) => Task.FromResult(jpeg));
        var playback = new FakeLivePlaybackController(isPlaying: true);
        var service = new SnapshotService(store, runner, playback, TimeSpan.FromSeconds(5));

        DateTimeOffset before = DateTimeOffset.UtcNow;
        SnapshotResult result = await service.CaptureAsync(cam);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.Equal(jpeg, result.JpegBytes);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.False(result.UsedPauseFallback);
        Assert.InRange(result.CapturedAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(1, runner.CallCount);
        Assert.Empty(playback.Events);
    }

    [Fact]
    public async Task CaptureAsync_WhenFirstAttemptFailsOnPlayingCamera_PausesCapturesAndResumes()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        byte[] jpeg = CreateMinimalJpeg(width: 640, height: 360);
        var callLog = new List<string>();

        int attempt = 0;
        var runner = new FakeSnapshotRunner((_, _, _, _, _) =>
        {
            attempt++;
            callLog.Add($"capture:{attempt}");
            if (attempt == 1)
            {
                throw new InvalidOperationException("453 Not Enough Bandwidth / max connections");
            }

            return Task.FromResult(jpeg);
        });

        var playback = new FakeLivePlaybackController(isPlaying: true, onEvent: e => callLog.Add(e));
        var service = new SnapshotService(store, runner, playback);

        SnapshotResult result = await service.CaptureAsync(cam);

        Assert.True(result.UsedPauseFallback);
        Assert.Equal(640, result.Width);
        Assert.Equal(360, result.Height);
        Assert.Equal(
            ["capture:1", $"stop:{cam.Id}", "capture:2", $"start:{cam.Id}"],
            callLog);
    }

    [Fact]
    public async Task CaptureAsync_WhenRetryAlsoFails_StillResumesLiveCameraInFinally()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        using var cts = new CancellationTokenSource();

        int attempt = 0;
        var runner = new FakeSnapshotRunner((_, _, _, _, _) =>
        {
            attempt++;
            if (attempt == 2)
            {
                // Cancel caller token mid-retry to verify finally still resumes with CancellationToken.None.
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            throw new InvalidOperationException("connection rejected");
        });

        var playback = new FakeLivePlaybackController(isPlaying: true);
        var service = new SnapshotService(store, runner, playback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(cam, cts.Token));

        Assert.Equal(2, runner.CallCount);
        Assert.Equal(
            [$"stop:{cam.Id}", $"start:{cam.Id}"],
            playback.Events);
        Assert.Equal(CancellationToken.None, playback.LastStartCancellationToken);
    }

    [Fact]
    public async Task CaptureAsync_WhenCameraIsNotPlayingAndFails_DoesNotPauseOrResume()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        var runner = new FakeSnapshotRunner((_, _, _, _, _) =>
            throw new InvalidOperationException("connection refused"));
        var playback = new FakeLivePlaybackController(isPlaying: false);
        var service = new SnapshotService(store, runner, playback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(cam));

        Assert.Equal(1, runner.CallCount);
        Assert.Empty(playback.Events);
    }

    [Fact]
    public async Task CaptureAsync_SerializesConcurrentRequestsForSameCamera()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        byte[] jpeg = CreateMinimalJpeg(width: 800, height: 600);

        int activeCount = 0;
        int maxConcurrentCount = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeSnapshotRunner(async (_, _, _, _, ct) =>
        {
            int current = Interlocked.Increment(ref activeCount);
            UpdateMax(ref maxConcurrentCount, current);
            try
            {
                if (firstEntered.TrySetResult())
                {
                    await releaseFirst.Task.WaitAsync(ct);
                }

                return jpeg;
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        });

        var service = new SnapshotService(store, runner);

        Task<SnapshotResult> task1 = service.CaptureAsync(cam);
        await firstEntered.Task;

        Task<SnapshotResult> task2 = service.CaptureAsync(cam);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref activeCount));
        Assert.False(task2.IsCompleted);

        releaseFirst.SetResult();
        SnapshotResult[] results = await Task.WhenAll(task1, task2);

        Assert.Equal(2, results.Length);
        Assert.Equal(1, maxConcurrentCount);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task FfmpegSnapshotRunner_SanitizesCredentialsFromErrorMessages()
    {
        string basic = FfmpegSnapshotRunner.SanitizeErrorText(
            "Error opening input rtsp://user:secret@192.168.1.5:554/stream1: Connection refused");
        Assert.Equal(
            "Error opening input rtsp://***@192.168.1.5:554/stream1: Connection refused",
            basic);

        string specialChars = FfmpegSnapshotRunner.SanitizeErrorText(
            "rtsp://admin:p@ss:w0rd!#$%^&*()_+=%2F@10.0.0.20:8554/h264?channel=1 failed\nrtsp://10.0.0.21:554/ok");
        Assert.Equal(
            "rtsp://***@10.0.0.20:8554/h264?channel=1 failed\nrtsp://10.0.0.21:554/ok",
            specialChars);

        var runner = new FfmpegSnapshotRunner();
        string missingFfmpeg = Path.Combine(_tempDir, "nonexistent-ffmpeg.exe");
        string secretUrl = "rtsp://camuser:SuperSecret!@192.168.1.99:554/live";

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.CaptureJpegAsync(missingFfmpeg, secretUrl, "tcp", TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.DoesNotContain("SuperSecret!", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("camuser", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseJpegDimensions_ParsesSofMarkersAndHandlesMissingOrTruncatedSof()
    {
        byte[] sof0 = CreateMinimalJpeg(width: 1920, height: 1080, sofMarker: 0xC0);
        Assert.True(SnapshotService.TryParseJpegDimensions(sof0, out int w0, out int h0));
        Assert.Equal(1920, w0);
        Assert.Equal(1080, h0);

        byte[] sof2 = CreateMinimalJpeg(width: 1280, height: 720, sofMarker: 0xC2);
        Assert.True(SnapshotService.TryParseJpegDimensions(sof2, out int w2, out int h2));
        Assert.Equal(1280, w2);
        Assert.Equal(720, h2);

        byte[] noSof = [0xFF, 0xD8, 0xFF, 0xD9];
        Assert.False(SnapshotService.TryParseJpegDimensions(noSof, out int wMissing, out int hMissing));
        Assert.Equal(0, wMissing);
        Assert.Equal(0, hMissing);

        byte[] truncatedSof = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08];
        Assert.False(SnapshotService.TryParseJpegDimensions(truncatedSof, out int wTrunc, out int hTrunc));
        Assert.Equal(0, wTrunc);
        Assert.Equal(0, hTrunc);

        byte[] zeroHeightSof = CreateMinimalJpeg(width: 640, height: 0, sofMarker: 0xC0);
        Assert.False(SnapshotService.TryParseJpegDimensions(zeroHeightSof, out int wZeroH, out int hZeroH));
        Assert.Equal(0, wZeroH);
        Assert.Equal(0, hZeroH);

        byte[] zeroWidthSof = CreateMinimalJpeg(width: 0, height: 480, sofMarker: 0xC0);
        Assert.False(SnapshotService.TryParseJpegDimensions(zeroWidthSof, out int wZeroW, out int hZeroW));
        Assert.Equal(0, wZeroW);
        Assert.Equal(0, hZeroW);
    }

    [Fact]
    public async Task CaptureAsync_WhenFfmpegFailsToStart_DoesNotPauseOrResumePlayingCamera()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        var playback = new FakeLivePlaybackController(isPlaying: true);

        // 1. Direct FileNotFoundException / Win32Exception from ISnapshotRunner
        var fnfRunner = new FakeSnapshotRunner((_, _, _, _, _) =>
            throw new FileNotFoundException("ffmpeg.exe not found"));
        var fnfService = new SnapshotService(store, fnfRunner, playback);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fnfService.CaptureAsync(cam));
        Assert.Equal(1, fnfRunner.CallCount);
        Assert.Empty(playback.Events);

        var win32Runner = new FakeSnapshotRunner((_, _, _, _, _) =>
            throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified."));
        var win32Service = new SnapshotService(store, win32Runner, playback);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => win32Service.CaptureAsync(cam));
        Assert.Equal(1, win32Runner.CallCount);
        Assert.Empty(playback.Events);

        // 2. Real FfmpegSnapshotRunner with missing executable path
        store.UpdateSettings(cfg => cfg.FfmpegPath = Path.Combine(_tempDir, "missing-ffmpeg.exe"));
        var realService = new SnapshotService(store, new FfmpegSnapshotRunner(), playback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => realService.CaptureAsync(cam));
        Assert.Empty(playback.Events);
    }

    [Fact]
    public async Task CaptureAsync_WhenStoredPasswordCannotDecrypt_ThrowsCleanInvalidOperationWithoutLeakingSecret()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();
        CameraConfig cam = store.AddCamera(new CameraAddRequest(
            Name: "Broken Secret Cam",
            Ip: "192.168.1.77",
            Username: "admin",
            Password: "ValidPassword123"));

        // Corrupt the stored ciphertext so Unprotect throws FormatException / CryptographicException.
        cam.EncryptedPassword = "not-valid-base64-or-dpapi!!!";

        var runner = new FakeSnapshotRunner((_, _, _, _, _) => Task.FromResult(CreateMinimalJpeg(640, 480)));
        var playback = new FakeLivePlaybackController(isPlaying: true);
        var service = new SnapshotService(store, runner, playback);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(cam));
        Assert.Contains(cam.Id, ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be decrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ValidPassword123", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-valid-base64", ex.Message, StringComparison.Ordinal);

        // Also verify CryptographicException from valid Base64 with invalid DPAPI payload
        cam.EncryptedPassword = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);
        InvalidOperationException cryptoEx = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(cam));
        Assert.Contains(cam.Id, cryptoEx.Message, StringComparison.Ordinal);
        Assert.Contains("could not be decrypted", cryptoEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.CallCount);
        Assert.Empty(playback.Events);
    }

    [Fact]
    public async Task CaptureAsync_WhenCanceledRightAsFallbackStopsCamera_StillResumesLiveCamera()
    {
        (CameraStore store, CameraConfig cam) = CreateStoreWithCamera();
        using var cts = new CancellationTokenSource();

        var runner = new FakeSnapshotRunner((_, _, _, _, _) =>
            throw new InvalidOperationException("453 Not Enough Bandwidth"));

        var playback = new FakeLivePlaybackController(isPlaying: true, onEvent: ev =>
        {
            if (ev.StartsWith("stop:", StringComparison.Ordinal))
            {
                // Cancel caller token right when StopCameraAsync completes.
                cts.Cancel();
            }
        });

        var service = new SnapshotService(store, runner, playback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(cam, cts.Token));
        Assert.Equal(1, runner.CallCount);
        Assert.Equal([$"stop:{cam.Id}", $"start:{cam.Id}"], playback.Events);
        Assert.Equal(CancellationToken.None, playback.LastStartCancellationToken);
    }

    [Fact]
    public async Task FfmpegSnapshotRunner_PreCanceledTokenAndWindowsExeResolution()
    {
        var runner = new FfmpegSnapshotRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException canceledEx = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.CaptureJpegAsync(
                Path.Combine(_tempDir, "nonexistent-ffmpeg.exe"),
                "rtsp://127.0.0.1:554/stream1",
                "tcp",
                TimeSpan.FromSeconds(2),
                cts.Token));
        Assert.Equal(cts.Token, canceledEx.CancellationToken);

        string dummyNoExt = Path.Combine(_tempDir, "custom-ffmpeg");
        string dummyExe = dummyNoExt + ".exe";
        await File.WriteAllBytesAsync(dummyExe, [0x00]);

        InvalidOperationException startEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.CaptureJpegAsync(
                dummyNoExt,
                "rtsp://127.0.0.1:554/stream1",
                "tcp",
                TimeSpan.FromSeconds(2),
                CancellationToken.None));
        Assert.Contains(dummyExe, startEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    private (CameraStore Store, CameraConfig Camera) CreateStoreWithCamera()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();
        CameraConfig cam = store.AddCamera(new CameraAddRequest(
            Name: "Test Cam",
            Ip: "192.168.1.50",
            Port: 554,
            Path: "/stream1",
            Username: "admin",
            Password: "secretPassword!@#",
            Transport: "tcp"));
        return (store, cam);
    }

    private static byte[] CreateMinimalJpeg(int width, int height, byte sofMarker = 0xC0)
    {
        return
        [
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x06, 0x4A, 0x46, 0x49, 0x46, // APP0 dummy
            0xFF, sofMarker, 0x00, 0x0B, 0x08, // SOFn header (len=11, 8-bit)
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9 // EOI
        ];
    }

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                break;
            }
        }
    }

    private sealed class FakeSnapshotRunner(
        Func<string, string, string, TimeSpan, CancellationToken, Task<byte[]>> handler) : ISnapshotRunner
    {
        public int CallCount { get; private set; }

        public Task<byte[]> CaptureJpegAsync(
            string ffmpegPath,
            string rtspUrl,
            string transport,
            TimeSpan timeout,
            CancellationToken ct)
        {
            CallCount++;
            return handler(ffmpegPath, rtspUrl, transport, timeout, ct);
        }
    }

    private sealed class FakeLivePlaybackController(bool isPlaying, Action<string>? onEvent = null)
        : ILivePlaybackController
    {
        private bool _isPlaying = isPlaying;
        public List<string> Events { get; } = [];
        public CancellationToken LastStartCancellationToken { get; private set; }

        public bool IsPlaying(string cameraId) => _isPlaying;

        public Task<bool> StartCameraAsync(string cameraId, CancellationToken ct = default)
        {
            _isPlaying = true;
            LastStartCancellationToken = ct;
            string ev = $"start:{cameraId}";
            Events.Add(ev);
            onEvent?.Invoke(ev);
            return Task.FromResult(true);
        }

        public Task StopCameraAsync(string cameraId, CancellationToken ct = default)
        {
            _isPlaying = false;
            string ev = $"stop:{cameraId}";
            Events.Add(ev);
            onEvent?.Invoke(ev);
            return Task.CompletedTask;
        }

        public Task<bool> RestartCameraAsync(string cameraId, CancellationToken ct = default)
        {
            string ev = $"restart:{cameraId}";
            Events.Add(ev);
            onEvent?.Invoke(ev);
            return Task.FromResult(true);
        }
    }
}
