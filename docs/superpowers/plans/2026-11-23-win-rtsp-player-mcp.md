# Win RTSP Player (.NET 8 Upgrade + Built-in Gemini MCP Server) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade `Src/C#` into a self-contained 32-bit `.NET 8` WinForms multi-camera live RTSP player with encrypted persistent camera configuration and an embedded OAuth 2.1 Streamable HTTP MCP server for Google Gemini.

**Architecture:** Split pure managed core logic (`WinRtspPlayer.Core`, `AnyCPU`, testable under x64 `dotnet test`) from the 32-bit native SDK wrapper (`EasyPlayerNetSDK`, `x86`) and WinForms UI + embedded Kestrel host (`Client`, `win-x86` self-contained). Camera configs and OAuth credentials/tokens persist atomically in `<ExeDir>/config/` encrypted with Windows DPAPI (`CurrentUser`), while MCP tool updates live-sync into the UI thread and on-demand screenshots invoke `ffmpeg.exe` in memory with a 1-connection-camera pause/resume fallback.

**Tech Stack:** .NET 8 (`net8.0-windows`), WinForms, ASP.NET Core Kestrel (`Microsoft.AspNetCore.App`), `System.Security.Cryptography.ProtectedData` 8.0.0, xUnit + `Microsoft.AspNetCore.TestHost`, FFmpeg CLI (`ffmpeg.exe`), native 32-bit `libEasyPlayer-RTSP.dll`.

**Spec:** `docs/superpowers/specs/2026-11-23-win-rtsp-player-mcp-design.md`

## Global Constraints

- Live view only — no PC video recording, no SD-card playback/ONVIF, no edits to `Src/C++`.
- Target `net8.0-windows`; `Client` publishes `dotnet publish -r win-x86 --self-contained true` (no x86 shared `.NET 8` runtime on this PC).
- `WinRtspPlayer.Core` and `WinRtspPlayer.Tests` build `AnyCPU` so `dotnet test` runs directly on the x64 `.NET 8` SDK.
- Native DLL hygiene: copy Release DLLs from `Src/C#/Solution Items/Lib/` plus Microsoft-signed `msvcr100.dll` and `msvcp100.dll` from `Bin/C++/` into output `Lib/`; exclude `dxva2.dll`, `msvcp140d.dll`, `ucrtbased.dll`, `vcruntime140d.dll`, `npEasyPlayerPlugin.dll`, and `ImageOle.dll`.
- All `libEasyPlayer-RTSP.dll` P/Invoke exports use `CallingConvention.Cdecl`, `EasyPlayer_CloseStream` returns `void`, `EasyPlayer_Init("EasyPlayer is Free!")`, and `EasyPlayer_OpenStream` passes `null` for `MediaSourceCallBack` during normal playback.
- Resolve all config and native `Lib/` paths relative to `AppContext.BaseDirectory`, never `Environment.CurrentDirectory`.
- Camera passwords and OAuth secrets/tokens must be encrypted at rest with DPAPI (`DataProtectionScope.CurrentUser`) and never returned in `list_cameras`, UI labels, or error messages.
- Embedded Kestrel binds strictly to `http://127.0.0.1:<mcpPort>` (default `8765`, never `0.0.0.0`).
- On-demand screenshots use `ffmpeg.exe` piped directly to memory (zero disk writes, 10s timeout, max width `1280px`, `-q:v 4`), with automatic brief pause-and-resume fallback if a live-playing camera rejects a second RTSP connection.

## Review Focus

1. **RTSP passwords/usernames with URI metacharacters (`@`, `:`, `/`, `#`, `?`, `%`)** — must percent-encode on build and decode cleanly on parse without corrupting credentials (pinned in Task 2).
2. **1-connection camera failure/timeout during paused screenshot retry** — per-camera lock must serialize requests and `finally` must always reopen the live tile even when `ffmpeg` fails or times out (pinned in Task 3).
3. **Gemini OAuth edge cases: long `state`, `redirect_uri` with existing query params, URL-encoded Basic Auth credentials** — `/oauth/authorize` must preserve `state` verbatim and append with `&` when `?` exists; `/oauth/token` must URL-decode Basic credentials per RFC 6749 §2.3.1 (pinned in Task 4).
4. **Abrupt PC power loss leaving malformed JSON in `config/cameras.json` or `config/mcp-auth.json`** — writes must be atomic (`.tmp` + replace), and corrupt JSON on load must back up the corrupt file and fall back cleanly instead of crashing on startup (pinned in Tasks 2 & 4).
5. **Launching from a shortcut / Windows Startup with a different working directory** — native DLL loading (`SetDllDirectory`) and `config/` path resolution must strictly use `AppContext.BaseDirectory` (pinned in Tasks 2 & 6).

---

## File Structure Map

