using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Media;

namespace WinRtspPlayer.Core.Mcp;

/// <summary>
/// Streamable HTTP MCP endpoint (<c>/mcp</c>) implementing JSON-RPC 2.0 and the 6 camera management tools.
/// </summary>
public static class McpEndpoint
{
    private const string DefaultProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions =
    [
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly McpToolDefinition[] ToolDefinitions =
    [
        new(
            Name: "list_cameras",
            Description: "List all configured cameras and their live playback status without exposing passwords.",
            InputSchema: new
            {
                type = "object",
                properties = new { }
            }),
        new(
            Name: "get_camera_screenshot",
            Description: "Capture a single live JPEG frame from a camera (by id, 1-based slot, or case-insensitive name).",
            InputSchema: new
            {
                type = "object",
                properties = new
                {
                    camera = new
                    {
                        type = "string",
                        description = "Camera ID, 1-based slot number, or case-insensitive camera name."
                    }
                },
                required = new[] { "camera" }
            }),
        new(
            Name: "update_camera",
            Description: "Update an existing camera's connection settings, credentials, slot, or playback options.",
            InputSchema: new
            {
                type = "object",
                properties = new
                {
                    camera = new { type = "string", description = "Camera ID, 1-based slot number, or case-insensitive name." },
                    name = new { type = "string", description = "Display name for the camera." },
                    ip = new { type = "string", description = "Camera IP address or hostname." },
                    port = new { type = "integer", minimum = 1, maximum = 65535, description = "RTSP port (1-65535)." },
                    path = new { type = "string", description = "RTSP stream path starting with '/' or empty." },
                    username = new { type = "string", description = "RTSP username." },
                    password = new { type = "string", description = "New RTSP password (stored encrypted at rest)." },
                    clear_password = new { type = "boolean", description = "Set true to remove the stored password." },
                    transport = new { type = "string", @enum = new[] { "tcp", "udp" }, description = "RTSP transport protocol." },
                    auto_play = new { type = "boolean", description = "Automatically connect on startup." },
                    hard_decode = new { type = "boolean", description = "Enable hardware video decoding." },
                    slot = new { type = "integer", minimum = 1, maximum = CameraStore.MaxSlots, description = "Target tile slot (1-16)." }
                },
                required = new[] { "camera" }
            }),
        new(
            Name: "add_camera",
            Description: "Add a new RTSP camera to a specific slot or the next available slot (1-16).",
            InputSchema: new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Display name for the camera." },
                    ip = new { type = "string", description = "Camera IP address or hostname." },
                    port = new { type = "integer", minimum = 1, maximum = 65535, description = "RTSP port (default 554)." },
                    path = new { type = "string", description = "RTSP stream path starting with '/' or empty." },
                    username = new { type = "string", description = "RTSP username." },
                    password = new { type = "string", description = "RTSP password (stored encrypted at rest)." },
                    transport = new { type = "string", @enum = new[] { "tcp", "udp" }, description = "RTSP transport protocol (default tcp)." },
                    slot = new { type = "integer", minimum = 1, maximum = CameraStore.MaxSlots, description = "Target tile slot (1-16)." },
                    auto_play = new { type = "boolean", description = "Automatically connect on startup (default true)." },
                    hard_decode = new { type = "boolean", description = "Enable hardware video decoding (default false)." }
                },
                required = new[] { "name", "ip" }
            }),
        new(
            Name: "remove_camera",
            Description: "Remove a configured camera by id, 1-based slot, or case-insensitive name.",
            InputSchema: new
            {
                type = "object",
                properties = new
                {
                    camera = new { type = "string", description = "Camera ID, 1-based slot number, or case-insensitive name." }
                },
                required = new[] { "camera" }
            }),
        new(
            Name: "control_camera",
            Description: "Start, stop, or restart live playback for a configured camera.",
            InputSchema: new
            {
                type = "object",
                properties = new
                {
                    camera = new { type = "string", description = "Camera ID, 1-based slot number, or case-insensitive name." },
                    action = new { type = "string", @enum = new[] { "start", "stop", "restart" }, description = "Playback action to perform." }
                },
                required = new[] { "camera", "action" }
            })
    ];

    public static void MapMcpEndpoints(
        IEndpointRouteBuilder app,
        CameraStore store,
        SnapshotService snapshots,
        OAuthAuthStore authStore,
        ILivePlaybackController? playback)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(authStore);

        app.MapGet("/mcp", (HttpContext ctx) =>
        {
            if (!TryAuthorize(ctx, store, authStore, out IResult? unauthorized))
            {
                return unauthorized!;
            }

            ctx.Response.Headers["Allow"] = "POST, DELETE";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });

        app.MapDelete("/mcp", (HttpContext ctx) =>
        {
            if (!TryAuthorize(ctx, store, authStore, out IResult? unauthorized))
            {
                return unauthorized!;
            }

            return Results.NoContent();
        });

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (!TryAuthorize(ctx, store, authStore, out IResult? unauthorized))
            {
                return unauthorized!;
            }

            string? sessionId = ExtractSafeSessionId(ctx.Request.Headers);
            if (sessionId is not null)
            {
                ctx.Response.Headers["MCP-Session-Id"] = sessionId;
            }

            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return JsonRpcError(null, -32700, "Parse error.", StatusCodes.Status400BadRequest);
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return JsonRpcError(null, -32600, "Invalid Request.", StatusCodes.Status400BadRequest);
                }

                bool hasId = root.TryGetProperty("id", out JsonElement idElement);
                if (hasId && idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                {
                    return JsonRpcError(null, -32600, "Invalid Request: 'id' must be a string, number, or null.", StatusCodes.Status400BadRequest);
                }

                JsonElement? responseId = hasId ? idElement.Clone() : null;

                if (!root.TryGetProperty("jsonrpc", out JsonElement versionElement) ||
                    versionElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(versionElement.GetString(), "2.0", StringComparison.Ordinal))
                {
                    return JsonRpcError(responseId, -32600, "Invalid Request: 'jsonrpc' must be '2.0'.", StatusCodes.Status400BadRequest);
                }

                if (!root.TryGetProperty("method", out JsonElement methodElement) ||
                    methodElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(methodElement.GetString()))
                {
                    return JsonRpcError(responseId, -32600, "Invalid Request: 'method' must be a non-empty string.", StatusCodes.Status400BadRequest);
                }

                string method = methodElement.GetString()!;
                bool isNotification = !hasId ||
                    (method.StartsWith("notifications/", StringComparison.Ordinal) && idElement.ValueKind == JsonValueKind.Null);
                if (isNotification)
                {
                    return Results.StatusCode(StatusCodes.Status202Accepted);
                }

                JsonElement? paramsElement = null;
                if (root.TryGetProperty("params", out JsonElement rawParams))
                {
                    if (rawParams.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    {
                        return JsonRpcError(responseId, -32602, "Invalid params: 'params' must be an object.");
                    }

                    if (rawParams.ValueKind == JsonValueKind.Object)
                    {
                        paramsElement = rawParams;
                    }
                }

                return await DispatchMethodAsync(
                    ctx,
                    responseId,
                    method,
                    paramsElement,
                    sessionId,
                    store,
                    snapshots,
                    playback).ConfigureAwait(false);
            }
        });
    }

    private static async Task<IResult> DispatchMethodAsync(
        HttpContext ctx,
        JsonElement? id,
        string method,
        JsonElement? paramsElement,
        string? existingSessionId,
        CameraStore store,
        SnapshotService snapshots,
        ILivePlaybackController? playback)
    {
        switch (method)
        {
            case "initialize":
                if (existingSessionId is null)
                {
                    ctx.Response.Headers["MCP-Session-Id"] = Guid.NewGuid().ToString("N");
                }

                string negotiatedVersion = DefaultProtocolVersion;
                if (paramsElement.HasValue &&
                    paramsElement.Value.TryGetProperty("protocolVersion", out JsonElement clientVersionEl) &&
                    clientVersionEl.ValueKind == JsonValueKind.String)
                {
                    string? clientVersion = clientVersionEl.GetString();
                    if (clientVersion is not null && SupportedProtocolVersions.Contains(clientVersion, StringComparer.Ordinal))
                    {
                        negotiatedVersion = clientVersion;
                    }
                }

                return JsonRpcResult(id, new
                {
                    protocolVersion = negotiatedVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "win-rtsp-player", version = "1.0.0" }
                });

            case "ping":
                return JsonRpcResult(id, new { });

            case "tools/list":
                return JsonRpcResult(id, new { tools = ToolDefinitions });

            case "tools/call":
                return await HandleToolsCallAsync(
                    id,
                    paramsElement,
                    store,
                    snapshots,
                    playback,
                    ctx.RequestAborted).ConfigureAwait(false);

            default:
                return JsonRpcError(id, -32601, $"Method not found: {method}");
        }
    }

    private static async Task<IResult> HandleToolsCallAsync(
        JsonElement? id,
        JsonElement? paramsElement,
        CameraStore store,
        SnapshotService snapshots,
        ILivePlaybackController? playback,
        CancellationToken ct)
    {
        if (!paramsElement.HasValue ||
            !paramsElement.Value.TryGetProperty("name", out JsonElement nameEl) ||
            nameEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            return JsonRpcError(id, -32602, "Invalid params: 'name' must be a non-empty string.");
        }

        string toolName = nameEl.GetString()!;
        JsonElement? args = null;
        if (paramsElement.Value.TryGetProperty("arguments", out JsonElement rawArgs))
        {
            if (rawArgs.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            {
                return JsonRpcError(id, -32602, "Invalid params: 'arguments' must be an object.");
            }

            if (rawArgs.ValueKind == JsonValueKind.Object)
            {
                args = rawArgs;
            }
        }

        McpToolResult? toolResult = toolName switch
        {
            "list_cameras" => HandleListCameras(store, playback),
            "get_camera_screenshot" => await HandleGetCameraScreenshotAsync(args, store, snapshots, ct).ConfigureAwait(false),
            "update_camera" => HandleUpdateCamera(args, store, playback),
            "add_camera" => HandleAddCamera(args, store, playback),
            "remove_camera" => HandleRemoveCamera(args, store),
            "control_camera" => await HandleControlCameraAsync(args, store, playback, ct).ConfigureAwait(false),
            _ => null
        };

        if (toolResult is null)
        {
            return JsonRpcError(id, -32602, $"Unknown tool: {toolName}");
        }

        return JsonRpcResult(id, toolResult);
    }

    private static McpToolResult HandleListCameras(CameraStore store, ILivePlaybackController? playback)
    {
        AppConfig snapshot = store.GetConfigSnapshot();
        IReadOnlyList<CameraView> views = store.ListCameras(playback is null ? null : playback.IsPlaying);

        var payload = new
        {
            grid_layout = snapshot.GridLayout,
            cameras = views.Select(ToCameraDto).ToArray()
        };

        return TextToolSuccess(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static async Task<McpToolResult> HandleGetCameraScreenshotAsync(
        JsonElement? args,
        CameraStore store,
        SnapshotService snapshots,
        CancellationToken ct)
    {
        if (!TryGetRequiredString(args, "camera", out string cameraKey, out string? argError))
        {
            return TextToolError(argError!);
        }

        CameraConfig? cam = store.FindCamera(cameraKey);
        if (cam is null)
        {
            return TextToolError($"Camera '{FfmpegSnapshotRunner.SanitizeErrorText(cameraKey)}' was not found.");
        }

        try
        {
            SnapshotResult result = await snapshots.CaptureAsync(cam, ct).ConfigureAwait(false);
            string base64 = Convert.ToBase64String(result.JpegBytes);
            string summary =
                $"Camera {cam.Name} (slot {cam.Slot}), {result.Width}x{result.Height}, " +
                $"captured {result.CapturedAtUtc.UtcDateTime:O}, " +
                $"pause_fallback={(result.UsedPauseFallback ? "true" : "false")}";

            return new McpToolResult(
                Content:
                [
                    McpContentItem.Image(base64, "image/jpeg"),
                    McpContentItem.TextContent(summary)
                ]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TextToolError($"Failed to capture screenshot: {FfmpegSnapshotRunner.SanitizeErrorText(ex.Message)}");
        }
    }

    private static McpToolResult HandleUpdateCamera(
        JsonElement? args,
        CameraStore store,
        ILivePlaybackController? playback)
    {
        if (!TryGetRequiredString(args, "camera", out string cameraKey, out string? error) ||
            !TryGetOptionalString(args, "name", out string? name, out error) ||
            !TryGetOptionalString(args, "ip", out string? ip, out error) ||
            !TryGetOptionalInt(args, "port", out int? port, out error) ||
            !TryGetOptionalString(args, "path", out string? path, out error) ||
            !TryGetOptionalString(args, "username", out string? username, out error) ||
            !TryGetOptionalString(args, "password", out string? password, out error) ||
            !TryGetOptionalBool(args, "clear_password", out bool? clearPassword, out error) ||
            !TryGetOptionalString(args, "transport", out string? transport, out error) ||
            !TryGetOptionalInt(args, "slot", out int? slot, out error) ||
            !TryGetOptionalBool(args, "hard_decode", out bool? hardDecode, out error) ||
            !TryGetOptionalBool(args, "auto_play", out bool? autoPlay, out error))
        {
            return TextToolError(error!);
        }

        try
        {
            CameraConfig updated = store.UpdateCamera(
                cameraKey,
                new CameraUpdateRequest(
                    Name: name,
                    Ip: ip,
                    Port: port,
                    Path: path,
                    Username: username,
                    Password: password,
                    ClearPassword: clearPassword ?? false,
                    Transport: transport,
                    Slot: slot,
                    HardDecode: hardDecode,
                    AutoPlay: autoPlay));

            return TextToolSuccess(JsonSerializer.Serialize(ToCameraDto(updated, playback), SerializerOptions));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            return TextToolError(FfmpegSnapshotRunner.SanitizeErrorText(ex.Message));
        }
    }

    private static McpToolResult HandleAddCamera(
        JsonElement? args,
        CameraStore store,
        ILivePlaybackController? playback)
    {
        if (!TryGetRequiredString(args, "name", out string name, out string? error) ||
            !TryGetRequiredString(args, "ip", out string ip, out error) ||
            !TryGetOptionalInt(args, "port", out int? port, out error) ||
            !TryGetOptionalString(args, "path", out string? path, out error) ||
            !TryGetOptionalString(args, "username", out string? username, out error) ||
            !TryGetOptionalString(args, "password", out string? password, out error) ||
            !TryGetOptionalString(args, "transport", out string? transport, out error) ||
            !TryGetOptionalInt(args, "slot", out int? slot, out error) ||
            !TryGetOptionalBool(args, "hard_decode", out bool? hardDecode, out error) ||
            !TryGetOptionalBool(args, "auto_play", out bool? autoPlay, out error))
        {
            return TextToolError(error!);
        }

        try
        {
            CameraConfig added = store.AddCamera(new CameraAddRequest(
                Name: name,
                Ip: ip,
                Port: port,
                Path: path,
                Username: username,
                Password: password,
                Transport: transport,
                Slot: slot,
                HardDecode: hardDecode,
                AutoPlay: autoPlay));

            return TextToolSuccess(JsonSerializer.Serialize(ToCameraDto(added, playback), SerializerOptions));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return TextToolError(FfmpegSnapshotRunner.SanitizeErrorText(ex.Message));
        }
    }

    private static McpToolResult HandleRemoveCamera(JsonElement? args, CameraStore store)
    {
        if (!TryGetRequiredString(args, "camera", out string cameraKey, out string? error))
        {
            return TextToolError(error!);
        }

        CameraConfig? existing = store.FindCamera(cameraKey);
        if (existing is null || !store.RemoveCamera(existing.Id))
        {
            return TextToolError($"Camera '{FfmpegSnapshotRunner.SanitizeErrorText(cameraKey)}' was not found.");
        }

        var payload = new
        {
            removed = true,
            id = existing.Id,
            slot = existing.Slot,
            name = existing.Name
        };

        return TextToolSuccess(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static async Task<McpToolResult> HandleControlCameraAsync(
        JsonElement? args,
        CameraStore store,
        ILivePlaybackController? playback,
        CancellationToken ct)
    {
        if (!TryGetRequiredString(args, "camera", out string cameraKey, out string? error) ||
            !TryGetRequiredString(args, "action", out string rawAction, out error))
        {
            return TextToolError(error!);
        }

        string action = rawAction.Trim().ToLowerInvariant();
        if (action is not ("start" or "stop" or "restart"))
        {
            return TextToolError($"Action must be 'start', 'stop', or 'restart' (got '{FfmpegSnapshotRunner.SanitizeErrorText(rawAction)}').");
        }

        CameraConfig? cam = store.FindCamera(cameraKey);
        if (cam is null)
        {
            return TextToolError($"Camera '{FfmpegSnapshotRunner.SanitizeErrorText(cameraKey)}' was not found.");
        }

        if (playback is null)
        {
            return TextToolError("Live playback controller is not available.");
        }

        try
        {
            bool succeeded = action switch
            {
                "start" => await playback.StartCameraAsync(cam.Id, ct).ConfigureAwait(false),
                "restart" => await playback.RestartCameraAsync(cam.Id, ct).ConfigureAwait(false),
                "stop" => await StopAndReturnTrueAsync(playback, cam.Id, ct).ConfigureAwait(false),
                _ => false
            };

            var payload = new
            {
                id = cam.Id,
                slot = cam.Slot,
                name = cam.Name,
                action,
                success = succeeded,
                is_playing = playback.IsPlaying(cam.Id)
            };

            return TextToolSuccess(JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TextToolError($"Failed to {action} camera '{cam.Name}': {FfmpegSnapshotRunner.SanitizeErrorText(ex.Message)}");
        }
    }

    private static async Task<bool> StopAndReturnTrueAsync(ILivePlaybackController playback, string cameraId, CancellationToken ct)
    {
        await playback.StopCameraAsync(cameraId, ct).ConfigureAwait(false);
        return true;
    }

    private static bool TryAuthorize(
        HttpContext ctx,
        CameraStore store,
        OAuthAuthStore authStore,
        out IResult? unauthorizedResult)
    {
        if (ctx.Request.Headers.TryGetValue("Authorization", out StringValues authHeaders) &&
            authHeaders.Count == 1)
        {
            string header = authHeaders[0] ?? string.Empty;
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = header["Bearer ".Length..].Trim();
                if (!string.IsNullOrEmpty(token) && authStore.ValidateAccessToken(token))
                {
                    unauthorizedResult = null;
                    return true;
                }
            }
        }

        string baseUrl = OAuthEndpoints.ResolvePublicBaseUrl(ctx.Request, store.GetConfigSnapshot().McpPublicBaseUrl);
        ctx.Response.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
        unauthorizedResult = Results.StatusCode(StatusCodes.Status401Unauthorized);
        return false;
    }

    private static string? ExtractSafeSessionId(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("MCP-Session-Id", out StringValues values) || values.Count != 1)
        {
            return null;
        }

        string trimmed = (values[0] ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl))
        {
            return null;
        }

        return trimmed;
    }

    private static bool TryGetRequiredString(JsonElement? args, string key, out string value, out string? error)
    {
        value = string.Empty;
        if (!args.HasValue ||
            !args.Value.TryGetProperty(key, out JsonElement prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            error = $"Missing required string argument '{key}'.";
            return false;
        }

        string candidate = prop.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = $"Argument '{key}' must not be empty.";
            return false;
        }

        value = candidate;
        error = null;
        return true;
    }

    private static bool TryGetOptionalString(JsonElement? args, string key, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!args.HasValue ||
            !args.Value.TryGetProperty(key, out JsonElement prop) ||
            prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            error = $"Argument '{key}' must be a string.";
            return false;
        }

        value = prop.GetString();
        return true;
    }

    private static bool TryGetOptionalInt(JsonElement? args, string key, out int? value, out string? error)
    {
        value = null;
        error = null;
        if (!args.HasValue ||
            !args.Value.TryGetProperty(key, out JsonElement prop) ||
            prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out int parsed))
        {
            error = $"Argument '{key}' must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetOptionalBool(JsonElement? args, string key, out bool? value, out string? error)
    {
        value = null;
        error = null;
        if (!args.HasValue ||
            !args.Value.TryGetProperty(key, out JsonElement prop) ||
            prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (prop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"Argument '{key}' must be a boolean.";
            return false;
        }

        value = prop.GetBoolean();
        return true;
    }

    private static McpCameraDto ToCameraDto(CameraConfig cam, ILivePlaybackController? playback) =>
        ToCameraDto(new CameraView(
            Id: cam.Id,
            Slot: cam.Slot,
            Name: cam.Name,
            Ip: cam.Ip,
            Port: cam.Port,
            Path: cam.Path,
            Username: cam.Username,
            HasPassword: !string.IsNullOrEmpty(cam.EncryptedPassword),
            Transport: cam.Transport,
            HardDecode: cam.HardDecode,
            AutoPlay: cam.AutoPlay,
            IsPlaying: playback?.IsPlaying(cam.Id) ?? false));

    private static McpCameraDto ToCameraDto(CameraView view) =>
        new(
            Id: view.Id,
            Slot: view.Slot,
            Name: view.Name,
            Ip: view.Ip,
            Port: view.Port,
            Path: view.Path,
            Username: view.Username,
            HasPassword: view.HasPassword,
            Transport: view.Transport,
            HardDecode: view.HardDecode,
            AutoPlay: view.AutoPlay,
            IsPlaying: view.IsPlaying);

    private static McpToolResult TextToolSuccess(string text) =>
        new(Content: [McpContentItem.TextContent(text)]);

    private static McpToolResult TextToolError(string text) =>
        new(Content: [McpContentItem.TextContent(text)], IsError: true);

    private static IResult JsonRpcResult(JsonElement? id, object result) =>
        Results.Json(new JsonRpcSuccessResponse("2.0", id, result), SerializerOptions);

    private static IResult JsonRpcError(JsonElement? id, int code, string message, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(new JsonRpcErrorResponse("2.0", id, new JsonRpcErrorBody(code, message)), SerializerOptions, statusCode: statusCode);

    private sealed record McpToolDefinition(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("inputSchema")] object InputSchema);

    private sealed record JsonRpcSuccessResponse(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement? Id,
        [property: JsonPropertyName("result")] object Result);

    private sealed record JsonRpcErrorResponse(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement? Id,
        [property: JsonPropertyName("error")] JsonRpcErrorBody Error);

    private sealed record JsonRpcErrorBody(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);

    private sealed record McpToolResult(
        [property: JsonPropertyName("content")] McpContentItem[] Content,
        [property: JsonPropertyName("isError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsError = null);

    private sealed record McpContentItem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Data = null,
        [property: JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType = null)
    {
        public static McpContentItem TextContent(string text) => new("text", Text: text);
        public static McpContentItem Image(string data, string mimeType) => new("image", Data: data, MimeType: mimeType);
    }

    private sealed record McpCameraDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ip")] string Ip,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("has_password")] bool HasPassword,
        [property: JsonPropertyName("transport")] string Transport,
        [property: JsonPropertyName("hard_decode")] bool HardDecode,
        [property: JsonPropertyName("auto_play")] bool AutoPlay,
        [property: JsonPropertyName("is_playing")] bool IsPlaying);
}
