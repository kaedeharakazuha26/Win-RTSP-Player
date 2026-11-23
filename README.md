# Win RTSP Player #

Look at my new project [AnotherRTSP](https://github.com/e1z0/AnotherRTSP).

<img src="https://raw.githubusercontent.com/e1z0/Win-RTSP-Player/master/pics/player_in_action.png" data-canonical-src="https://raw.githubusercontent.com/e1z0/Win-RTSP-Player/master/pics/player_in_action.png" width="600"/>


This RTSP Windows player is a fork of complete RTSP streaming media player project developed by TSINGSEE Qingxi open platform. Video encoding supports H.264, H.265, MPEG4, MJPEG, and audio supports G711A, G711U, G726, AAC. Supports RTSP over TCP/UDP protocol, supports soft/hard decoding, and is an excellent set of playback components for security streaming media platforms! Windows version has gone through many years of development and iteration, and has been used in many commercial projects. It has become very stable and complete. Its functions include: live broadcast, video recording, and screenshots. It should be said that it has the most functionality, stability and completeness on the market. A powerful RTSP player!


## Features ##

- [x] Ultra-low latency RTSP player;
- [x] Super device compatibility and customizability;
- [x] Perfectly supports multi-window and multi-instance playback;
- [x] Support RTSP TCP/UDP mode switching;
- [x] Supports real-time setting of the playback buffer and mature low-latency frame tracking technology;
- [x] Turn on the playback function in seconds;
- [x] Support custom playback layout;
- [x] The codec, display, and playback source codes are all open and more flexible;
- [x] Support OSD text watermark;
- [x] Detailed log output callback;

## This fork features (.NET 8 + Gemini MCP) ##

- [x] **Multi-camera live RTSP viewer:** Upgraded `.NET 8` (`win-x86`) WinForms player supporting `1`, `4`, `9`, or `16` camera tiles (live feed only — no PC video recording).
- [x] **Persistent camera configuration across reboots:** Camera links, credentials, and grid layout save automatically to `config/cameras.json` next to `WinRtspPlayer.exe` (passwords encrypted with Windows DPAPI `CurrentUser`), with automatic first-run migration from legacy `rtsp.txt`.
- [x] **Built-in OAuth 2.1 Streamable HTTP MCP server:** Embedded loopback server (`/mcp` on `127.0.0.1:8765`) for Google Gemini with 6 tools (`list_cameras`, `get_camera_screenshot`, `update_camera`, `add_camera`, `remove_camera`, `control_camera`).
- [x] **On-demand in-memory JPEG screenshots:** Uses `ffmpeg` in memory (never writes video or images to disk) with automatic brief pause/resume fallback for 1-connection cameras.
- [x] English UI & auto-playback on startup.

## Build & Run (.NET 8 App) ##

### Prerequisites ###

* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build from source)
* [FFmpeg](https://ffmpeg.org/) (`ffmpeg.exe` on `PATH` or configured in Settings) for MCP camera screenshots

### Publish & Run ###

```powershell
dotnet publish Src/C#/Client/EasyPlayer-RTSP.Client.csproj -c Release -r win-x86 --self-contained true -o Bin/WinRtspPlayer
```

Run `Bin/WinRtspPlayer/WinRtspPlayer.exe`.

> **Important:** Keep the entire `Bin/WinRtspPlayer/` folder intact if you move or copy the app — `WinRtspPlayer.exe` requires `WinRtspPlayer.dll`, the self-contained `.NET 8` runtime DLLs, and the `Lib/` folder in the same directory.

## Run Tests ##

```powershell
dotnet test Src/C#/WinRtspPlayer.Tests/WinRtspPlayer.Tests.csproj
```

## Connect Google Gemini (MCP) ##

See [docs/MCP-GEMINI-SETUP.md](docs/MCP-GEMINI-SETUP.md) for step-by-step setup instructions, and use [Src/C#/start-tunnel.bat](Src/C#/start-tunnel.bat) to expose `http://127.0.0.1:8765` over HTTPS via Cloudflare Tunnel.