- **Modify:** `Src/C#/EasyPlayer-RTSP.sln` — register `EasyPlayer-RTSP.NetSDK`, `WinRtspPlayer.Core`, `EasyPlayer-RTSP.Client`, and `WinRtspPlayer.Tests`.
- **Modify:** `Src/C#/EasyPlayerNetSDK/EasyPlayer-RTSP.NetSDK.csproj` — SDK-style `net8.0-windows`, `PlatformTarget=x86`.
- **Modify:** `Src/C#/EasyPlayerNetSDK/PlayerSdk.cs` — fix `__cdecl` signatures, `void EasyPlayer_CloseStream`, `System.Drawing.Rectangle`, add `NativeDllLoader.ConfigureLibDirectory(string baseDirectory)`.
- **Create:** `Src/C#/WinRtspPlayer.Core/WinRtspPlayer.Core.csproj` — `net8.0-windows` (`AnyCPU`), `Microsoft.AspNetCore.App` FrameworkReference, `System.Security.Cryptography.ProtectedData` 8.0.0.
- **Create:** `Src/C#/WinRtspPlayer.Core/Config/CameraConfig.cs` — `CameraConfig`, `AppConfig`, `CameraView`, `CameraUpdateRequest`, `CameraAddRequest`.
- **Create:** `Src/C#/WinRtspPlayer.Core/Config/SecretProtector.cs` — `ISecretProtector`, `DpapiSecretProtector`.
- **Create:** `Src/C#/WinRtspPlayer.Core/Config/RtspUrlCodec.cs` — `ValidateCameraFields`, `BuildUrl`, `TryParseUrl`.
- **Create:** `Src/C#/WinRtspPlayer.Core/Config/CameraStore.cs` — atomic `config/cameras.json` store, one-time `rtsp.txt` migration, camera lookup/CRUD, live-sync events.
- **Create:** `Src/C#/WinRtspPlayer.Core/Media/ILivePlaybackController.cs` — interface for UI live tile state/control (`IsPlaying`, `StartAsync`, `StopAsync`, `RestartAsync`).
- **Create:** `Src/C#/WinRtspPlayer.Core/Media/SnapshotService.cs` — `ISnapshotRunner`, `FfmpegSnapshotRunner`, `SnapshotResult`, `SnapshotService` (direct + 1-connection fallback).
- **Create:** `Src/C#/WinRtspPlayer.Core/Auth/OAuthAuthStore.cs` — DPAPI-persisted `config/mcp-auth.json`, credentials, auth codes, hashed access/refresh tokens, rate limiter.
- **Create:** `Src/C#/WinRtspPlayer.Core/Auth/OAuthEndpoints.cs` — `.well-known/*`, `GET/POST /oauth/authorize`, `POST /oauth/token`, public base URL resolver.
- **Create:** `Src/C#/WinRtspPlayer.Core/Mcp/McpEndpoint.cs` — Bearer middleware + Streamable HTTP `/mcp` JSON-RPC 2.0 router & 6 camera tools.
- **Create:** `Src/C#/WinRtspPlayer.Core/Mcp/McpServerHost.cs` — embedded Kestrel host (`127.0.0.1:<port>`).
- **Modify:** `Src/C#/Client/EasyPlayer-RTSP.Client.csproj` — SDK-style `net8.0-windows` WinForms (`PlatformTarget=x86`, `Microsoft.AspNetCore.App`, selective native DLL copy targets).
- **Modify:** `Src/C#/Client/Program.cs` — configure native `Lib/` directory, launch `MainForm`.
- **Create:** `Src/C#/Client/CameraTileControl.cs` — per-camera video panel + status/Play-Stop/Edit bar + double-click maximize.
- **Create:** `Src/C#/Client/EditCameraDialog.cs` — structured IP/port/path/user/pass editor + "Paste full RTSP URL" parser.
- **Create:** `Src/C#/Client/McpSettingsDialog.cs` — MCP server status, port/public URL, copyable credentials & `cloudflared` setup hint, regenerate secrets.
- **Create:** `Src/C#/Client/StartupRegistration.cs` — optional `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WinRtspPlayer` toggle.
- **Create:** `Src/C#/Client/MainForm.cs` — multi-camera grid (`1/4/6/9/16`), `EasyPlayer` playback manager implementing `ILivePlaybackController`, startup auto-play, live MCP sync, clean shutdown.
- **Delete (obsolete demo files):** `Src/C#/Client/Form1.cs`, `Src/C#/Client/Form1.Designer.cs`, `Src/C#/Client/Form1.resx`, `Src/C#/Client/App.config` (replaced by `MainForm.cs`).
- **Create:** `Src/C#/WinRtspPlayer.Tests/` — xUnit test project (`CameraStoreTests.cs`, `RtspUrlCodecTests.cs`, `SnapshotServiceTests.cs`, `OAuthEndpointsTests.cs`, `McpEndpointTests.cs`).
- **Create:** `Src/C#/start-tunnel.bat` — helper launcher for `cloudflared` tunnel + `docs/MCP-GEMINI-SETUP.md` concise setup guide.

---

### Task 1: Solution Modernization, Native SDK Wrapper & Test Project Scaffold

**Files:**
- Modify: `Src/C#/EasyPlayer-RTSP.sln`
- Modify: `Src/C#/EasyPlayerNetSDK/EasyPlayer-RTSP.NetSDK.csproj`
- Modify: `Src/C#/EasyPlayerNetSDK/PlayerSdk.cs`
- Create: `Src/C#/WinRtspPlayer.Core/WinRtspPlayer.Core.csproj`
- Create: `Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj`
- Create: `Src/C#/WinRtspPlayer.Tests/SmokeTests.cs`

**Interfaces:**
- Produces in `EasyPlayerNetSDK`:
  - `NativeDllLoader.ConfigureLibDirectory(string baseDirectory)` — calls Win32 `SetDllDirectory(Path.Combine(baseDirectory, "Lib"))` and verifies `Lib\libEasyPlayer-RTSP.dll` exists.
  - `PlayerSdk.EasyPlayer_Init(string key = "EasyPlayer is Free!") -> int` (`CallingConvention.Cdecl`)
  - `PlayerSdk.EasyPlayer_Release()` (`CallingConvention.Cdecl`)
  - `PlayerSdk.EasyPlayer_OpenStream(string url, IntPtr hWnd, RENDER_FORMAT renderFormat, int rtpovertcp, string username, string password, MediaSourceCallBack? callback, IntPtr userPtr, bool bHardDecode = false, string? startTime = null, string? endTime = null, float fScale = 1.0f) -> int` (`CallingConvention.Cdecl`)
  - `PlayerSdk.EasyPlayer_CloseStream(int channelId)` (`void`, `CallingConvention.Cdecl`)
  - `PlayerSdk.EasyPlayer_SetFrameCache(int channelId, int cache) -> int`
  - `PlayerSdk.EasyPlayer_SetShownToScale(int channelId, int shownToScale) -> int`
  - `PlayerSdk.EasyPlayer_SetRenderRect(int channelId, System.Drawing.Rectangle lpSrcRect) -> int`
  - `PlayerSdk.EasyPlayer_ShowStatisticalInfo(int channelId, int show) -> int`
  - `PlayerSdk.EasyPlayer_PlaySound(int channelId) -> int`
  - `PlayerSdk.EasyPlayer_StopSound() -> int`

