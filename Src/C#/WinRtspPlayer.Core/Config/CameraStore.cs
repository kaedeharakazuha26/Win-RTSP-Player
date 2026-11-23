using System.Text;
using System.Text.Json;

namespace WinRtspPlayer.Core.Config;

/// <summary>
/// Thread-safe, DPAPI-backed persistent store for camera configurations and application settings.
/// Resolves all paths strictly relative to the configured base directory.
/// </summary>
public sealed class CameraStore
{
    public const int MaxSlots = 16;

    private static readonly int[] ValidGridLayouts = [1, 4, 6, 9, 16];
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseDirectory;
    private readonly string _configPath;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();
    private AppConfig _config = new();

    public event Action<CameraChangeEvent>? CameraChanged;

    public CameraStore(string baseDirectory, ISecretProtector protector)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory must not be empty.", nameof(baseDirectory));
        }

        _baseDirectory = Path.GetFullPath(baseDirectory);
        _configPath = Path.Combine(_baseDirectory, "config", "cameras.json");
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public AppConfig LoadOrCreate(string? legacyRtspTxtPath = null)
    {
        lock (_lock)
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath, Utf8NoBom);
                    AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                    if (loaded is null)
                    {
                        BackupCorruptConfigLocked();
                        _config = new AppConfig();
                        SaveAtomicLocked();
                    }
                    else
                    {
                        NormalizeLoadedConfig(loaded);
                        _config = loaded;
                    }
                }
                catch (JsonException)
                {
                    BackupCorruptConfigLocked();
                    _config = new AppConfig();
                    SaveAtomicLocked();
                }

                return _config.Clone();
            }

            _config = new AppConfig();
            string rtspTxt = string.IsNullOrWhiteSpace(legacyRtspTxtPath)
                ? Path.Combine(_baseDirectory, "rtsp.txt")
                : legacyRtspTxtPath;

            if (File.Exists(rtspTxt))
            {
                ImportLegacyRtspTxtLocked(rtspTxt);
            }

            SaveAtomicLocked();
            return _config.Clone();
        }
    }

    public AppConfig GetConfigSnapshot()
    {
        lock (_lock)
        {
            return _config.Clone();
        }
    }

    public IReadOnlyList<CameraView> ListCameras(Func<string, bool>? isPlayingLookup = null)
    {
        List<CameraConfig> snapshot;
        lock (_lock)
        {
            snapshot = _config.Cameras
                .OrderBy(c => c.Slot)
                .Select(c => c.Clone())
                .ToList();
        }

        return snapshot
            .Select(c => new CameraView(
                Id: c.Id,
                Slot: c.Slot,
                Name: c.Name,
                Ip: c.Ip,
                Port: c.Port,
                Path: c.Path,
                Username: c.Username,
                HasPassword: !string.IsNullOrEmpty(c.EncryptedPassword),
                Transport: c.Transport,
                HardDecode: c.HardDecode,
                AutoPlay: c.AutoPlay,
                IsPlaying: isPlayingLookup?.Invoke(c.Id) ?? false))
            .ToList();
    }

    public CameraConfig? FindCamera(string idOrSlotOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrSlotOrName))
        {
            return null;
        }

        lock (_lock)
        {
            return FindCameraLocked(idOrSlotOrName)?.Clone();
        }
    }

    public string GetPlaintextPassword(CameraConfig cam)
    {
        ArgumentNullException.ThrowIfNull(cam);
        return string.IsNullOrEmpty(cam.EncryptedPassword)
            ? string.Empty
            : _protector.Unprotect(cam.EncryptedPassword);
    }

    public string BuildRtspUrl(CameraConfig cam)
    {
        ArgumentNullException.ThrowIfNull(cam);
        string password = GetPlaintextPassword(cam);
        return RtspUrlCodec.BuildUrl(cam, password);
    }

    public CameraConfig AddCamera(CameraAddRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        string name = req.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Camera name must not be empty.", nameof(req));
        }

        string ip = req.Ip?.Trim() ?? string.Empty;
        int port = req.Port ?? 554;
        string path = req.Path?.Trim() ?? string.Empty;
        string transport = string.IsNullOrWhiteSpace(req.Transport)
            ? "tcp"
            : req.Transport.Trim().ToLowerInvariant();

        string? validationError = RtspUrlCodec.ValidateCameraInput(ip, port, path, transport);
        if (validationError is not null)
        {
            throw new ArgumentException(validationError, nameof(req));
        }

        CameraConfig snapshot;
        lock (_lock)
        {
            int slot;
            if (req.Slot.HasValue)
            {
                if (req.Slot.Value is < 1 or > MaxSlots)
                {
                    throw new ArgumentOutOfRangeException(nameof(req), $"Slot must be between 1 and {MaxSlots}.");
                }

                if (_config.Cameras.Any(c => c.Slot == req.Slot.Value))
                {
                    throw new InvalidOperationException($"Slot {req.Slot.Value} is already occupied.");
                }

                slot = req.Slot.Value;
            }
            else
            {
                slot = FindNextFreeSlotLocked()
                    ?? throw new InvalidOperationException($"All {MaxSlots} camera slots are occupied.");
            }

            var cam = new CameraConfig
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Slot = slot,
                Name = name,
                Ip = ip,
                Port = port,
                Path = path,
                Username = req.Username ?? string.Empty,
                EncryptedPassword = string.IsNullOrEmpty(req.Password)
                    ? string.Empty
                    : _protector.Protect(req.Password),
                Transport = transport,
                HardDecode = req.HardDecode ?? false,
                AutoPlay = req.AutoPlay ?? true
            };

            _config.Cameras.Add(cam);
            _config.Cameras.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            SaveAtomicLocked();
            snapshot = cam.Clone();
        }

        CameraChanged?.Invoke(new CameraChangeEvent(CameraChangeKind.Added, snapshot));
        return snapshot;
    }

    public CameraConfig UpdateCamera(string idOrSlotOrName, CameraUpdateRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        CameraConfig snapshot;
        lock (_lock)
        {
            CameraConfig existing = FindCameraLocked(idOrSlotOrName)
                ?? throw new KeyNotFoundException($"Camera '{idOrSlotOrName}' was not found.");

            string name = req.Name is not null ? req.Name.Trim() : existing.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Camera name must not be empty.", nameof(req));
            }

            string ip = req.Ip is not null ? req.Ip.Trim() : existing.Ip;
            int port = req.Port ?? existing.Port;
            string path = req.Path is not null ? req.Path.Trim() : existing.Path;
            string transport = req.Transport is not null
                ? req.Transport.Trim().ToLowerInvariant()
                : existing.Transport;

            string? validationError = RtspUrlCodec.ValidateCameraInput(ip, port, path, transport);
            if (validationError is not null)
            {
                throw new ArgumentException(validationError, nameof(req));
            }

            int slot = existing.Slot;
            if (req.Slot.HasValue)
            {
                if (req.Slot.Value is < 1 or > MaxSlots)
                {
                    throw new ArgumentOutOfRangeException(nameof(req), $"Slot must be between 1 and {MaxSlots}.");
                }

                if (_config.Cameras.Any(c => c.Id != existing.Id && c.Slot == req.Slot.Value))
                {
                    throw new InvalidOperationException($"Slot {req.Slot.Value} is already occupied.");
                }

                slot = req.Slot.Value;
            }

            string encryptedPassword = existing.EncryptedPassword;
            if (req.ClearPassword)
            {
                encryptedPassword = string.Empty;
            }
            else if (req.Password is not null)
            {
                encryptedPassword = string.IsNullOrEmpty(req.Password)
                    ? string.Empty
                    : _protector.Protect(req.Password);
            }

            existing.Name = name;
            existing.Ip = ip;
            existing.Port = port;
            existing.Path = path;
            existing.Username = req.Username ?? existing.Username;
            existing.EncryptedPassword = encryptedPassword;
            existing.Transport = transport;
            existing.Slot = slot;
            existing.HardDecode = req.HardDecode ?? existing.HardDecode;
            existing.AutoPlay = req.AutoPlay ?? existing.AutoPlay;

            _config.Cameras.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            SaveAtomicLocked();
            snapshot = existing.Clone();
        }

        CameraChanged?.Invoke(new CameraChangeEvent(CameraChangeKind.Updated, snapshot));
        return snapshot;
    }

    public bool RemoveCamera(string idOrSlotOrName)
    {
        CameraConfig? removedSnapshot;
        lock (_lock)
        {
            CameraConfig? existing = FindCameraLocked(idOrSlotOrName);
            if (existing is null)
            {
                return false;
            }

            _config.Cameras.Remove(existing);
            SaveAtomicLocked();
            removedSnapshot = existing.Clone();
        }

        CameraChanged?.Invoke(new CameraChangeEvent(CameraChangeKind.Removed, removedSnapshot));
        return true;
    }

    public void UpdateSettings(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_lock)
        {
            AppConfig candidate = _config.Clone();
            mutate(candidate);

            if (Array.IndexOf(ValidGridLayouts, candidate.GridLayout) < 0)
            {
                throw new ArgumentException(
                    $"GridLayout must be one of {string.Join(", ", ValidGridLayouts)} (got {candidate.GridLayout}).",
                    nameof(mutate));
            }

            if (candidate.McpPort is < 1 or > 65535)
            {
                throw new ArgumentException(
                    $"McpPort must be between 1 and 65535 (got {candidate.McpPort}).",
                    nameof(mutate));
            }

            if (candidate.FrameCache < 1)
            {
                candidate.FrameCache = 1;
            }

            if (string.IsNullOrWhiteSpace(candidate.RenderFormat))
            {
                candidate.RenderFormat = "RGB24_GDI";
            }

            if (string.IsNullOrWhiteSpace(candidate.FfmpegPath))
            {
                candidate.FfmpegPath = "ffmpeg";
            }

            candidate.Cameras ??= new List<CameraConfig>();
            _config = candidate;
            SaveAtomicLocked();
        }
    }

    private CameraConfig? FindCameraLocked(string idOrSlotOrName)
    {
        string key = idOrSlotOrName.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        CameraConfig? byId = _config.Cameras.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.Ordinal));
        if (byId is not null)
        {
            return byId;
        }

        if (int.TryParse(key, out int slot) && slot is >= 1 and <= MaxSlots)
        {
            CameraConfig? bySlot = _config.Cameras.FirstOrDefault(c => c.Slot == slot);
            if (bySlot is not null)
            {
                return bySlot;
            }
        }

        return _config.Cameras.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private int? FindNextFreeSlotLocked()
    {
        for (int slot = 1; slot <= MaxSlots; slot++)
        {
            if (_config.Cameras.All(c => c.Slot != slot))
            {
                return slot;
            }
        }

        return null;
    }

    private void ImportLegacyRtspTxtLocked(string rtspTxtPath)
    {
        string content = File.ReadAllText(rtspTxtPath, Encoding.UTF8);
        string[] lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (!RtspUrlCodec.TryParseUrl(line, out ParsedRtspUrl? parsed, out _) || parsed is null)
            {
                continue;
            }

            if (RtspUrlCodec.ValidateCameraInput(parsed.Ip, parsed.Port, parsed.Path, "tcp") is not null)
            {
                continue;
            }

            int? nextSlot = FindNextFreeSlotLocked();
            if (!nextSlot.HasValue)
            {
                break;
            }

            _config.Cameras.Add(new CameraConfig
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Slot = nextSlot.Value,
                Name = $"Camera {nextSlot.Value}",
                Ip = parsed.Ip,
                Port = parsed.Port,
                Path = parsed.Path,
                Username = parsed.Username,
                EncryptedPassword = string.IsNullOrEmpty(parsed.Password)
                    ? string.Empty
                    : _protector.Protect(parsed.Password),
                Transport = "tcp",
                HardDecode = false,
                AutoPlay = true
            });
        }
    }

    private void BackupCorruptConfigLocked()
    {
        if (!File.Exists(_configPath))
        {
            return;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        string backupPath = $"{_configPath}.corrupt.{stamp}";
        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{_configPath}.corrupt.{stamp}.{suffix++}";
        }

        File.Move(_configPath, backupPath);
    }

    private void SaveAtomicLocked()
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = _configPath + ".tmp";
        string json = JsonSerializer.Serialize(_config, JsonOptions);
        File.WriteAllText(tempPath, json, Utf8NoBom);
        File.Move(tempPath, _configPath, overwrite: true);
    }

    private static void NormalizeLoadedConfig(AppConfig config)
    {
        if (Array.IndexOf(ValidGridLayouts, config.GridLayout) < 0)
        {
            config.GridLayout = 4;
        }

        if (config.McpPort is < 1 or > 65535)
        {
            config.McpPort = 8765;
        }

        if (config.FrameCache < 1)
        {
            config.FrameCache = 3;
        }

        if (string.IsNullOrWhiteSpace(config.RenderFormat))
        {
            config.RenderFormat = "RGB24_GDI";
        }

        if (string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            config.FfmpegPath = "ffmpeg";
        }

        config.Cameras ??= new List<CameraConfig>();
        foreach (CameraConfig cam in config.Cameras)
        {
            if (string.IsNullOrWhiteSpace(cam.Id))
            {
                cam.Id = Guid.NewGuid().ToString("N")[..12];
            }

            if (cam.Port is < 1 or > 65535)
            {
                cam.Port = 554;
            }

            cam.Path ??= string.Empty;
            cam.Username ??= string.Empty;
            cam.EncryptedPassword ??= string.Empty;
            cam.Transport = string.Equals(cam.Transport, "udp", StringComparison.OrdinalIgnoreCase)
                ? "udp"
                : "tcp";
        }

        config.Cameras.Sort((a, b) => a.Slot.CompareTo(b.Slot));
    }
}
