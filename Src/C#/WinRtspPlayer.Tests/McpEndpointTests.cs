using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Mcp;
using WinRtspPlayer.Core.Media;
using Xunit;

namespace WinRtspPlayer.Tests;

public sealed class McpEndpointTests : IDisposable
{
    private readonly string _tempDir;

    public McpEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinRtspMcpTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task McpEndpoint_WithoutBearerToken_Returns401WithResourceMetadataHeader()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, _) = CreateDependencies();
        store.UpdateSettings(cfg => cfg.McpPublicBaseUrl = "https://cam.example.com/");
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback: null);

        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete })
        {
            using var req = new HttpRequestMessage(method, "/mcp");
            if (method == HttpMethod.Post)
            {
                req.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json");
            }

            HttpResponseMessage res = await host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            AssertProtectedResourceHeader(res, "https://cam.example.com/.well-known/oauth-protected-resource");
        }

        // Invalid and empty Bearer tokens also return 401 with WWW-Authenticate
        using var badBearer = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        badBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        badBearer.Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json");
        HttpResponseMessage badRes = await host.Client.SendAsync(badBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, badRes.StatusCode);
        AssertProtectedResourceHeader(badRes, "https://cam.example.com/.well-known/oauth-protected-resource");

        using var basicAuth = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        basicAuth.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("a:b")));
        HttpResponseMessage basicRes = await host.Client.SendAsync(basicAuth);
        Assert.Equal(HttpStatusCode.Unauthorized, basicRes.StatusCode);
        AssertProtectedResourceHeader(basicRes, "https://cam.example.com/.well-known/oauth-protected-resource");
    }

    [Fact]
    public async Task McpEndpoint_InitializeAndToolsList_ReturnsAllSixToolsAndSessionHeader()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, _) = CreateDependencies();
        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback: null);

        // 1. initialize echoes supported protocolVersion and issues MCP-Session-Id
        HttpResponseMessage initRes = await SendJsonRpcAsync(
            host.Client,
            token,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "gemini-test", version = "1.0" }
                }
            });

        Assert.Equal(HttpStatusCode.OK, initRes.StatusCode);
        Assert.True(initRes.Headers.TryGetValues("MCP-Session-Id", out IEnumerable<string>? sessionValues));
        string sessionId = Assert.Single(sessionValues);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        JsonObject initJson = await ReadJsonObjectAsync(initRes);
        Assert.Equal("2.0", initJson["jsonrpc"]?.GetValue<string>());
        Assert.Equal(1, initJson["id"]?.GetValue<int>());
        JsonObject initResult = initJson["result"]!.AsObject();
        Assert.Equal("2025-03-26", initResult["protocolVersion"]?.GetValue<string>());
        Assert.NotNull(initResult["capabilities"]?["tools"]);
        Assert.Equal("win-rtsp-player", initResult["serverInfo"]?["name"]?.GetValue<string>());
        Assert.Equal("1.0.0", initResult["serverInfo"]?["version"]?.GetValue<string>());

        // Unsupported protocol version falls back to 2025-06-18
        HttpResponseMessage fallbackInit = await SendJsonRpcAsync(
            host.Client,
            token,
            new
            {
                jsonrpc = "2.0",
                id = "init-2",
                method = "initialize",
                @params = new { protocolVersion = "1999-01-01" }
            });
        Assert.Equal(HttpStatusCode.OK, fallbackInit.StatusCode);
        JsonObject fallbackJson = await ReadJsonObjectAsync(fallbackInit);
        Assert.Equal("init-2", fallbackJson["id"]?.GetValue<string>());
        Assert.Equal("2025-06-18", fallbackJson["result"]?["protocolVersion"]?.GetValue<string>());

        // 2. notifications/initialized returns 202 Accepted and echoes MCP-Session-Id
        HttpResponseMessage notifRes = await SendJsonRpcAsync(
            host.Client,
            token,
            new { jsonrpc = "2.0", method = "notifications/initialized" },
            sessionId);
        Assert.Equal(HttpStatusCode.Accepted, notifRes.StatusCode);
        Assert.Equal(string.Empty, await notifRes.Content.ReadAsStringAsync());
        Assert.Equal(sessionId, Assert.Single(notifRes.Headers.GetValues("MCP-Session-Id")));

        // 3. ping returns empty object result
        HttpResponseMessage pingRes = await SendJsonRpcAsync(
            host.Client,
            token,
            new { jsonrpc = "2.0", id = 2, method = "ping" },
            sessionId);
        Assert.Equal(HttpStatusCode.OK, pingRes.StatusCode);
        Assert.Equal(sessionId, Assert.Single(pingRes.Headers.GetValues("MCP-Session-Id")));
        JsonObject pingJson = await ReadJsonObjectAsync(pingRes);
        Assert.Empty(pingJson["result"]!.AsObject());

        // 4. tools/list returns all 6 expected tools with schemas
        HttpResponseMessage listRes = await SendJsonRpcAsync(
            host.Client,
            token,
            new { jsonrpc = "2.0", id = 3, method = "tools/list" },
            sessionId);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        Assert.Equal(sessionId, Assert.Single(listRes.Headers.GetValues("MCP-Session-Id")));

        JsonObject listJson = await ReadJsonObjectAsync(listRes);
        JsonArray tools = listJson["result"]!["tools"]!.AsArray();
        string[] toolNames = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(
            [
                "list_cameras",
                "get_camera_screenshot",
                "update_camera",
                "add_camera",
                "remove_camera",
                "control_camera"
            ],
            toolNames);

        JsonObject updateTool = tools.Single(t => t!["name"]!.GetValue<string>() == "update_camera")!.AsObject();
        Assert.Equal(["camera"], updateTool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.NotNull(updateTool["inputSchema"]!["properties"]!["clear_password"]);

        JsonObject controlTool = tools.Single(t => t!["name"]!.GetValue<string>() == "control_camera")!.AsObject();
        Assert.Equal(
            ["start", "stop", "restart"],
            controlTool["inputSchema"]!["properties"]!["action"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task McpEndpoint_ListCameras_NeverReturnsPasswords()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, FakeLivePlaybackController playback) = CreateDependencies();
        const string camSecret = "TopSecretCamPass!#123";
        CameraConfig cam1 = store.AddCamera(new CameraAddRequest(
            Name: "Front Gate",
            Ip: "192.168.1.10",
            Port: 554,
            Path: "/stream1",
            Username: "admin",
            Password: camSecret,
            Transport: "tcp",
            HardDecode: true,
            AutoPlay: true));
        CameraConfig cam2 = store.AddCamera(new CameraAddRequest(
            Name: "Garage",
            Ip: "192.168.1.11",
            Port: 8554,
            Path: "/live",
            Transport: "udp",
            AutoPlay: false));
        playback.SetPlaying(cam1.Id, true);

        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback);

        HttpResponseMessage res = await CallToolAsync(host.Client, token, "list_cameras", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        string rawBody = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(camSecret, rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain(cam1.EncryptedPassword, rawBody, StringComparison.Ordinal);

        JsonObject payload = ExtractToolJsonPayload(rawBody);
        Assert.Equal(4, payload["grid_layout"]?.GetValue<int>());

        JsonArray cameras = payload["cameras"]!.AsArray();
        Assert.Equal(2, cameras.Count);

        JsonObject first = cameras[0]!.AsObject();
        Assert.Equal(cam1.Id, first["id"]?.GetValue<string>());
        Assert.Equal(1, first["slot"]?.GetValue<int>());
        Assert.Equal("Front Gate", first["name"]?.GetValue<string>());
        Assert.Equal("192.168.1.10", first["ip"]?.GetValue<string>());
        Assert.Equal(554, first["port"]?.GetValue<int>());
        Assert.Equal("/stream1", first["path"]?.GetValue<string>());
        Assert.Equal("admin", first["username"]?.GetValue<string>());
        Assert.True(first["has_password"]?.GetValue<bool>());
        Assert.Equal("tcp", first["transport"]?.GetValue<string>());
        Assert.True(first["hard_decode"]?.GetValue<bool>());
        Assert.True(first["auto_play"]?.GetValue<bool>());
        Assert.True(first["is_playing"]?.GetValue<bool>());
        Assert.False(first.ContainsKey("password"));
        Assert.False(first.ContainsKey("encrypted_password"));

        JsonObject second = cameras[1]!.AsObject();
        Assert.Equal(cam2.Id, second["id"]?.GetValue<string>());
        Assert.False(second["has_password"]?.GetValue<bool>());
        Assert.False(second["is_playing"]?.GetValue<bool>());
    }

    [Fact]
    public async Task McpEndpoint_AddUpdateRemoveAndControlCamera_PersistsAndCallsPlaybackController()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, FakeLivePlaybackController playback) = CreateDependencies();
        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback);

        // 1. add_camera
        const string initialSecret = "InitialPass@99";
        HttpResponseMessage addRes = await CallToolAsync(host.Client, token, "add_camera", new
        {
            name = "Lobby",
            ip = "192.168.1.25",
            port = 8554,
            path = "/h264",
            username = "camuser",
            password = initialSecret,
            transport = "udp",
            slot = 2,
            auto_play = true,
            hard_decode = true
        });
        Assert.Equal(HttpStatusCode.OK, addRes.StatusCode);
        string addRaw = await addRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(initialSecret, addRaw, StringComparison.Ordinal);

        JsonObject addedPayload = ExtractToolJsonPayload(addRaw);
        string camId = addedPayload["id"]!.GetValue<string>();
        Assert.Equal(2, addedPayload["slot"]?.GetValue<int>());
        Assert.Equal("Lobby", addedPayload["name"]?.GetValue<string>());
        Assert.Equal("192.168.1.25", addedPayload["ip"]?.GetValue<string>());
        Assert.True(addedPayload["has_password"]?.GetValue<bool>());
        Assert.True(addedPayload["hard_decode"]?.GetValue<bool>());
        Assert.Empty(playback.Events); // Pre-flight ruling: add_camera does NOT directly call playback controller

        CameraConfig? storedAfterAdd = store.FindCamera(camId);
        Assert.NotNull(storedAfterAdd);
        Assert.Equal(initialSecret, store.GetPlaintextPassword(storedAfterAdd));

        // 2. update_camera (IP + username + password)
        const string newSecret = "UpdatedPass!42";
        HttpResponseMessage updateRes = await CallToolAsync(host.Client, token, "update_camera", new
        {
            camera = "Lobby",
            ip = "10.0.0.50",
            username = "operator",
            password = newSecret
        });
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        string updateRaw = await updateRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(newSecret, updateRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(initialSecret, updateRaw, StringComparison.Ordinal);

        JsonObject updatedPayload = ExtractToolJsonPayload(updateRaw);
        Assert.Equal("10.0.0.50", updatedPayload["ip"]?.GetValue<string>());
        Assert.Equal("operator", updatedPayload["username"]?.GetValue<string>());
        Assert.True(updatedPayload["has_password"]?.GetValue<bool>());
        Assert.Empty(playback.Events); // Pre-flight ruling: update_camera does NOT directly call playback controller

        CameraConfig? storedAfterUpdate = store.FindCamera(camId);
        Assert.NotNull(storedAfterUpdate);
        Assert.Equal(newSecret, store.GetPlaintextPassword(storedAfterUpdate));
        string diskJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "config", "cameras.json"));
        Assert.DoesNotContain(newSecret, diskJson, StringComparison.Ordinal);

        // 3. update_camera with clear_password = true
        HttpResponseMessage clearRes = await CallToolAsync(host.Client, token, "update_camera", new
        {
            camera = "2",
            clear_password = true
        });
        JsonObject clearedPayload = ExtractToolJsonPayload(await clearRes.Content.ReadAsStringAsync());
        Assert.False(clearedPayload["has_password"]?.GetValue<bool>());
        Assert.Equal(string.Empty, store.GetPlaintextPassword(store.FindCamera(camId)!));

        // 4. control_camera invokes start / restart / stop
        HttpResponseMessage startRes = await CallToolAsync(host.Client, token, "control_camera", new { camera = camId, action = "start" });
        JsonObject startPayload = ExtractToolJsonPayload(await startRes.Content.ReadAsStringAsync());
        Assert.True(startPayload["is_playing"]?.GetValue<bool>());

        HttpResponseMessage restartRes = await CallToolAsync(host.Client, token, "control_camera", new { camera = "2", action = "restart" });
        JsonObject restartPayload = ExtractToolJsonPayload(await restartRes.Content.ReadAsStringAsync());
        Assert.True(restartPayload["is_playing"]?.GetValue<bool>());

        HttpResponseMessage stopRes = await CallToolAsync(host.Client, token, "control_camera", new { camera = "Lobby", action = "stop" });
        JsonObject stopPayload = ExtractToolJsonPayload(await stopRes.Content.ReadAsStringAsync());
        Assert.False(stopPayload["is_playing"]?.GetValue<bool>());

        Assert.Equal([$"start:{camId}", $"restart:{camId}", $"stop:{camId}"], playback.Events);

        // 5. remove_camera
        HttpResponseMessage removeRes = await CallToolAsync(host.Client, token, "remove_camera", new { camera = camId });
        Assert.Equal(HttpStatusCode.OK, removeRes.StatusCode);
        JsonObject removePayload = ExtractToolJsonPayload(await removeRes.Content.ReadAsStringAsync());
        Assert.True(removePayload["removed"]?.GetValue<bool>());
        Assert.Null(store.FindCamera(camId));
        Assert.Equal(3, playback.Events.Count); // Pre-flight ruling: remove_camera does NOT directly call playback controller
    }

    [Fact]
    public async Task McpEndpoint_GetCameraScreenshot_ReturnsJpegImageAndTextContent()
    {
        byte[] jpegBytes = CreateMinimalJpeg(width: 1280, height: 720);
        int callCount = 0;
        var runner = new FakeSnapshotRunner((_, _, _, _, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("453 Not Enough Bandwidth");
            }

            return Task.FromResult(jpegBytes);
        });

        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, FakeLivePlaybackController playback) =
            CreateDependencies(runner);
        CameraConfig cam = store.AddCamera(new CameraAddRequest(
            Name: "Backyard",
            Ip: "192.168.1.40",
            Username: "admin",
            Password: "SnapSecretPassword!"));
        playback.SetPlaying(cam.Id, true);

        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback);

        HttpResponseMessage res = await CallToolAsync(host.Client, token, "get_camera_screenshot", new { camera = "backyard" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        JsonObject json = await ReadJsonObjectAsync(res);
        JsonObject result = json["result"]!.AsObject();
        Assert.False(result["isError"]?.GetValue<bool>() ?? false);

        JsonArray content = result["content"]!.AsArray();
        Assert.Equal(2, content.Count);

        JsonObject imageItem = content[0]!.AsObject();
        Assert.Equal("image", imageItem["type"]?.GetValue<string>());
        Assert.Equal("image/jpeg", imageItem["mimeType"]?.GetValue<string>());
        byte[] decoded = Convert.FromBase64String(imageItem["data"]!.GetValue<string>());
        Assert.Equal(jpegBytes, decoded);

        JsonObject textItem = content[1]!.AsObject();
        Assert.Equal("text", textItem["type"]?.GetValue<string>());
        string summary = textItem["text"]!.GetValue<string>();
        Assert.Contains("Camera Backyard (slot 1)", summary, StringComparison.Ordinal);
        Assert.Contains("1280x720", summary, StringComparison.Ordinal);
        Assert.Contains("captured ", summary, StringComparison.Ordinal);
        Assert.Contains("pause_fallback=true", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpEndpoint_InvalidToolArgs_ReturnsIsErrorTrueWithoutLeakingSecrets()
    {
        const string secret = "VerySecretPwd#999";
        var runner = new FakeSnapshotRunner((_, rtspUrl, _, _, _) =>
            throw new InvalidOperationException($"Failed opening {rtspUrl}: Connection timed out"));

        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, _) = CreateDependencies(runner);
        store.AddCamera(new CameraAddRequest(
            Name: "Gate",
            Ip: "192.168.1.80",
            Username: "admin",
            Password: secret));
        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback: null);

        // 1. Screenshot error sanitizes RTSP credentials and returns isError: true
        HttpResponseMessage snapErrRes = await CallToolAsync(host.Client, token, "get_camera_screenshot", new { camera = "Gate" });
        string snapErrRaw = await snapErrRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, snapErrRaw, StringComparison.Ordinal);
        JsonObject snapErrResult = (await ReadJsonObjectAsync(snapErrRes))["result"]!.AsObject();
        Assert.True(snapErrResult["isError"]?.GetValue<bool>());
        Assert.Contains("rtsp://***@", snapErrResult["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // 2. add_camera with invalid IP + password does not leak password
        HttpResponseMessage addErrRes = await CallToolAsync(host.Client, token, "add_camera", new
        {
            name = "Bad",
            ip = "192.168.1.1@evil",
            password = secret
        });
        string addErrRaw = await addErrRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, addErrRaw, StringComparison.Ordinal);
        Assert.True(JsonNode.Parse(addErrRaw)!["result"]!["isError"]?.GetValue<bool>());

        // 3. Missing/wrong-typed args and unknown camera return isError: true
        HttpResponseMessage wrongTypeRes = await CallToolAsync(host.Client, token, "add_camera", new
        {
            name = "BadPort",
            ip = "192.168.1.81",
            port = "not-an-int"
        });
        Assert.True((await ReadJsonObjectAsync(wrongTypeRes))["result"]!["isError"]?.GetValue<bool>());

        HttpResponseMessage missingCamRes = await CallToolAsync(host.Client, token, "remove_camera", new { camera = "does-not-exist" });
        Assert.True((await ReadJsonObjectAsync(missingCamRes))["result"]!["isError"]?.GetValue<bool>());

        // 4. control_camera with invalid action or null playback controller returns isError: true
        HttpResponseMessage badActionRes = await CallToolAsync(host.Client, token, "control_camera", new { camera = "Gate", action = "pause" });
        Assert.True((await ReadJsonObjectAsync(badActionRes))["result"]!["isError"]?.GetValue<bool>());

        HttpResponseMessage noPlaybackRes = await CallToolAsync(host.Client, token, "control_camera", new { camera = "Gate", action = "start" });
        Assert.True((await ReadJsonObjectAsync(noPlaybackRes))["result"]!["isError"]?.GetValue<bool>());

        // 5. JSON-RPC protocol errors: -32700, -32600, -32601, -32602
        using var malformedReq = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        malformedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        malformedReq.Content = new StringContent("{not valid json", Encoding.UTF8, "application/json");
        HttpResponseMessage malformedRes = await host.Client.SendAsync(malformedReq);
        Assert.Equal(-32700, (await ReadJsonObjectAsync(malformedRes))["error"]!["code"]?.GetValue<int>());

        HttpResponseMessage invalidRpcRes = await SendJsonRpcAsync(host.Client, token, new { jsonrpc = "1.0", id = 9, method = "ping" });
        Assert.Equal(-32600, (await ReadJsonObjectAsync(invalidRpcRes))["error"]!["code"]?.GetValue<int>());

        HttpResponseMessage unknownMethodRes = await SendJsonRpcAsync(host.Client, token, new { jsonrpc = "2.0", id = 10, method = "resources/list" });
        Assert.Equal(HttpStatusCode.OK, unknownMethodRes.StatusCode);
        Assert.Equal(-32601, (await ReadJsonObjectAsync(unknownMethodRes))["error"]!["code"]?.GetValue<int>());

        HttpResponseMessage unknownToolRes = await CallToolAsync(host.Client, token, "nonexistent_tool", new { });
        Assert.Equal(-32602, (await ReadJsonObjectAsync(unknownToolRes))["error"]!["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task McpEndpoint_GetReturns405AndDeleteReturns204()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, _) = CreateDependencies();
        string token = IssueAccessToken(authStore);
        await using TestApp host = await StartTestHostAsync(store, snapshots, authStore, playback: null);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage getRes = await host.Client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getRes.StatusCode);

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        deleteReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage deleteRes = await host.Client.SendAsync(deleteReq);
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
    }

    [Fact]
    public async Task McpServerHost_BindsLoopbackAndStartsAndStopsCleanly()
    {
        (CameraStore store, OAuthAuthStore authStore, SnapshotService snapshots, FakeLivePlaybackController playback) = CreateDependencies();
        string token = IssueAccessToken(authStore);
        int port = FindFreeLoopbackPort();

        await using var host = new McpServerHost(store, snapshots, authStore, playback);
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.BoundPort);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => host.StartAsync(0));
        Assert.False(host.IsRunning);

        await host.StartAsync(port);
        Assert.True(host.IsRunning);
        Assert.Equal(port, host.BoundPort);
        Assert.Null(host.LastError);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        HttpResponseMessage wellKnownRes = await http.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, wellKnownRes.StatusCode);
        JsonObject wellKnownJson = await ReadJsonObjectAsync(wellKnownRes);
        Assert.Equal($"http://127.0.0.1:{port}/mcp", wellKnownJson["resource"]?.GetValue<string>());

        using var unauthMcp = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        unauthMcp.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json");
        HttpResponseMessage unauthRes = await http.SendAsync(unauthMcp);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        HttpResponseMessage pingRes = await SendJsonRpcAsync(http, token, new { jsonrpc = "2.0", id = 1, method = "ping" });
        Assert.Equal(HttpStatusCode.OK, pingRes.StatusCode);

        // Restart onto a second free port
        int port2 = FindFreeLoopbackPort();
        await host.RestartAsync(port2);
        Assert.True(host.IsRunning);
        Assert.Equal(port2, host.BoundPort);

        using var http2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port2}") };
        HttpResponseMessage wellKnown2 = await http2.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, wellKnown2.StatusCode);

        await host.StopAsync();
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.BoundPort);

        // Port collision leaves IsRunning = false and populates LastError
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        int occupiedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(occupiedPort));
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.BoundPort);
        Assert.False(string.IsNullOrWhiteSpace(host.LastError));
    }

    private (CameraStore Store, OAuthAuthStore AuthStore, SnapshotService Snapshots, FakeLivePlaybackController Playback)
        CreateDependencies(ISnapshotRunner? runner = null)
    {
        var protector = new DpapiSecretProtector();
        var store = new CameraStore(_tempDir, protector);
        store.LoadOrCreate();
        var authStore = new OAuthAuthStore(_tempDir, protector);
        var playback = new FakeLivePlaybackController();
        ISnapshotRunner effectiveRunner = runner ?? new FakeSnapshotRunner((_, _, _, _, _) =>
            Task.FromResult(CreateMinimalJpeg(640, 480)));
        var snapshots = new SnapshotService(store, effectiveRunner, playback);
        return (store, authStore, snapshots, playback);
    }

    private static string IssueAccessToken(OAuthAuthStore authStore)
    {
        OAuthCredentialsView creds = authStore.GetCredentials();
        const string redirectUri = "https://oauth-redirect.googleusercontent.com/r/test";
        const string verifier = "mcp-test-pkce-verifier-01234567890123456789012";
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        string code = authStore.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);
        TokenPair? pair = authStore.ExchangeAuthCode(code, creds.ClientId, redirectUri, verifier);
        Assert.NotNull(pair);
        return pair.AccessToken;
    }

    private static async Task<HttpResponseMessage> CallToolAsync(
        HttpClient client,
        string bearerToken,
        string toolName,
        object arguments,
        int id = 1)
    {
        return await SendJsonRpcAsync(
            client,
            bearerToken,
            new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments
                }
            });
    }

    private static async Task<HttpResponseMessage> SendJsonRpcAsync(
        HttpClient client,
        string bearerToken,
        object payload,
        string? sessionId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (!string.IsNullOrEmpty(sessionId))
        {
            req.Headers.Add("MCP-Session-Id", sessionId);
        }

        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await client.SendAsync(req);
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonObject? obj = JsonNode.Parse(body)?.AsObject();
        Assert.NotNull(obj);
        return obj;
    }

    private static JsonObject ExtractToolJsonPayload(string responseBody)
    {
        JsonObject root = JsonNode.Parse(responseBody)!.AsObject();
        JsonObject result = root["result"]!.AsObject();
        Assert.False(result["isError"]?.GetValue<bool>() ?? false);
        string text = result["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!.AsObject();
    }

    private static void AssertProtectedResourceHeader(HttpResponseMessage response, string expectedMetadataUrl)
    {
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out IEnumerable<string>? values));
        string header = Assert.Single(values);
        Assert.Equal($"Bearer resource_metadata=\"{expectedMetadataUrl}\"", header);
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] CreateMinimalJpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x06, 0x4A, 0x46, 0x49, 0x46,
        0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)(height & 0xFF),
        (byte)(width >> 8), (byte)(width & 0xFF),
        0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9
    ];

    private static async Task<TestApp> StartTestHostAsync(
        CameraStore store,
        SnapshotService snapshots,
        OAuthAuthStore authStore,
        ILivePlaybackController? playback)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();
        OAuthEndpoints.MapOAuthEndpoints(app, authStore, () => store.GetConfigSnapshot().McpPublicBaseUrl);
        McpEndpoint.MapMcpEndpoints(app, store, snapshots, authStore, playback);
        await app.StartAsync();
        return new TestApp(app, app.GetTestClient());
    }

    private sealed class TestApp(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeSnapshotRunner(
        Func<string, string, string, TimeSpan, CancellationToken, Task<byte[]>> handler) : ISnapshotRunner
    {
        public Task<byte[]> CaptureJpegAsync(
            string ffmpegPath,
            string rtspUrl,
            string transport,
            TimeSpan timeout,
            CancellationToken ct) =>
            handler(ffmpegPath, rtspUrl, transport, timeout, ct);
    }

    private sealed class FakeLivePlaybackController : ILivePlaybackController
    {
        private readonly Dictionary<string, bool> _playing = new(StringComparer.Ordinal);
        public List<string> Events { get; } = [];

        public void SetPlaying(string cameraId, bool isPlaying) => _playing[cameraId] = isPlaying;

        public bool IsPlaying(string cameraId) => _playing.TryGetValue(cameraId, out bool playing) && playing;

        public Task<bool> StartCameraAsync(string cameraId, CancellationToken ct = default)
        {
            _playing[cameraId] = true;
            Events.Add($"start:{cameraId}");
            return Task.FromResult(true);
        }

        public Task StopCameraAsync(string cameraId, CancellationToken ct = default)
        {
            _playing[cameraId] = false;
            Events.Add($"stop:{cameraId}");
            return Task.CompletedTask;
        }

        public Task<bool> RestartCameraAsync(string cameraId, CancellationToken ct = default)
        {
            _playing[cameraId] = true;
            Events.Add($"restart:{cameraId}");
            return Task.FromResult(true);
        }
    }
}