- [ ] **Step 1: Commit the approved design spec**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add docs/superpowers/specs/2026-11-23-win-rtsp-player-mcp-design.md && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "docs: add .NET 8 Win RTSP Player and Gemini MCP design spec"`
  Expected: clean commit containing the spec file.

- [ ] **Step 2: Create SDK-style `WinRtspPlayer.Core.csproj`, `WinRtspPlayer.Tests.csproj`, and modernize `EasyPlayer-RTSP.NetSDK.csproj` + `EasyPlayer-RTSP.sln`**
  - `EasyPlayer-RTSP.NetSDK.csproj`: `<TargetFramework>net8.0-windows</TargetFramework>`, `<PlatformTarget>x86</PlatformTarget>`, `<Nullable>enable</Nullable>`, remove legacy `.NET 4.0` XML & `WindowsBase` reference.
  - `WinRtspPlayer.Core.csproj`: `<TargetFramework>net8.0-windows</TargetFramework>` (`AnyCPU`), `<Nullable>enable</Nullable>`, `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, `<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />`.
  - `WinRtspPlayer.Tests.csproj`: `<TargetFramework>net8.0-windows</TargetFramework>` (`AnyCPU`), references `WinRtspPlayer.Core`, `xunit` 2.9.x, `xunit.runner.visualstudio` 2.8.x, `Microsoft.NET.Test.Sdk` 17.11.x, `Microsoft.AspNetCore.TestHost` 8.0.x.
  - Update `Src/C#/EasyPlayer-RTSP.sln` to include `EasyPlayer-RTSP.NetSDK`, `WinRtspPlayer.Core`, and `WinRtspPlayer.Tests` (keep `Client` wired up in Task 6 or temporarily keep solution buildable on Core + NetSDK + Tests).

- [ ] **Step 3: Update `Src/C#/EasyPlayerNetSDK/PlayerSdk.cs`**
  - Fix `EasyPlayer_Init` default key to `"EasyPlayer is Free!"`, fix `EasyPlayer_Release` to `CallingConvention.Cdecl`, fix `EasyPlayer_CloseStream` return type to `void`, replace `System.Windows.Rect` with `System.Drawing.Rectangle`, remove recording/snapshot helper methods (`EasyPlayer_PicShot`, `EasyPlayer_StartManuRecording`, etc.) per Non-Goals, add `NativeDllLoader.ConfigureLibDirectory(string baseDirectory)` using `kernel32.dll` `SetDllDirectory`.

- [ ] **Step 4: Write and run baseline smoke test (`Src/C#/WinRtspPlayer.Tests/SmokeTests.cs`)**
  - Add `[Fact] public void TestProject_RunsOnNet8()` asserting `Environment.Version.Major >= 8`.
  - Run: `dotnet build Src/C#/EasyPlayerNetSDK/EasyPlayer-RTSP.NetSDK.csproj` and `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj`
  - Expected: `EasyPlayer-RTSP.NetSDK` builds cleanly (0 errors) and `dotnet test` passes 1 test.

- [ ] **Step 5: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C#/EasyPlayer-RTSP.sln Src/C#/EasyPlayerNetSDK Src/C#/WinRtspPlayer.Core/WinRtspPlayer.Core.csproj Src/C#/WinRtspPlayer.Tests && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "build: modernize C# projects to .NET 8 and fix EasyPlayer P/Invoke"`

---

### Task 2: Camera Models, RTSP URL Codec, DPAPI Protection & Persistent `CameraStore`

**Files:**
- Create: `Src/C#/WinRtspPlayer.Core/Config/CameraConfig.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Config/SecretProtector.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Config/RtspUrlCodec.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Config/CameraStore.cs`
- Test: `Src/C#/WinRtspPlayer.Tests/RtspUrlCodecTests.cs`
- Test: `Src/C#/WinRtspPlayer.Tests/CameraStoreTests.cs`

