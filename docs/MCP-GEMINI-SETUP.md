# Connecting Win RTSP Player to Google Gemini via MCP

Win RTSP Player includes an embedded local MCP server (`http://127.0.0.1:8765/mcp` by default) protected by OAuth 2.1 with PKCE (`S256`) and a consent passcode. Because Gemini web connects over the public internet, you expose the loopback MCP endpoint over HTTPS using a Cloudflare Tunnel (`cloudflared`).

## Prerequisites

- **Win RTSP Player** built or published (`Bin\WinRtspPlayer\WinRtspPlayer.exe`)
- **FFmpeg** installed and available on `PATH` (for on-demand `get_camera_screenshot` capture; verify with `ffmpeg -version`)
- **Cloudflare Tunnel (`cloudflared`)** installed:
  ```powershell
  winget install Cloudflare.cloudflared
  ```

---

## Step 1: Launch Win RTSP Player & Open MCP Settings

1. Run `WinRtspPlayer.exe`.
2. Add or verify your RTSP cameras (passwords are encrypted at rest with Windows DPAPI).
3. Click **MCP Settings** in the top toolbar (or click the **MCP** status link in the bottom status bar).
4. Confirm the status shows `Listening on http://127.0.0.1:8765` (or your configured port).

Keep this dialog open or return to it after starting your Cloudflare Tunnel.

---

## Step 2: Set Up Cloudflare Tunnel (`cloudflared`)

Choose **Option A** for a quick test, or **Option B** (recommended) for a permanent URL that survives PC restarts.

### Option A: Quick Tunnel (Temporary URL for Testing)

Run `Src\C#\start-tunnel.bat` (or run directly in PowerShell):

```powershell
cloudflared tunnel --url http://127.0.0.1:8765
```

`cloudflared` prints a temporary public URL such as `https://random-words.trycloudflare.com`. Every restart generates a new URL, so you would need to re-enter the URL in Gemini if you restart the tunnel.

### Option B: Free Named Cloudflare Tunnel (Recommended — Permanent URL)

If you have a domain managed on Cloudflare DNS (Cloudflare Tunnel itself is free):

1. Authenticate `cloudflared` with your Cloudflare account:
   ```powershell
   cloudflared tunnel login
   ```
2. Create a named tunnel:
   ```powershell
   cloudflared tunnel create winrtsp-mcp
   ```
3. Route a subdomain to the tunnel:
   ```powershell
   cloudflared tunnel route dns winrtsp-mcp cameras.yourdomain.com
   ```
4. Create `%USERPROFILE%\.cloudflared\config.yml` (replacing `<TUNNEL-UUID>` with the ID printed in step 2):
   ```yaml
   tunnel: <TUNNEL-UUID>
   credentials-file: C:\Users\<YOUR-USER>\.cloudflared\<TUNNEL-UUID>.json

   ingress:
     - hostname: cameras.yourdomain.com
       service: http://127.0.0.1:8765
     - service: http_status:404
   ```
5. Start the tunnel (or install it as a Windows service with `cloudflared service install`):
   ```powershell
   cloudflared tunnel run winrtsp-mcp
   ```

Back in **MCP Settings**, set **Public HTTPS Base URL** to your tunnel origin (for example `https://cameras.yourdomain.com` without a trailing slash or `/mcp`) and click **Save**. This ensures OAuth discovery metadata (`/.well-known/oauth-protected-resource`) advertises your public HTTPS URL cleanly.

---

## Step 3: Connect in Gemini Web

1. Open Google Gemini in your browser and open **Connect to an MCP server** (or Custom MCP connection settings).
2. Copy these values from Win RTSP Player's **MCP Settings** dialog:
   - **MCP Server URL**: `https://<your-tunnel-domain>/mcp` (for example `https://cameras.yourdomain.com/mcp`)
   - **OAuth Client ID**: copy from **OAuth Client ID**
   - **OAuth Client Secret**: click **Show** or **Copy** next to **OAuth Client Secret**
3. Submit the connection form. Gemini redirects your browser to Win RTSP Player's built-in `/oauth/authorize` approval page.

---

## Step 4: Enter the Consent Passcode

1. On the browser authorization screen, enter the **Consent Passcode** shown in **MCP Settings** (click **Show** or **Copy**).
2. Click **Approve** to complete the OAuth 2.1 PKCE handshake and issue access/refresh tokens back to Gemini.
   > **Note:** After 5 consecutive failed passcode attempts within 60 seconds, `/oauth/authorize` returns HTTP 429 briefly to protect against brute-force guesses.
   > **Security tip:** Clicking **Regenerate Secrets** in **MCP Settings** immediately invalidates all issued tokens and rotates the Client ID, Client Secret, and Consent Passcode.

---

## Step 5: Example Prompts & Available Tools

Once connected, Gemini can invoke these 6 MCP tools:

- `list_cameras` — Lists all cameras (`id`, `slot`, `name`, `ip`, `port`, `path`, `username`, `has_password`, `transport`, `hard_decode`, `auto_play`, `is_playing`) without ever exposing passwords.
- `get_camera_screenshot` — Captures a live JPEG frame (max width `1280px`) via `ffmpeg` in memory; if a single-connection camera rejects a second RTSP connection, Win RTSP Player temporarily pauses the live tile, captures the frame, and resumes live playback.
- `add_camera` — Adds a new camera to a target slot (`1–16`) or the next free slot.
- `update_camera` — Updates camera name, IP/host, port, stream path, credentials, transport (`tcp`/`udp`), slot, `auto_play`, or `hard_decode` and refreshes live playback.
- `remove_camera` — Removes a camera and stops its live tile.
- `control_camera` — Starts, stops, or restarts live playback (`start`, `stop`, `restart`).

Example prompts you can ask Gemini:

- *"List my cameras and tell me which ones are currently playing."*
- *"Show me a screenshot of Front Door."*
- *"Update camera 2 IP to 192.168.1.55 and password to MyNewCameraPass!"*
- *"Add a camera named Garage on slot 3 at 192.168.1.80 port 554 path /h264Preview_01_main."*
- *"Restart the Backyard camera."*
