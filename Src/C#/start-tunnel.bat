@echo off
setlocal

set "MCP_PORT=%~1"
if "%MCP_PORT%"=="" set "MCP_PORT=8765"

where cloudflared >nul 2>&1
if errorlevel 1 (
    echo cloudflared was not found in PATH.
    echo Install it with: winget install Cloudflare.cloudflared
    pause
    exit /b 1
)

echo Starting Cloudflare Quick Tunnel for http://127.0.0.1:%MCP_PORT% ...
echo.
echo NOTE: Quick Tunnel generates a temporary https://*.trycloudflare.com URL on every run.
echo For a permanent HTTPS URL that stays constant across PC reboots, create a free
echo named Cloudflare Tunnel instead:
echo   cloudflared tunnel login
echo   cloudflared tunnel create winrtsp-mcp
echo   cloudflared tunnel route dns winrtsp-mcp cameras.yourdomain.com
echo   cloudflared tunnel run winrtsp-mcp
echo (See docs/MCP-GEMINI-SETUP.md for full instructions.)
echo.

cloudflared tunnel --url http://127.0.0.1:%MCP_PORT%