**Interfaces:**
- Produces in `WinRtspPlayer.Core.Config`:
  - `ISecretProtector`: `string Protect(string plaintext)`, `string Unprotect(string ciphertext)`
  - `DpapiSecretProtector : ISecretProtector` (uses `ProtectedData` with `DataProtectionScope.CurrentUser` and entropy `"WinRtspPlayer.v1"`).
  - `CameraConfig`: `Id`, `Slot` (1..16), `Name`, `Ip`, `Port` (default `554`), `Path` (default `""`), `Username`, `EncryptedPassword`, `Transport` (`"tcp"` | `"udp"`, default `"tcp"`), `HardDecode` (`bool`), `AutoPlay` (`bool`, default `true`).
  - `CameraView`: `Id`, `Slot`, `Name`, `Ip`, `Port`, `Path`, `Username`, `HasPassword`, `Transport`, `HardDecode`, `AutoPlay`, `IsPlaying` (never includes password).
  - `AppConfig`: `GridLayout` (`1|4|6|9|16`, default `4`), `RenderFormat` (default `"RGB24_GDI"`), `FrameCache` (default `3`), `ShownToScale` (default `true`), `FfmpegPath` (default `"ffmpeg"`), `McpPort` (default `8765`), `McpPublicBaseUrl` (`string?`), `Cameras` (`List<CameraConfig>`).
  - `CameraAddRequest` / `CameraUpdateRequest` DTOs (used by both UI and MCP).
  - `RtspUrlCodec`:
    - `string? ValidateCameraInput(string ip, int port, string path, string transport)` — returns `null` if valid, else error message (rejects whitespace/`@`/`/`/`?`/`#`/control chars in `ip`, port outside 1..65535, non-empty `path` not starting with `/`, transport not `tcp`/`udp`).
    - `string BuildUrl(CameraConfig cam, string? plaintextPassword)` — percent-encodes `username` and `password` (`Uri.EscapeDataString`), omits credentials if username & password empty.
    - `bool TryParseUrl(string rawUrl, out ParsedRtspUrl parsed, out string? error)` — accepts `rtsp://` URLs, decodes percent-encoded userinfo, extracts host, port (default 554), path+query, username, password.
  - `CameraStore`:
    - Constructor: `CameraStore(string baseDirectory, ISecretProtector protector)` (always stores at `Path.Combine(baseDirectory, "config", "cameras.json")`).
    - `AppConfig LoadOrCreate(string? legacyRtspTxtPath = null)` — loads `cameras.json`; if corrupt JSON, backs it up to `cameras.json.corrupt.<timestamp>` and initializes fresh defaults; if missing and `rtsp.txt` exists, imports non-empty `rtsp://` lines (supporting both `\r\n` and `\n`) into slots 1..16 and saves immediately.
    - `IReadOnlyList<CameraView> ListCameras(Func<string, bool>? isPlayingLookup = null)`
    - `CameraConfig? FindCamera(string idOrSlotOrName)` (matches exact `id`, 1-based `slot`, or case-insensitive `name`).
    - `string GetPlaintextPassword(CameraConfig cam)`
    - `string BuildRtspUrl(CameraConfig cam)`
    - `CameraConfig AddCamera(CameraAddRequest req)` (assigns requested empty slot or next free slot 1..16, validates, saves atomically, fires `CameraChanged`).
    - `CameraConfig UpdateCamera(string idOrSlotOrName, CameraUpdateRequest req)` (updates provided fields, handles `ClearPassword`, validates, saves atomically, fires `CameraChanged`).
    - `bool RemoveCamera(string idOrSlotOrName)` (removes, saves atomically, fires `CameraChanged`).
    - `void UpdateSettings(Action<AppConfig> mutate)` (saves atomically).
    - Event: `event Action<CameraChangeEvent>? CameraChanged;` (`Added`, `Updated`, `Removed` + `CameraConfig`).

- [ ] **Step 1: Write failing tests in `RtspUrlCodecTests.cs` and `CameraStoreTests.cs`**
  - `RtspUrlCodecTests`:
    - `BuildUrl_PercentEncodesSpecialCharactersInCredentials` (`user: "admin@1"`, `pass: "p@ss:w/ord#?%"` → properly escaped and round-trips via `TryParseUrl`).
    - `BuildUrl_OmitsCredentialsWhenEmpty` (`rtsp://192.168.1.10:554/stream1`).
    - `ValidateCameraInput_RejectsInvalidHostPortPathAndTransport` (host with `@` or whitespace, port `0`/`70000`, path `"stream1"` missing leading `/`, transport `"http"`).
  - `CameraStoreTests`:
    - `SaveAndReload_EncryptsPasswordWithDpapiAndPreservesFields` (verifies raw JSON on disk does NOT contain plaintext password, while `GetPlaintextPassword` after fresh `CameraStore` instance returns original password).
    - `ListCameras_NeverExposesPasswordAndSetsHasPasswordFlag`.
    - `LoadOrCreate_MigratesLegacyRtspTxtWithLfAndCrLfLines` (ignores blank lines, populates slots 1..N, writes `config/cameras.json` under `baseDirectory` regardless of `Environment.CurrentDirectory`).
    - `LoadOrCreate_BacksUpCorruptJsonInsteadOfCrashing` (writes truncated JSON, reloads cleanly with defaults, asserts `.corrupt.` backup file exists).
    - `UpdateCamera_FiresCameraChangedAndUpdatesOnlySpecifiedFields`.
    - `FindCamera_MatchesByIdSlotOrCaseInsensitiveName`.

- [ ] **Step 2: Run tests to verify they fail**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter "RtspUrlCodecTests|CameraStoreTests"`
  Expected: FAIL (types do not exist yet).

- [ ] **Step 3: Implement `CameraConfig.cs`, `SecretProtector.cs`, `RtspUrlCodec.cs`, and `CameraStore.cs`**
  - Use `SemaphoreSlim` / `lock` for thread safety; write JSON atomically to `<path>.tmp` then `File.Move(tmp, target, overwrite: true)`.

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter "RtspUrlCodecTests|CameraStoreTests"`
  Expected: PASS (all tests green).

- [ ] **Step 5: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C#/WinRtspPlayer.Core/Config Src/C#/WinRtspPlayer.Tests/RtspUrlCodecTests.cs Src/C#/WinRtspPlayer.Tests/CameraStoreTests.cs && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "feat: add persistent DPAPI camera config store and RTSP URL codec"`

---

### Task 3: On-Demand FFmpeg Snapshot Service with 1-Connection Fallback

