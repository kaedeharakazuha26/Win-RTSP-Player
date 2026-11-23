using System.Text;
using WinRtspPlayer.Core.Config;
using Xunit;

namespace WinRtspPlayer.Tests;

public class CameraStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CameraStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinRtspPlayerTests_" + Guid.NewGuid().ToString("N"));
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
    public void SaveAndReload_EncryptsPasswordWithDpapiAndPreservesFields()
    {
        const string secretPassword = "S3cr3t!@#:password/%";
        var protector = new DpapiSecretProtector();
        var store1 = new CameraStore(_tempDir, protector);
        store1.LoadOrCreate();

        CameraConfig added = store1.AddCamera(new CameraAddRequest(
            Name: "Gate Cam",
            Ip: "192.168.1.50",
            Port: 8554,
            Path: "/h264/ch1",
            Username: "admin",
            Password: secretPassword,
            Transport: "udp",
            Slot: 3,
            HardDecode: true,
            AutoPlay: false));

        string jsonPath = Path.Combine(_tempDir, "config", "cameras.json");
        Assert.True(File.Exists(jsonPath));

        string rawJson = File.ReadAllText(jsonPath, Encoding.UTF8);
        Assert.DoesNotContain(secretPassword, rawJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(added.EncryptedPassword));

        var store2 = new CameraStore(_tempDir, new DpapiSecretProtector());
        AppConfig reloaded = store2.LoadOrCreate();

        CameraConfig cam = Assert.Single(reloaded.Cameras);
        Assert.Equal(added.Id, cam.Id);
        Assert.Equal(3, cam.Slot);
        Assert.Equal("Gate Cam", cam.Name);
        Assert.Equal("192.168.1.50", cam.Ip);
        Assert.Equal(8554, cam.Port);
        Assert.Equal("/h264/ch1", cam.Path);
        Assert.Equal("admin", cam.Username);
        Assert.Equal("udp", cam.Transport);
        Assert.True(cam.HardDecode);
        Assert.False(cam.AutoPlay);
        Assert.Equal(secretPassword, store2.GetPlaintextPassword(cam));
        string builtUrl = store2.BuildRtspUrl(cam);
        Assert.Equal(
            "rtsp://admin:S3cr3t%21%40%23%3Apassword%2F%25@192.168.1.50:8554/h264/ch1",
            builtUrl);
        Assert.True(RtspUrlCodec.TryParseUrl(builtUrl, out ParsedRtspUrl? parsed, out _));
        Assert.Equal(secretPassword, parsed!.Password);
    }

    [Fact]
    public void ListCameras_NeverExposesPasswordAndSetsHasPasswordFlag()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();

        CameraConfig withPass = store.AddCamera(new CameraAddRequest(
            Name: "WithPass",
            Ip: "192.168.1.10",
            Username: "admin",
            Password: "mySecretPassword123"));

        CameraConfig withoutPass = store.AddCamera(new CameraAddRequest(
            Name: "NoPass",
            Ip: "192.168.1.11"));

        IReadOnlyList<CameraView> views = store.ListCameras(id => id == withPass.Id);

        Assert.Equal(2, views.Count);

        CameraView v1 = Assert.Single(views, v => v.Id == withPass.Id);
        Assert.True(v1.HasPassword);
        Assert.True(v1.IsPlaying);
        Assert.DoesNotContain("mySecretPassword123", v1.ToString(), StringComparison.Ordinal);

        CameraView v2 = Assert.Single(views, v => v.Id == withoutPass.Id);
        Assert.False(v2.HasPassword);
        Assert.False(v2.IsPlaying);

        // Verify CameraView type itself defines no Password property.
        Assert.Null(typeof(CameraView).GetProperty("Password"));
        Assert.Null(typeof(CameraView).GetProperty("EncryptedPassword"));
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacyRtspTxtWithLfAndCrLfLines()
    {
        string legacyPath = Path.Combine(_tempDir, "rtsp.txt");
        string content =
            "# comment line\r\n" +
            "\r\n" +
            "  rtsp://admin:pass1@192.168.1.10/stream1  \r\n" +
            "\n" +
            "rtsp://192.168.1.11:8554/live\n" +
            "   \n" +
            "not-an-rtsp-url\r\n";
        File.WriteAllText(legacyPath, content, Encoding.UTF8);

        string originalCwd = Environment.CurrentDirectory;
        string otherCwd = Path.Combine(Path.GetTempPath(), "WinRtspPlayerOtherCwd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherCwd);

        try
        {
            Environment.CurrentDirectory = otherCwd;

            var store = new CameraStore(_tempDir, new DpapiSecretProtector());
            AppConfig config = store.LoadOrCreate();

            Assert.Equal(2, config.Cameras.Count);
            Assert.Equal(1, config.Cameras[0].Slot);
            Assert.Equal("192.168.1.10", config.Cameras[0].Ip);
            Assert.Equal(554, config.Cameras[0].Port);
            Assert.Equal("/stream1", config.Cameras[0].Path);
            Assert.Equal("admin", config.Cameras[0].Username);
            Assert.Equal("pass1", store.GetPlaintextPassword(config.Cameras[0]));

            Assert.Equal(2, config.Cameras[1].Slot);
            Assert.Equal("192.168.1.11", config.Cameras[1].Ip);
            Assert.Equal(8554, config.Cameras[1].Port);
            Assert.Equal("/live", config.Cameras[1].Path);
            Assert.Equal(string.Empty, store.GetPlaintextPassword(config.Cameras[1]));

            Assert.True(File.Exists(Path.Combine(_tempDir, "config", "cameras.json")));
            Assert.False(File.Exists(Path.Combine(otherCwd, "config", "cameras.json")));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            if (Directory.Exists(otherCwd))
            {
                Directory.Delete(otherCwd, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadOrCreate_BacksUpCorruptJsonInsteadOfCrashing()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string configPath = Path.Combine(configDir, "cameras.json");
        File.WriteAllText(configPath, "{ \"GridLayout\": 4, \"Cameras\": [ { \"Id\": ", Encoding.UTF8);

        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        AppConfig config = store.LoadOrCreate();

        Assert.NotNull(config);
        Assert.Equal(4, config.GridLayout);
        Assert.Empty(config.Cameras);

        string[] backups = Directory.GetFiles(configDir, "cameras.json.corrupt.*");
        Assert.Single(backups);
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public void LoadOrCreate_BacksUpCorruptConfig_WhenCamerasArrayContainsNullElement()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string configPath = Path.Combine(configDir, "cameras.json");
        File.WriteAllText(
            configPath,
            """
            {
              "GridLayout": 9,
              "Cameras": [
                { "Id": "cam1", "Slot": 1, "Name": "Valid", "Ip": "192.168.1.10" },
                null
              ]
            }
            """,
            Encoding.UTF8);

        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        AppConfig config = store.LoadOrCreate();

        Assert.NotNull(config);
        Assert.Equal(4, config.GridLayout);
        Assert.Empty(config.Cameras);

        string[] backups = Directory.GetFiles(configDir, "cameras.json.corrupt.*");
        Assert.Single(backups);
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public void LoadOrCreate_DeduplicatesAndNormalizesOutOfRangeSlotsUpToMaxSlots()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string configPath = Path.Combine(configDir, "cameras.json");

        var camerasJson = new StringBuilder();
        camerasJson.Append("""{ "GridLayout": 4, "Cameras": [""");
        camerasJson.Append("""{ "Id": "c1", "Slot": 2, "Name": "ValidSlot2", "Ip": "192.168.1.1" },""");
        camerasJson.Append("""{ "Id": "c2", "Slot": 2, "Name": "DupSlot2", "Ip": "192.168.1.2" },""");
        camerasJson.Append("""{ "Id": "c3", "Slot": 0, "Name": "ZeroSlot", "Ip": "192.168.1.3" },""");
        camerasJson.Append("""{ "Id": "c4", "Slot": 99, "Name": "HighSlot", "Ip": "192.168.1.4" }""");
        for (int i = 5; i <= 18; i++)
        {
            camerasJson.Append($$""",{ "Id": "c{{i}}", "Slot": {{i}}, "Name": "Cam{{i}}", "Ip": "192.168.1.{{i}}" }""");
        }
        camerasJson.Append("]}");

        File.WriteAllText(configPath, camerasJson.ToString(), Encoding.UTF8);

        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        AppConfig config = store.LoadOrCreate();

        Assert.Equal(CameraStore.MaxSlots, config.Cameras.Count);
        Assert.Equal(CameraStore.MaxSlots, config.Cameras.Select(c => c.Slot).Distinct().Count());
        Assert.All(config.Cameras, c => Assert.InRange(c.Slot, 1, CameraStore.MaxSlots));
        Assert.Equal(2, Assert.Single(config.Cameras, c => c.Id == "c1").Slot);
        Assert.Equal(1, Assert.Single(config.Cameras, c => c.Id == "c2").Slot);
        Assert.Equal(3, Assert.Single(config.Cameras, c => c.Id == "c3").Slot);
        Assert.Equal(4, Assert.Single(config.Cameras, c => c.Id == "c4").Slot);
    }

    [Fact]
    public void UpdateCamera_FiresCameraChangedAndUpdatesOnlySpecifiedFields()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();

        var events = new List<CameraChangeEvent>();
        store.CameraChanged += e => events.Add(e);

        CameraConfig created = store.AddCamera(new CameraAddRequest(
            Name: "Door",
            Ip: "10.0.0.1",
            Port: 554,
            Path: "/ch0",
            Username: "user1",
            Password: "oldPassword",
            Transport: "tcp",
            HardDecode: false,
            AutoPlay: true));

        CameraConfig updated = store.UpdateCamera(created.Id, new CameraUpdateRequest(
            Name: "Front Door",
            Port: 8554));

        Assert.Equal("Front Door", updated.Name);
        Assert.Equal("10.0.0.1", updated.Ip);
        Assert.Equal(8554, updated.Port);
        Assert.Equal("/ch0", updated.Path);
        Assert.Equal("user1", updated.Username);
        Assert.Equal("oldPassword", store.GetPlaintextPassword(updated));
        Assert.Equal("tcp", updated.Transport);
        Assert.False(updated.HardDecode);
        Assert.True(updated.AutoPlay);

        CameraConfig cleared = store.UpdateCamera(created.Id, new CameraUpdateRequest(
            ClearPassword: true));
        Assert.Equal(string.Empty, store.GetPlaintextPassword(cleared));

        Assert.Equal(3, events.Count);
        Assert.Equal(CameraChangeKind.Added, events[0].Kind);
        Assert.Equal(CameraChangeKind.Updated, events[1].Kind);
        Assert.Equal("Front Door", events[1].Camera.Name);
        Assert.Equal(CameraChangeKind.Updated, events[2].Kind);
    }

    [Fact]
    public void FindCamera_MatchesByIdSlotOrCaseInsensitiveName()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();

        CameraConfig cam = store.AddCamera(new CameraAddRequest(
            Name: "Lobby Camera",
            Ip: "192.168.1.20",
            Slot: 5));

        CameraConfig? byId = store.FindCamera(cam.Id);
        CameraConfig? bySlot = store.FindCamera("5");
        CameraConfig? byName = store.FindCamera("lobby camera");
        CameraConfig? missing = store.FindCamera("nonexistent");

        Assert.NotNull(byId);
        Assert.NotNull(bySlot);
        Assert.NotNull(byName);
        Assert.Equal(cam.Id, byId.Id);
        Assert.Equal(cam.Id, bySlot.Id);
        Assert.Equal(cam.Id, byName.Id);
        Assert.Null(missing);
    }

    [Fact]
    public void RemoveCamera_AndUpdateSettings_PersistAndValidate()
    {
        var store = new CameraStore(_tempDir, new DpapiSecretProtector());
        store.LoadOrCreate();

        var events = new List<CameraChangeEvent>();
        store.CameraChanged += e => events.Add(e);

        CameraConfig cam = store.AddCamera(new CameraAddRequest("Cam1", "192.168.1.30"));
        Assert.True(store.RemoveCamera("1"));
        Assert.False(store.RemoveCamera("1"));
        Assert.Empty(store.ListCameras());

        Assert.Equal(2, events.Count);
        Assert.Equal(CameraChangeKind.Removed, events[1].Kind);

        store.UpdateSettings(cfg =>
        {
            cfg.GridLayout = 9;
            cfg.McpPort = 9000;
        });

        var reloaded = new CameraStore(_tempDir, new DpapiSecretProtector());
        AppConfig snap = reloaded.LoadOrCreate();
        Assert.Equal(9, snap.GridLayout);
        Assert.Equal(9000, snap.McpPort);

        Assert.Throws<ArgumentException>(() => store.UpdateSettings(cfg => cfg.GridLayout = 5));
        Assert.Throws<ArgumentException>(() => store.UpdateSettings(cfg => cfg.McpPort = 70000));
    }
}
