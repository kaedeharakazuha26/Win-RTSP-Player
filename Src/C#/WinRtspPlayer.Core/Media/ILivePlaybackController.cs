namespace WinRtspPlayer.Core.Media;

/// <summary>
/// Controls live RTSP playback tiles so background services (such as on-demand snapshot capture)
/// can inspect playing state or temporarily pause and resume single-connection cameras.
/// </summary>
public interface ILivePlaybackController
{
    bool IsPlaying(string cameraId);

    Task<bool> StartCameraAsync(string cameraId, CancellationToken ct = default);

    Task StopCameraAsync(string cameraId, CancellationToken ct = default);

    Task<bool> RestartCameraAsync(string cameraId, CancellationToken ct = default);
}
