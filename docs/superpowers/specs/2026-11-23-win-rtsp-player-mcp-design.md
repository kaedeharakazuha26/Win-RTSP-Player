# Win RTSP Player (.NET 8 Upgrade + Built-in Gemini MCP Server) — Design Specification

Date: 2026-11-23

## 1. Intent & Why

The user monitors local-network IP cameras on Windows and wants to eliminate the recurring frustration of manually re-entering every camera's RTSP link after shutting down or rebooting the PC. They also want to connect Google Gemini (via Gemini web's "Connect to an MCP server" custom Connected App dialog) to the player so Gemini can inspect live camera screenshots and update each camera's IP, username, and password.

## 2. Goals, Non-Goals, and Anti-Goals

### Goals
- **Lightweight live multi-camera viewer:** Upgrade `Src/C#` into a `.NET 8` Windows Forms multi-camera live RTSP player using the repo's Release native decoding engine (`Src/C#/Solution Items/Lib/libEasyPlayer-RTSP.dll`).
- **Persistent camera configuration across reboots:** Automatically save camera settings (structured IP/host, port, stream path, username, password, transport, auto-play flag, grid layout) whenever edited in the app UI or via MCP, and auto-load/auto-play enabled cameras on app startup.
- **Built-in MCP server for Google Gemini:** Embed an HTTP MCP server (Streamable HTTP transport, `/mcp`) with OAuth 2.1 (`Client ID` + `Client Secret` + PKCE `S256` + consent passcode) directly inside the desktop app so UI edits and Gemini edits stay live-synced in a single process.
- **MCP capabilities requested by user:**
  - View the app's camera list and live playback status (never exposing raw passwords).
  - Grab a live on-demand screenshot of any camera feed for Gemini to inspect.
  - Edit camera IP, username, and password (plus port/path/name/transport and adding/removing/starting/stopping cameras), persisting changes immediately and reconnecting the live tile in the app.

### Non-Goals
- **No PC video recording** (explicitly rejected by user to keep PC performance light).
- **No camera SD-card playback/browsing** (no ONVIF Profile G or vendor SD-card APIs).
- **No edits to the legacy VS2010 C++ MFC app** (`Src/C++/EasyPlayer`), ActiveX (`EasyPlayerWebActiveX`), or NPAPI browser plugin (`EasyPlayerPlugin`).
- **No custom cloud relay service:** Public HTTPS exposure for Gemini web is provided by an external tunnel (Cloudflare Tunnel recommended) configured by the user.