**Files:**
- Create: `Src/C#/WinRtspPlayer.Core/Media/ILivePlaybackController.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Media/SnapshotService.cs`
- Test: `Src/C#/WinRtspPlayer.Tests/SnapshotServiceTests.cs`

**Interfaces:**
- Consumes: `CameraStore`, `CameraConfig` from Task 2.
- Produces in `WinRtspPlayer.Core.Media`:
  - `ILivePlaybackController`:
    - `bool IsPlaying(string cameraId)`
    - `Task<bool> StartCameraAsync(string cameraId, CancellationToken ct = default)`
    - `Task StopCameraAsync(string cameraId, CancellationToken ct = default)`
    - `Task<bool> RestartCameraAsync(string cameraId, CancellationToken ct = default)`
  - `SnapshotResult`: `byte[] JpegBytes`, `int Width`, `int Height`, `DateTimeOffset CapturedAtUtc`, `bool UsedPauseFallback`.
  - `ISnapshotRunner`: `Task<byte[]> CaptureJpegAsync(string ffmpegPath, string rtspUrl, string transport, TimeSpan timeout, CancellationToken ct)`.
  - `FfmpegSnapshotRunner : ISnapshotRunner` — spawns `Process` with `UseShellExecute = false`, `CreateNoWindow = true`, `RedirectStandardOutput = true`, `RedirectStandardError = true`, passing exact `ArgumentList`:
    `"-hide_banner"`, `"-loglevel"`, `"error"`, `"-rtsp_transport"`, `transport`, `"-i"`, `rtspUrl`, `"-frames:v"`, `"1"`, `"-vf"`, `"scale='min(1280,iw)':-2"`, `"-q:v"`, `"4"`, `"-f"`, `"image2pipe"`, `"-vcodec"`, `"mjpeg"`, `"pipe:1"`.
    Kills process tree on timeout/cancel and strips any `rtsp://...` credentials from stderr before throwing.
  - `SnapshotService`:
    - Constructor: `SnapshotService(CameraStore store, ISnapshotRunner runner, ILivePlaybackController? playback = null, TimeSpan? timeout = null)` (default timeout 10s).
    - `Task<SnapshotResult> CaptureAsync(CameraConfig camera, CancellationToken ct = default)` — acquires per-camera `SemaphoreSlim`, calls `runner.CaptureJpegAsync(...)`; if it fails AND `playback?.IsPlaying(camera.Id) == true`, stops that camera, retries `CaptureJpegAsync` once, and ALWAYS restarts the camera in `finally`. Validates JPEG header (`0xFF 0xD8`) and extracts width/height from JPEG SOF marker (0x0 if unparseable).

- [ ] **Step 1: Write failing tests in `SnapshotServiceTests.cs`**
  - `CaptureAsync_SucceedsDirectlyWithoutPausingPlayingCamera` (asserts `StopCameraAsync` is NOT called, returns valid JPEG + parsed width/height, `UsedPauseFallback == false`).
  - `CaptureAsync_WhenFirstAttemptFailsOnPlayingCamera_PausesCapturesAndResumes` (asserts exact sequence: direct capture fails → `StopCameraAsync` → retry capture succeeds → `StartCameraAsync`, `UsedPauseFallback == true`).
  - `CaptureAsync_WhenRetryAlsoFails_StillResumesLiveCameraInFinally` (asserts `StartCameraAsync` is called even when retry throws).
  - `CaptureAsync_SerializesConcurrentRequestsForSameCamera` (two concurrent calls do not overlap inside runner).
  - `FfmpegSnapshotRunner_SanitizesCredentialsFromErrorMessages` (unit test helper `SanitizeErrorText` redacts `rtsp://user:secret@192.168.1.5:554/...` → `rtsp://***@192.168.1.5:554/...`).

- [ ] **Step 2: Run tests to verify they fail**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter SnapshotServiceTests`
  Expected: FAIL (types do not exist).

- [ ] **Step 3: Implement `ILivePlaybackController.cs` and `SnapshotService.cs`**
  - Include lightweight pure-managed JPEG SOF0/SOF2 dimension parser (no GDI+ dependency required in Core) and credential redaction helper for stderr.

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter SnapshotServiceTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C#/WinRtspPlayer.Core/Media Src/C#/WinRtspPlayer.Tests/SnapshotServiceTests.cs && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "feat: add on-demand FFmpeg snapshot service with 1-connection fallback"`

---

### Task 4: Built-in OAuth 2.1 Authorization Server

