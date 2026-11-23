namespace WinRtspPlayer.Core.Config;

/// <summary>
/// Persisted configuration for a single RTSP camera channel.
/// Passwords are stored exclusively as DPAPI-protected Base64 strings in <see cref="EncryptedPassword"/>.
/// </summary>
public sealed class CameraConfig
{
    public string Id { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 554;
    public string Path { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public string Transport { get; set; } = "tcp";
    public bool HardDecode { get; set; }
    public bool AutoPlay { get; set; } = true;

    public CameraConfig Clone() => new()
    {
        Id = Id,
        Slot = Slot,
        Name = Name,
        Ip = Ip,
        Port = Port,
        Path = Path,
        Username = Username,
        EncryptedPassword = EncryptedPassword,
        Transport = Transport,
        HardDecode = HardDecode,
        AutoPlay = AutoPlay
    };

    public override string ToString() =>
        $"CameraConfig {{ Id = {Id}, Slot = {Slot}, Name = {Name}, Ip = {Ip}, Port = {Port}, Transport = {Transport} }}";
}

/// <summary>
/// Sanitized, read-only view of a camera returned to MCP clients and UI callers.
/// Never contains plaintext or encrypted passwords.
/// </summary>
public sealed record CameraView(
    string Id,
    int Slot,
    string Name,
    string Ip,
    int Port,
    string Path,
    string Username,
    bool HasPassword,
    string Transport,
    bool HardDecode,
    bool AutoPlay,
    bool IsPlaying);

/// <summary>
/// Root persisted application configuration stored in config/cameras.json.
/// </summary>
public sealed class AppConfig
{
    public int GridLayout { get; set; } = 4;
    public string RenderFormat { get; set; } = "RGB24_GDI";
    public int FrameCache { get; set; } = 3;
    public bool ShownToScale { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int McpPort { get; set; } = 8765;
    public string? McpPublicBaseUrl { get; set; }
    public List<CameraConfig> Cameras { get; set; } = new();

    public AppConfig Clone() => new()
    {
        GridLayout = GridLayout,
        RenderFormat = RenderFormat,
        FrameCache = FrameCache,
        ShownToScale = ShownToScale,
        FfmpegPath = FfmpegPath,
        McpPort = McpPort,
        McpPublicBaseUrl = McpPublicBaseUrl,
        Cameras = Cameras.Select(c => c.Clone()).ToList()
    };
}

public sealed record CameraAddRequest(
    string Name,
    string Ip,
    int? Port = null,
    string? Path = null,
    string? Username = null,
    string? Password = null,
    string? Transport = null,
    int? Slot = null,
    bool? HardDecode = null,
    bool? AutoPlay = null)
{
    public override string ToString() =>
        $"CameraAddRequest {{ Name = {Name}, Ip = {Ip}, Port = {Port}, Path = {Path}, Username = {Username}, HasPassword = {!string.IsNullOrEmpty(Password)}, Transport = {Transport}, Slot = {Slot} }}";
}

public sealed record CameraUpdateRequest(
    string? Name = null,
    string? Ip = null,
    int? Port = null,
    string? Path = null,
    string? Username = null,
    string? Password = null,
    bool ClearPassword = false,
    string? Transport = null,
    int? Slot = null,
    bool? HardDecode = null,
    bool? AutoPlay = null)
{
    public override string ToString() =>
        $"CameraUpdateRequest {{ Name = {Name}, Ip = {Ip}, Port = {Port}, Path = {Path}, Username = {Username}, HasPassword = {!string.IsNullOrEmpty(Password)}, ClearPassword = {ClearPassword}, Transport = {Transport}, Slot = {Slot} }}";
}

public enum CameraChangeKind
{
    Added,
    Updated,
    Removed
}

public sealed record CameraChangeEvent(CameraChangeKind Kind, CameraConfig Camera);

public sealed record ParsedRtspUrl(
    string Ip,
    int Port,
    string Path,
    string Username,
    string Password)
{
    public override string ToString() =>
        $"ParsedRtspUrl {{ Ip = {Ip}, Port = {Port}, Path = {Path}, Username = {Username}, HasPassword = {!string.IsNullOrEmpty(Password)} }}";
}