### Anti-Goals (Failure Modes to Avoid)
- Background CPU/disk load (no continuous recording, no polling/background frame capture when Gemini isn't asking).
- Losing saved camera links after PC shutdown or failing to find config files when launched from a shortcut or Windows Startup (must resolve paths relative to `AppContext.BaseDirectory`, never the current working directory).
- Exposing local cameras publicly on the internet without authentication, or leaking camera passwords into logs, UI screenshots, or MCP responses.
- Startup crashes from x64/x86 P/Invoke mismatch (`BadImageFormatException`) or missing VC++ 2010 CRT DLLs (`msvcr100.dll`).

## 3. Environment & Technical Constraints

- **32-bit native engine on 64-bit Windows:** All native DLLs in `Src/C#/Solution Items/Lib/` (`libEasyPlayer-RTSP.dll`, `libEasyRTSPClient.dll`, `D3DRender.dll`, `FFDecoder.dll`, `av*-57.dll`, etc.) are **32-bit x86**. The development PC has `.NET 8.0 SDK` (`8.0.428`, x64) with no x86 `.NET 8` shared runtime and no `MSVCR100.dll` in `SysWOW64`.
  - **Build & packaging requirement:** Target `net8.0-windows` with `PlatformTarget=x86`, `RuntimeIdentifier=win-x86`, and `SelfContained=true` for publish/run (`dotnet publish -r win-x86 --self-contained true`) so the app runs as a self-contained 32-bit Windows executable with zero external `.NET` runtime installs. (Unit tests that don't P/Invoke native x86 DLLs target `net8.0-windows` / `net8.0` and run under standard `dotnet test`.)
  - **Native DLL hygiene:** Copy only the required Release runtime DLLs from `Src/C#/Solution Items/Lib/` PLUS the Microsoft-signed `msvcr100.dll` and `msvcp100.dll` from `Bin/C++/` into the app output `Lib/` (and root folder for CRT resolution). Explicitly **exclude** `dxva2.dll` (unsigned Windows 7 system DLL — Windows `SysWOW64\dxva2.dll` is present and signed), debug CRTs (`msvcp140d.dll`, `ucrtbased.dll`, `vcruntime140d.dll`), `npEasyPlayerPlugin.dll`, and `ImageOle.dll`.
- **P/Invoke correctness (`PlayerSdk.cs`):** All exports in Release `libEasyPlayer-RTSP.dll` use `__cdecl` (`CallingConvention.Cdecl`), `EasyPlayer_CloseStream` returns `void`, `EasyPlayer_Init` accepts `"EasyPlayer is Free!"`, and `EasyPlayer_SetRenderRect` should use `System.Drawing.Rectangle` instead of WPF `System.Windows.Rect`. Pass `null` for `MediaSourceCallBack` during normal playback to avoid per-frame software conversion overhead (and if a callback is ever registered, keep its delegate rooted per channel).
- **On-demand screenshot engine (Our Call — handles both multi-session and 1-session cameras):** Use the installed `ffmpeg.exe` (`FFmpeg 9.0.2` on `PATH`, overrideable in `config/cameras.json`) invoked ONLY when a screenshot is requested:
  `ffmpeg -hide_banner -loglevel error -rtsp_transport <tcp|udp> -i <rtsp_url> -frames:v 1 -vf "scale='min(1280,iw)':-2" -q:v 4 -f image2pipe -vcodec mjpeg pipe:1`
  - Captures a single JPEG directly to stdout memory (zero disk writes, 10-second timeout, process killed cleanly on timeout/cancel).
  - **1-connection camera fallback:** Try standalone `ffmpeg.exe` first (zero flicker, works when minimized or stopped). If `ffmpeg.exe` fails while that camera slot is actively playing in the UI (common on cameras that enforce a 1-client RTSP limit), serialize per camera, briefly close that tile's live stream, capture 1 frame via `ffmpeg.exe`, and immediately reopen the tile's live stream.
  - **Zero extra live CPU overhead:** Pass `null` for `MediaSourceCallBack` in `EasyPlayer_OpenStream` during normal playback (because `libEasyPlayer-RTSP.dll`'s `ChannelManager.cpp` runs software RGB24 `ConvertImage` on every frame whenever a callback is registered).
- **Gemini Web MCP Transport & OAuth 2.1:**
  - Gemini web connects from Google's servers to a public `https://.../mcp` URL using MCP **Streamable HTTP** transport and OAuth 2.1 with pre-registered `Client ID` and `Client Secret` (from Gemini's "Additional settings").
  - The embedded Kestrel server listens strictly on `127.0.0.1:<port>` (default `8765`, never `0.0.0.0`) behind the user's HTTPS tunnel.
  - Because an ephemeral quick tunnel (`trycloudflare.com`) changes URL on every PC reboot, the setup docs recommend a free **named Cloudflare Tunnel** (or static HTTPS reverse proxy) so the Gemini MCP URL stays stable across reboots.

## 4. Approaches Considered

1. **Upgrade `Src/C#` to .NET 8 WinForms (x86) + built-in MCP server *(Selected by user)***
   - *Pros:* Builds immediately with installed `.NET 8 SDK`, single process with instant two-way sync between GUI and Gemini, low CPU live playback via existing Release native DLLs, clean `cameras.json` persistence, zero recording.
   - *Trade-offs:* Standard WinForms UI instead of skinned MFC chrome; still relies on the repo's legacy unsigned x86 decoding DLLs (can be swapped to FFmpeg/LibVLC later without changing the app/MCP architecture).
2. **Modify original C++ MFC app (`Src/C++/EasyPlayer`) + Python MCP sidecar *(Rejected)***
   - *Trade-offs:* Installed VS Build Tools lack MFC (`atlmfc`), and VS2010 static `.lib`s (`Detours.lib`, `SkinUI*.lib`) are high-risk to link on MSVC 14.51; requires two separate processes.
3. **Keep prebuilt `Win-rtsp-player.exe` untouched + Python wrapper & MCP server *(Rejected)***
   - *Trade-offs:* Edits inside the player UI still wouldn't persist, and the committed `Bin/C++/Win-rtsp-player.exe` is a Debug build requiring missing `MSVCR100D.dll`.

## 5. System Architecture & Decisions

### 5.1 Project Layout (Our Call)
Modernize `Src/C#/` in place while keeping solution structure clean:
- `Src/C#/EasyPlayerNetSDK/` — `net8.0-windows` class library (`PlatformTarget=x86`), fixed P/Invoke wrapper for `libEasyPlayer-RTSP.dll`.
- `Src/C#/WinRtspPlayer.Core/` — `net8.0-windows` class library containing:
  - `CameraConfig`, `AppSettings`, `CameraStore` (thread-safe persistence + events + RTSP URL builder/parser + DPAPI password encryption).
  - `SnapshotService` (on-demand `ffmpeg.exe` single-frame JPEG capture in memory).
  - `OAuthStore` & `OAuthEndpoints` (OAuth 2.1 metadata, consent passcode page, PKCE `S256`, Basic/form secret verification, DPAPI-persisted tokens).
  - `McpEndpoint` (Streamable HTTP JSON-RPC 2.0 handler & MCP tool implementations).
- `Src/C#/Client/` — `net8.0-windows` WinForms app (`PlatformTarget=x86`, `FrameworkReference` to `Microsoft.AspNetCore.App` for in-process Kestrel hosting) replacing the old single-camera demo with the multi-camera grid UI + Edit Camera dialog + MCP Settings dialog.
- `Src/C#/WinRtspPlayer.Tests/` — xUnit test project covering `CameraStore`, RTSP URL parsing/encoding, DPAPI round-trip, `OAuth` flows, and `MCP` tool handlers.

### 5.2 Persistent Configuration & Credential Protection (Our Call)
All config files live in `Path.Combine(AppContext.BaseDirectory, "config")` (resolved from the `.exe` directory, immune to shortcut working directory issues) and are written atomically (`write .tmp` → `File.Replace` / `File.Move(overwrite: true)`):
- **`config/cameras.json`**:
  - Stores `gridLayout` (`1`, `4`, `6`, `9`, `16`, default `4`), `renderFormat` (default `RGB24_GDI` for broad compatibility, selectable `YUY2`/`YV12`/`RGB565`/`GDI`), `frameCache` (default `3`), `shownToScale` (default `true`), `ffmpegPath` (default `"ffmpeg"`), `mcpPort` (default `8765`), `mcpPublicBaseUrl` (optional explicit `https://...` override), and `cameras[]`.
  - Each camera entry: `{ id, slot (1-based), name, ip, port (default 554), path (e.g. "/stream1"), username, encryptedPassword (DPAPI CurrentUser base64), transport ("tcp"|"udp", default "tcp"), hardDecode (default false), autoPlay (default true) }`.
  - **URL composition:** Builds `rtsp://<user>:<pass>@<ip>:<port><path>` (percent-encoding special characters in username/password; omitting credentials if username is empty). Also supports pasting a full `rtsp://...` URL in the UI Edit dialog to auto-parse into `ip`, `port`, `path`, `username`, `password`.
  - **One-time `rtsp.txt` import:** On first launch when `config/cameras.json` does not exist, if `rtsp.txt` exists next to the `.exe` (or in `Bin/C++`), parse non-empty lines into slots 1..N and save `cameras.json`.
- **`config/mcp-auth.json`**:
  - Auto-generated on first run with cryptographically random `clientId`, `clientSecret`, and `consentPasscode` (all secrets stored DPAPI-encrypted for `CurrentUser`).
  - Persists issued OAuth `access_tokens` and `refresh_tokens` (DPAPI-encrypted, storing SHA-256 token hashes + expiry) so Gemini stays connected across PC reboots without re-authorizing.

### 5.3 Desktop UI Behavior
- **Default view:** Clean multi-camera grid (default `2x2` = 4 cameras, switchable to `1 / 4 / 6 / 9 / 16` via toolbar dropdown; choice persisted).
- **Per-camera tile:**
  - Live video surface (`Panel` passed to `EasyPlayer_OpenStream`). Double-click to maximize a single camera; double-click again to return to grid.
  - Compact header/footer bar: Slot number + Camera Name + `IP:Port` (never displaying password), status dot/text (Playing / Connecting / Stopped / Error), `Play/Stop` button, and `Edit` button.
- **Edit Camera dialog:** Structured inputs for `Name`, `IP / Host`, `Port`, `Stream Path`, `Username`, `Password` (masked with Show/Hide toggle), `TCP/UDP`, `Hardware Decode`, `Auto-play on startup`, plus a quick **"Paste full RTSP URL"** box that parses and populates the fields. Clicking Save persists `cameras.json` immediately and reconnects that tile live if playing/auto-play.
- **MCP Settings dialog:**
  - Shows embedded MCP server status (`Listening on http://127.0.0.1:8765`), editable port & optional `Public HTTPS Base URL`.
  - Displays copyable **MCP Server URL** (`<publicBaseUrl>/mcp`), **OAuth Client ID**, **OAuth Client Secret** (masked + copy button), and **Consent Passcode** (used once during Gemini browser authorization), plus a **Regenerate Secrets** button (which also revokes existing tokens) and copyable `cloudflared` setup hint.
- **Startup & shutdown:**
  - On load: initialize `EasyPlayer_Init("EasyPlayer is Free!")`, start Kestrel MCP server, load `cameras.json`, and auto-play all configured cameras with `autoPlay == true`.
  - Optional **"Start with Windows"** checkbox in toolbar/settings (toggles `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WinRtspPlayer`, off by default).
  - On exit: cleanly close all active RTSP streams, call `EasyPlayer_Release()`, and stop Kestrel gracefully.
- **Live sync with MCP:** `CameraStore` fires events whenever MCP adds, updates, removes, or starts/stops a camera; the main form marshals via `BeginInvoke` to update tile metadata and close/reopen the affected stream immediately.

### 5.4 Built-in MCP Server & OAuth 2.1 Design
- **Host & reverse proxy handling:** Embedded ASP.NET Core Kestrel binds to `http://127.0.0.1:<mcpPort>`. Determines public base URL from `mcpPublicBaseUrl` if configured, otherwise from `X-Forwarded-Proto` + `X-Forwarded-Host` headers (falling back to `Request.Scheme://Request.Host`).
- **OAuth 2.1 endpoints (aligned with MCP Authorization Spec & Gemini web dialog):**
  - `GET /.well-known/oauth-protected-resource` and `GET /.well-known/oauth-protected-resource/mcp`:
    Returns `{ "resource": "<baseUrl>/mcp", "authorization_servers": ["<baseUrl>"], "bearer_methods_supported": ["header"] }`.
  - `GET /.well-known/oauth-authorization-server`:
    Returns RFC 8414 metadata (`issuer`, `authorization_endpoint: <baseUrl>/oauth/authorize`, `token_endpoint: <baseUrl>/oauth/token`, `response_types_supported: ["code"]`, `grant_types_supported: ["authorization_code", "refresh_token"]`, `code_challenge_methods_supported: ["S256"]`, `token_endpoint_auth_methods_supported: ["client_secret_basic", "client_secret_post"]`). No open Dynamic Client Registration endpoint (clients must use the app's pre-registered `Client ID` + `Client Secret`).
  - `GET /oauth/authorize` & `POST /oauth/authorize`:
    - Validates `response_type=code`, exact `client_id`, `code_challenge` + `code_challenge_method=S256`, and `redirect_uri` (must be `https://` on an allowlisted host: `oauth-redirect.googleusercontent.com`, `gemini.google.com`, `*.googleusercontent.com`, `*.google.com`, or `http://localhost` / `http://127.0.0.1` for local dev).
    - `GET` renders a minimal HTML consent page asking for the user's **Consent Passcode** (and displays the requesting client & redirect host).
    - `POST` verifies the passcode with constant-time comparison and basic lockout/rate-limiting (e.g., max 5 failed attempts per minute), generates a 5-minute single-use authorization code bound to `(client_id, redirect_uri, code_challenge, resource)`, and issues `302 Found` to exact `redirect_uri` with `code` and unmodified `state`.
  - `POST /oauth/token`:
    - Authenticates `client_id` + `client_secret` via EITHER HTTP `Authorization: Basic ...` (`client_secret_basic`) OR form body (`client_secret_post`) using constant-time comparison.
    - Supports `grant_type=authorization_code` (verifies single-use code, expiry, exact `redirect_uri`, and PKCE `S256` `code_verifier`) and `grant_type=refresh_token` (rotates/issues new access token).
    - Returns `{ access_token, token_type: "Bearer", expires_in: 3600, refresh_token }`.
- **MCP Streamable HTTP endpoint (`/mcp`):**
  - Requires valid `Authorization: Bearer <access_token>`. Unauthenticated/expired requests return `401 Unauthorized` with `WWW-Authenticate: Bearer resource_metadata="<baseUrl>/.well-known/oauth-protected-resource"`.
  - `POST /mcp` handles JSON-RPC 2.0 requests (`initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call`) returning `application/json` (or `202 Accepted` for notifications), supporting `MCP-Session-Id` and protocol versions `2025-06-18` / `2025-03-26` / `2024-11-05`. `DELETE /mcp` ends a session (`204 No Content`). `GET /mcp` returns `405 Method Not Allowed` (stateless request/response Streamable HTTP).
- **MCP Tools:**
  1. `list_cameras`: No required args. Returns JSON list of cameras `{ id, slot, name, ip, port, path, username, has_password, transport, auto_play, is_playing }` plus `grid_layout`. Never returns passwords.
  2. `get_camera_screenshot`: Args `{ camera: string }` (matches camera `id`, 1-based `slot`, or case-insensitive `name`). Captures 1 live JPEG frame via `ffmpeg.exe` into memory (never writes to disk, max width 1280px, 10s timeout; if the camera rejects a second concurrent RTSP connection while playing, automatically pauses that tile briefly, grabs the frame, and resumes playback) and returns MCP `image` content (`mimeType: "image/jpeg"`, base64 `data`) + `text` content with camera name, slot, resolution, and capture timestamp. Returns clear `isError: true` message on unreachable camera, auth failure, or missing `ffmpeg`.
  3. `update_camera`: Args `{ camera: string, name?, ip?, port?, path?, username?, password?, clear_password?, transport?, auto_play? }`. Validates inputs (valid hostname/IP without whitespace or `@`/`/` injection, port 1–65535, path starting with `/` or empty, transport `tcp` or `udp`). Updates only provided fields, persists to `cameras.json`, and immediately triggers live reload of that slot in the GUI. Returns updated sanitized camera object.
  4. `add_camera`: Args `{ name, ip, port?, path?, username?, password?, transport?, slot?, auto_play? }`. Adds to requested empty slot or next available slot (1–16), persists, and starts live view if `auto_play` is true.
  5. `remove_camera`: Args `{ camera: string }`. Stops live stream, removes camera from `cameras.json`, clears tile.
  6. `control_camera`: Args `{ camera: string, action: "start" | "stop" | "restart" }`. Controls live playback of that tile in the GUI.

### 5.5 What Is Left to the Builder
- Exact WinForms visual styling, spacing, fonts, and dialog control layout.
- Exact JSON-RPC helper classes vs official C# MCP SDK NuGet package (either is fine, provided Streamable HTTP + custom OAuth 2.1 endpoints + image tool result match Section 5.4 cleanly and keep dependencies minimal).
- Helper launcher script (e.g., `start-tunnel.bat` / PowerShell snippet) for running `cloudflared` alongside the app.