**Files:**
- Create: `Src/C#/WinRtspPlayer.Core/Auth/OAuthAuthStore.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Auth/OAuthEndpoints.cs`
- Test: `Src/C#/WinRtspPlayer.Tests/OAuthEndpointsTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector`, `CameraStore` (for `McpPublicBaseUrl`).
- Produces in `WinRtspPlayer.Core.Auth`:
  - `OAuthCredentialsView`: `string ClientId`, `string ClientSecret`, `string ConsentPasscode`.
  - `OAuthAuthStore`:
    - Constructor: `OAuthAuthStore(string baseDirectory, ISecretProtector protector)` (persists atomically to `Path.Combine(baseDirectory, "config", "mcp-auth.json")`, handles corrupt JSON by backing up + regenerating).
    - `OAuthCredentialsView GetCredentials()`
    - `OAuthCredentialsView RegenerateSecrets()` (generates new random `clientId`, `clientSecret`, `consentPasscode`, clears all active tokens & auth codes, saves atomically).
    - `bool ValidateClientId(string clientId)` (constant-time)
    - `bool ValidateClientSecret(string clientId, string clientSecret)` (constant-time)
    - `bool ValidateConsentPasscode(string passcode, out bool isRateLimited)` (constant-time + max 5 failed attempts per 60-second sliding window)
    - `string CreateAuthCode(string clientId, string redirectUri, string codeChallenge, string? resource)` (5-minute TTL, single-use)
    - `TokenPair? ExchangeAuthCode(string code, string clientId, string redirectUri, string codeVerifier)` (verifies single-use, TTL, `redirect_uri` exact match, PKCE `S256` `BASE64URL(SHA256(verifier)) == codeChallenge`; stores SHA-256 hashes of `access_token` (1h) & `refresh_token` (30d) atomically to disk)
    - `TokenPair? RefreshAccessToken(string refreshToken, string clientId)` (verifies refresh token hash & TTL, rotates/issues new pair, persists atomically)
    - `bool ValidateAccessToken(string bearerToken)` (checks SHA-256 hash & expiry)
  - `OAuthEndpoints`:
    - `static string ResolvePublicBaseUrl(HttpRequest req, string? configuredPublicBaseUrl)` (prefers `configuredPublicBaseUrl`, else `X-Forwarded-Proto` + `X-Forwarded-Host`, else `req.Scheme://req.Host`, trims trailing `/`).
    - `static bool IsAllowedRedirectUri(string redirectUri)` (allows `https://` on `oauth-redirect.googleusercontent.com`, `gemini.google.com`, `*.googleusercontent.com`, `*.google.com`, plus `http://localhost` / `http://127.0.0.1` for local testing; rejects all others).
    - `static void MapOAuthEndpoints(IEndpointRouteBuilder app, OAuthAuthStore authStore, Func<string?> getConfiguredBaseUrl)` — registers:
      - `GET /.well-known/oauth-protected-resource`
      - `GET /.well-known/oauth-protected-resource/mcp`
      - `GET /.well-known/oauth-authorization-server`
      - `GET /oauth/authorize` (validates params, returns minimal HTML consent form with hidden fields + passcode input, HTML-encodes everything)
      - `POST /oauth/authorize` (validates passcode + rate limit, issues `302` to exact `redirect_uri` preserving `state` verbatim and using `&` if `redirect_uri` already contains `?`)
      - `POST /oauth/token` (supports `client_secret_basic` with RFC 6749 URL-decoding AND `client_secret_post`; supports `authorization_code` and `refresh_token`)

- [ ] **Step 1: Write failing tests in `OAuthEndpointsTests.cs` (using `TestServer`)**
  - `WellKnownEndpoints_ReturnHttpsMetadataUsingForwardedHeadersOrConfiguredBaseUrl` (checks both `/.well-known/oauth-protected-resource`, `/.well-known/oauth-protected-resource/mcp`, and `/.well-known/oauth-authorization-server`).
  - `FullAuthorizationCodeFlow_WithPkceAndBasicAuth_SucceedsAndPersistsAcrossStoreReload` (including long `state` string, `redirect_uri` containing existing `?foo=1` on `https://oauth-redirect.googleusercontent.com/r/test`, URL-encoded Basic auth secret, token validation after reloading `OAuthAuthStore` from disk).
  - `TokenEndpoint_SupportsClientSecretPostAndRefreshTokenGrant`.
  - `Authorize_RejectsDisallowedRedirectUri` (`https://evil.example.com/cb` → `400`).
  - `Authorize_RateLimitsAfterFiveFailedPasscodes` (6th attempt returns `429`).
  - `TokenEndpoint_RejectsBadPkceVerifierAndReplayedAuthCode`.
  - `RegenerateSecrets_RevokesExistingAccessAndRefreshTokens`.
  - `CorruptMcpAuthJson_IsBackedUpAndRegeneratedCleanly`.

- [ ] **Step 2: Run tests to verify they fail**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter OAuthEndpointsTests`
  Expected: FAIL.

- [ ] **Step 3: Implement `OAuthAuthStore.cs` and `OAuthEndpoints.cs`**
  - Use `RandomNumberGenerator.GetBytes` and `CryptographicOperations.FixedTimeEquals` for all secret/token/passcode comparisons.

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter OAuthEndpointsTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C#/WinRtspPlayer.Core/Auth Src/C#/WinRtspPlayer.Tests/OAuthEndpointsTests.cs && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "feat: implement built-in OAuth 2.1 authorization server for Gemini MCP"`

---

### Task 5: MCP Streamable HTTP Endpoint, 6 Camera Tools & Embedded Kestrel Host

