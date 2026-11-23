using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WinRtspPlayer.Core.Config;

namespace WinRtspPlayer.Core.Media;

/// <summary>
/// Result of an on-demand JPEG snapshot capture.
/// </summary>
public sealed record SnapshotResult(
    byte[] JpegBytes,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc,
    bool UsedPauseFallback);

/// <summary>
/// Executes a single-frame RTSP snapshot capture and returns raw JPEG bytes.
/// </summary>
public interface ISnapshotRunner
{
    Task<byte[]> CaptureJpegAsync(
        string ffmpegPath,
        string rtspUrl,
        string transport,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Spawns <c>ffmpeg.exe</c> to grab a single JPEG frame directly into memory via stdout pipe,
/// enforcing a strict timeout and redacting RTSP credentials from all error messages.
/// </summary>
public sealed class FfmpegSnapshotRunner : ISnapshotRunner
{
    private static readonly Regex RtspCredentialRegex = new(
        @"(?i)\brtsps?://(?:[^\s/@""'<>]+@)+",
        RegexOptions.Compiled);

    public async Task<byte[]> CaptureJpegAsync(
        string ffmpegPath,
        string rtspUrl,
        string transport,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("FFmpeg executable path must not be empty.", nameof(ffmpegPath));
        }

        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            throw new ArgumentException("RTSP URL must not be empty.", nameof(rtspUrl));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Snapshot timeout must be positive.");
        }

        string resolvedFfmpegPath = ResolveExecutablePath(ffmpegPath.Trim());
        string effectiveTransport = string.IsNullOrWhiteSpace(transport)
            ? "tcp"
            : transport.Trim().ToLowerInvariant();

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedFfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string[] arguments =
        [
            "-hide_banner",
            "-loglevel", "error",
            "-rtsp_transport", effectiveTransport,
            "-i", rtspUrl,
            "-frames:v", "1",
            "-vf", "scale='min(1280,iw)':-2",
            "-q:v", "4",
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "pipe:1"
        ];

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start ffmpeg '{resolvedFfmpegPath}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to start ffmpeg '{resolvedFfmpegPath}': {SanitizeErrorText(ex.Message)}");
        }

        using var timeoutCts = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var stdoutStream = new MemoryStream();
        Task copyStdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutStream, linkedCts.Token);
        Task<string> readStderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
        Task waitForExitTask = process.WaitForExitAsync(linkedCts.Token);

        try
        {
            await Task.WhenAll(copyStdoutTask, readStderrTask, waitForExitTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await DrainTasksQuietlyAsync(copyStdoutTask, readStderrTask, waitForExitTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await DrainTasksQuietlyAsync(copyStdoutTask, readStderrTask, waitForExitTask).ConfigureAwait(false);
            throw new TimeoutException($"ffmpeg snapshot timed out after {timeout.TotalSeconds:0.#}s.");
        }
        catch
        {
            TryKillProcessTree(process);
            await DrainTasksQuietlyAsync(copyStdoutTask, readStderrTask, waitForExitTask).ConfigureAwait(false);
            throw;
        }

        string stderr = SanitizeErrorText(await readStderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrEmpty(stderr) ? "no error output" : stderr;
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {detail}");
        }

        byte[] bytes = stdoutStream.ToArray();
        if (bytes.Length == 0)
        {
            string detail = string.IsNullOrEmpty(stderr) ? string.Empty : $" {stderr}";
            throw new InvalidOperationException($"ffmpeg produced an empty snapshot.{detail}");
        }

        return bytes;
    }

    /// <summary>
    /// Redacts credentials in any <c>rtsp://userinfo@host</c> substrings so passwords never leak into logs or MCP errors.
    /// </summary>
    public static string SanitizeErrorText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return RtspCredentialRegex.Replace(text, static match =>
        {
            int schemeEnd = match.Value.IndexOf("://", StringComparison.Ordinal) + 3;
            return string.Concat(match.Value.AsSpan(0, schemeEnd), "***@");
        });
    }

    private static string ResolveExecutablePath(string ffmpegPath)
    {
        if (Path.IsPathRooted(ffmpegPath))
        {
            return ffmpegPath;
        }

        // Resolve relative subpaths against AppContext.BaseDirectory rather than Environment.CurrentDirectory,
        // while keeping bare command names (e.g. "ffmpeg") resolvable via PATH.
        if (ffmpegPath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ffmpegPath));
        }

        string localCandidate = Path.Combine(AppContext.BaseDirectory, ffmpegPath);
        if (File.Exists(localCandidate))
        {
            return localCandidate;
        }

        return ffmpegPath;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited concurrently; ignore cleanup failures.
        }
    }

    private static async Task DrainTasksQuietlyAsync(params Task[] tasks)
    {
        Task all = Task.WhenAll(tasks);
        _ = all.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        await Task.WhenAny(all, Task.Delay(500)).ConfigureAwait(false);
    }
}

