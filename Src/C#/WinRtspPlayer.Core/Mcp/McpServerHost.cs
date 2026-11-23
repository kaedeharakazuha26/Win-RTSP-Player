using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Media;

namespace WinRtspPlayer.Core.Mcp;

/// <summary>
/// Embedded ASP.NET Core Kestrel server bound strictly to <c>127.0.0.1</c> hosting OAuth 2.1 and MCP endpoints.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly CameraStore _store;
    private readonly SnapshotService _snapshots;
    private readonly OAuthAuthStore _authStore;
    private readonly ILivePlaybackController? _playback;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private WebApplication? _app;
    private volatile bool _isRunning;
    private volatile int _boundPort;
    private volatile string? _lastError;
    private volatile bool _disposed;

    public McpServerHost(
        CameraStore store,
        SnapshotService snapshots,
        OAuthAuthStore authStore,
        ILivePlaybackController? playback = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
        _playback = playback;
    }

    public bool IsRunning => _isRunning;
    public int BoundPort => _boundPort;
    public string? LastError => _lastError;

    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidatePort(port);
            if (_app is not null)
            {
                throw new InvalidOperationException("MCP server is already running.");
            }

            await StartCoreAsync(port, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(int newPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidatePort(newPort);
            await StopCoreAsync(ct).ConfigureAwait(false);
            await StartCoreAsync(newPort, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Keep _lifecycleLock undisposed (no kernel handle is allocated without AvailableWaitHandle)
            // so concurrent waiters and finally blocks never throw ObjectDisposedException.
            _lifecycleLock.Release();
        }
    }

    private void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            string message = $"MCP port must be between 1 and 65535 (got {port}).";
            if (!_isRunning)
            {
                _lastError = message;
            }

            throw new ArgumentOutOfRangeException(nameof(port), port, message);
        }
    }

    private async Task StartCoreAsync(int port, CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Logging.ClearProviders();
        builder.WebHost.SuppressStatusMessages(true);
        // Clear inherited URL/port env settings so Kestrel binds exclusively to loopback on the requested port.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseSetting("http_ports", string.Empty);
        builder.WebHost.UseSetting("https_ports", string.Empty);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

        WebApplication app = builder.Build();
        OAuthEndpoints.MapOAuthEndpoints(app, _authStore, () => _store.GetConfigSnapshot().McpPublicBaseUrl);
        McpEndpoint.MapMcpEndpoints(app, _store, _snapshots, _authStore, _playback);

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            _app = app;
            _boundPort = port;
            _isRunning = true;
            _lastError = null;
        }
        catch (Exception ex)
        {
            _isRunning = false;
            _boundPort = 0;
            _lastError = FfmpegSnapshotRunner.SanitizeErrorText(ex.Message);
            await DisposeAppQuietlyAsync(app).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        WebApplication? app = _app;
        _app = null;
        _isRunning = false;
        _boundPort = 0;

        if (app is null)
        {
            return;
        }

        try
        {
            await app.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeAppQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup after failed startup.
        }
    }
}