**Files:**
- Create: `Src/C#/WinRtspPlayer.Core/Mcp/McpEndpoint.cs`
- Create: `Src/C#/WinRtspPlayer.Core/Mcp/McpServerHost.cs`
- Test: `Src/C#/WinRtspPlayer.Tests/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `CameraStore` (Task 2), `SnapshotService` + `ILivePlaybackController` (Task 3), `OAuthAuthStore` + `OAuthEndpoints` (Task 4).
- Produces in `WinRtspPlayer.Core.Mcp`:
  - `McpEndpoint.MapMcpEndpoints(IEndpointRouteBuilder app, CameraStore store, SnapshotService snapshots, OAuthAuthStore authStore, ILivePlaybackController? playback)`
    - Bearer auth check on `/mcp`: missing/invalid token returns `401 Unauthorized` with header `WWW-Authenticate: Bearer resource_metadata="<baseUrl>/.well-known/oauth-protected-resource"`.
    - `POST /mcp`: JSON-RPC 2.0 supporting:
      - `initialize` (returns `protocolVersion` echoed from supported set `2025-06-18` / `2025-03-26` / `2024-11-05` defaulting to `2025-06-18`, `capabilities: { tools: {} }`, `serverInfo: { name: "win-rtsp-player", version: "1.0.0" }`, sets `MCP-Session-Id` header)
      - `notifications/initialized` (returns `202 Accepted`)
      - `ping` (returns `{}`)
      - `tools/list` (returns exact JSON schemas for all 6 tools: `list_cameras`, `get_camera_screenshot`, `update_camera`, `add_camera`, `remove_camera`, `control_camera`)
      - `tools/call` (dispatches to the 6 tools; validation/runtime failures return `result: { isError: true, content: [{ type: "text", text: "..." }] }`)
    - `DELETE /mcp`: returns `204 No Content`.
    - `GET /mcp`: returns `405 Method Not Allowed`.
  - `McpServerHost : IAsyncDisposable`:
    - Constructor: `McpServerHost(CameraStore store, SnapshotService snapshots, OAuthAuthStore authStore, ILivePlaybackController? playback)`
    - `bool IsRunning { get; }`, `int BoundPort { get; }`, `string? LastError { get; }`
    - `Task StartAsync(int port, CancellationToken ct = default)` (binds Kestrel strictly to `IPAddress.Loopback` + `port`, maps OAuth + MCP endpoints).
    - `Task RestartAsync(int newPort, CancellationToken ct = default)`
    - `Task StopAsync(CancellationToken ct = default)`

- [ ] **Step 1: Write failing tests in `McpEndpointTests.cs` (using `TestServer`)**
  - `McpEndpoint_WithoutBearerToken_Returns401WithResourceMetadataHeader`.
  - `McpEndpoint_InitializeAndToolsList_ReturnsAllSixToolsAndSessionHeader`.
  - `McpEndpoint_ListCameras_NeverReturnsPasswords`.
  - `McpEndpoint_AddUpdateRemoveAndControlCamera_PersistsAndCallsPlaybackController` (tests updating IP, username, password, verifying password updated in store while omitted from response, and verifying `RestartCameraAsync`/`StartCameraAsync`/`StopCameraAsync` invocations).
  - `McpEndpoint_GetCameraScreenshot_ReturnsJpegImageAndTextContent`.
  - `McpEndpoint_InvalidToolArgs_ReturnsIsErrorTrueWithoutLeakingSecrets`.
  - `McpEndpoint_GetReturns405AndDeleteReturns204`.

- [ ] **Step 2: Run tests to verify they fail**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter McpEndpointTests`
  Expected: FAIL.

- [ ] **Step 3: Implement `McpEndpoint.cs` and `McpServerHost.cs`**
  - Keep JSON-RPC handling clean and minimal with `System.Text.Json.Nodes`.

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj --filter McpEndpointTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C#/WinRtspPlayer.Core/Mcp Src/C#/WinRtspPlayer.Tests/McpEndpointTests.cs && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "feat: implement Streamable HTTP MCP endpoint and camera tools"`

---

### Task 6: .NET 8 Multi-Camera WinForms UI, Live Playback Integration, Self-Contained Publish & Tunnel Setup

**Files:**
- Modify: `Src/C#/Client/EasyPlayer-RTSP.Client.csproj`
- Modify: `Src/C#/Client/Program.cs`
- Create: `Src/C#/Client/CameraTileControl.cs`
- Create: `Src/C#/Client/EditCameraDialog.cs`
- Create: `Src/C#/Client/McpSettingsDialog.cs`
- Create: `Src/C#/Client/StartupRegistration.cs`
- Create: `Src/C#/Client/MainForm.cs`
- Delete: `Src/C#/Client/Form1.cs`, `Src/C#/Client/Form1.Designer.cs`, `Src/C#/Client/Form1.resx`, `Src/C#/Client/App.config`
- Create: `Src/C#/start-tunnel.bat`
- Create: `docs/MCP-GEMINI-SETUP.md`

**Interfaces:**
- Consumes: `EasyPlayerNetSDK` (`NativeDllLoader`, `PlayerSdk`), `WinRtspPlayer.Core` (`CameraStore`, `RtspUrlCodec`, `DpapiSecretProtector`, `SnapshotService`, `FfmpegSnapshotRunner`, `ILivePlaybackController`, `OAuthAuthStore`, `McpServerHost`).
- Produces: runnable self-contained `win-x86` desktop executable `WinRtspPlayer.exe` with clean `Lib/` directory.

- [ ] **Step 1: Upgrade `Src/C#/Client/EasyPlayer-RTSP.Client.csproj` to SDK-style `.NET 8` WinForms (`win-x86`)**
  - Set `<TargetFramework>net8.0-windows</TargetFramework>`, `<UseWindowsForms>true</UseWindowsForms>`, `<AssemblyName>WinRtspPlayer</AssemblyName>`, `<PlatformTarget>x86</PlatformTarget>`, `<Nullable>enable</Nullable>`, `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, references `EasyPlayer-RTSP.NetSDK` and `WinRtspPlayer.Core`.
  - Add selective MSBuild `<None Include="...">` copy items for `Lib\`:
    - Copy from `..\Solution Items\Lib\`: `avcodec-57.dll`, `avdevice-57.dll`, `avfilter-6.dll`, `avformat-57.dll`, `avutil-55.dll`, `D3DRender.dll`, `D3DX9_43.dll`, `FFDecoder.dll`, `IntelHardCodec.dll`, `js.dll`, `libEasyAACEncoder.dll`, `libEasyPlayer-RTSP.dll`, `libEasyRTSPClient.dll`, `libeay32.dll`, `libgcc_s_sjlj-1.dll`, `libgpac.dll`, `libmfxsw32.dll`, `libstdc++-6.dll`, `ssleay32.dll`, `swresample-2.dll`, `swscale-4.dll`.
    - Copy Microsoft-signed `..\..\..\Bin\C++\msvcr100.dll` and `..\..\..\Bin\C++\msvcp100.dll` into `Lib\`.
    - Explicitly do NOT include `dxva2.dll`, `msvcp140d.dll`, `ucrtbased.dll`, `vcruntime140d.dll`, `npEasyPlayerPlugin.dll`, or `ImageOle.dll`.
  - Remove legacy `Form1.cs`, `Form1.Designer.cs`, `Form1.resx`, `App.config` and add `Client` cleanly to `Src/C#/EasyPlayer-RTSP.sln`.