/// <summary>
/// Captures on-demand JPEG snapshots from configured cameras, serializing per-camera requests
/// and falling back to a brief live-tile pause/resume for 1-connection RTSP cameras.
/// </summary>
public sealed class SnapshotService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly CameraStore _store;
    private readonly ISnapshotRunner _runner;
    private readonly ILivePlaybackController? _playback;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cameraLocks = new(StringComparer.Ordinal);

    public SnapshotService(
        CameraStore store,
        ISnapshotRunner runner,
        ILivePlaybackController? playback = null,
        TimeSpan? timeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _playback = playback;

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Snapshot timeout must be positive.");
        }

        _timeout = effectiveTimeout;
    }

    public async Task<SnapshotResult> CaptureAsync(CameraConfig camera, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(camera);

        string lockKey = string.IsNullOrEmpty(camera.Id) ? $"slot:{camera.Slot}" : camera.Id;
        SemaphoreSlim gate = _cameraLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string ffmpegPath = _store.GetConfigSnapshot().FfmpegPath;
            string rtspUrl = _store.BuildRtspUrl(camera);
            bool usedFallback = false;
            byte[] bytes;

            try
            {
                bytes = await _runner.CaptureJpegAsync(
                    ffmpegPath,
                    rtspUrl,
                    camera.Transport,
                    _timeout,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch when (_playback is not null && _playback.IsPlaying(camera.Id))
            {
                await _playback.StopCameraAsync(camera.Id, ct).ConfigureAwait(false);
                try
                {
                    bytes = await _runner.CaptureJpegAsync(
                        ffmpegPath,
                        rtspUrl,
                        camera.Transport,
                        _timeout,
                        ct).ConfigureAwait(false);
                    usedFallback = true;
                }
                finally
                {
                    // Always resume the user's live tile even if the retry failed or the caller canceled.
                    await _playback.StartCameraAsync(camera.Id, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (bytes is null || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            {
                throw new InvalidOperationException("Captured snapshot is not a valid JPEG image.");
            }

            TryParseJpegDimensions(bytes, out int width, out int height);
            return new SnapshotResult(bytes, width, height, DateTimeOffset.UtcNow, usedFallback);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Parses image width and height from JPEG SOF0/SOF1/SOF2/SOF3 frame header markers.
    /// Sets both outputs to <c>0</c> and returns <c>false</c> if the marker is missing or truncated.
    /// </summary>
    public static bool TryParseJpegDimensions(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }

        int pos = 2;
        while (pos < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                return false;
            }

            while (pos < jpeg.Length && jpeg[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= jpeg.Length)
            {
                return false;
            }

            byte marker = jpeg[pos++];
            if (marker == 0x00)
            {
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return false;
            }

            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (pos + 2 > jpeg.Length)
            {
                return false;
            }

            int segmentLength = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (segmentLength < 2 || pos + segmentLength > jpeg.Length)
            {
                return false;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = (jpeg[pos + 3] << 8) | jpeg[pos + 4];
                width = (jpeg[pos + 5] << 8) | jpeg[pos + 6];
                return width > 0 && height > 0;
            }

            pos += segmentLength;
        }

        return false;
    }
}