- [ ] **Step 2: Implement `CameraTileControl.cs`, `EditCameraDialog.cs`, `McpSettingsDialog.cs`, and `StartupRegistration.cs`**
  - `CameraTileControl`: dark video `Panel` (exposes `VideoHandle`), compact bar showing `Slot`, `Name`, `Ip:Port` (never password), status label, `Play/Stop` button, `Edit` button, double-click event to maximize/restore.
  - `EditCameraDialog`: structured fields (`Name`, `IP / Host`, `Port`, `Stream Path`, `Username`, `Password` with show/hide checkbox, `TCP/UDP`, `HardDecode`, `AutoPlay`) + "Paste full RTSP URL" textbox & "Parse" button using `RtspUrlCodec.TryParseUrl`, validates via `RtspUrlCodec.ValidateCameraInput` before accepting, plus optional Delete button when editing an existing camera.
  - `McpSettingsDialog`: displays live server status (`Listening on http://127.0.0.1:<port>` or error), editable `Port` & `Public HTTPS Base URL`, read-only copyable `MCP Server URL`, `OAuth Client ID`, masked `OAuth Client Secret` (+ copy), `Consent Passcode` (+ copy), `Regenerate Secrets` button (with confirmation dialog), and copyable `cloudflared` command.
  - `StartupRegistration`: reads/writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `WinRtspPlayer` pointing to quoted `Environment.ProcessPath` (toggled only when user checks "Start with Windows").

- [ ] **Step 3: Implement `MainForm.cs` and `Program.cs`**
  - `Program.cs`: calls `NativeDllLoader.ConfigureLibDirectory(AppContext.BaseDirectory)`, runs `MainForm`.
  - `MainForm : Form, ILivePlaybackController`:
    - Toolbar: Grid Layout dropdown (`1`, `4`, `6`, `9`, `16`), Render Format dropdown (`GDI`, `RGB565`, `YV12`, `YUY2`), `Add Camera`, `MCP Settings`, `Start with Windows` checkbox, MCP status indicator.
    - Grid layout manager (`TableLayoutPanel`, supporting 1/4/6/9/16 slots and single-tile double-click maximize/restore).
    - Calls `PlayerSdk.EasyPlayer_Init("EasyPlayer is Free!")` once on startup; opens streams with `callback: null`, `SetFrameCache`, `SetShownToScale`.
    - Starts `McpServerHost` on configured `McpPort`; subscribes to `CameraStore.CameraChanged` and marshals via `BeginInvoke` so MCP edits immediately update tile headers and reconnect/stop streams cleanly.
    - Implements `ILivePlaybackController` (`IsPlaying`, `StartCameraAsync`, `StopCameraAsync`, `RestartCameraAsync`) safely marshaling onto the UI thread when called from MCP/SnapshotService.
    - Auto-plays all cameras with `AutoPlay == true` on startup; on `FormClosing`, closes all active streams, calls `EasyPlayer_Release()`, and stops `McpServerHost`.

- [ ] **Step 4: Add `Src/C#/start-tunnel.bat` and `docs/MCP-GEMINI-SETUP.md`**
  - Concise instructions for connecting Gemini web's "Connect to an MCP server" dialog (install `cloudflared`, quick vs persistent named Cloudflare Tunnel so URL survives reboots, paste MCP URL + Client ID + Client Secret from MCP Settings, enter Consent Passcode once).

- [ ] **Step 5: Run full test suite, self-contained `win-x86` publish, and verify output DLL hygiene**
  - Run: `dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj`
    Expected: All tests PASS (0 failures).
  - Run: `dotnet publish Src/C#/Client/EasyPlayer-RTSP.Client.csproj -c Release -r win-x86 --self-contained true -o Bin/WinRtspPlayer`
    Expected: Publish succeeds with 0 errors.
  - Verify DLL hygiene in `Bin/WinRtspPlayer/Lib/`:
    - Confirm `libEasyPlayer-RTSP.dll`, `libEasyRTSPClient.dll`, `msvcr100.dll`, `msvcp100.dll` exist.
    - Confirm `dxva2.dll`, `msvcp140d.dll`, `ucrtbased.dll`, `vcruntime140d.dll`, `npEasyPlayerPlugin.dll`, `ImageOle.dll` are ABSENT.
  - Verify `WinRtspPlayer.exe` launches cleanly, loads `libEasyPlayer-RTSP.dll` without P/Invoke/CRT error, and responds on `http://127.0.0.1:8765/.well-known/oauth-protected-resource` (test in a short headless check or smoke check and close cleanly).

- [ ] **Step 6: Commit**
  Run: `git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player add Src/C# docs/MCP-GEMINI-SETUP.md .gitignore && git -C e:\My-Dev-ENV\RTSP\Win-RTSP-Player commit -m "feat: add .NET 8 multi-camera WinForms player with built-in Gemini MCP server"`
  (Ensure `Bin/WinRtspPlayer/` and `config/` are added to `.gitignore` so runtime secrets and large self-contained binaries aren't committed to git.)
