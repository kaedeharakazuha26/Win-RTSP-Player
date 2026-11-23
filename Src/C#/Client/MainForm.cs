using System.Collections.Concurrent;
using EasyPlayerNetSDK;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Mcp;
using WinRtspPlayer.Core.Media;

namespace WinRtspPlayer.Client;

/// <summary>
/// Main multi-camera live RTSP player window hosting up to 16 video tiles, persistent camera config,
/// and the embedded local MCP/OAuth server for Gemini integration.
/// </summary>
public sealed class MainForm : Form, ILivePlaybackController
{
    private static readonly int[] ValidGridLayouts = [1, 4, 6, 9, 16];

    private readonly CameraStore _store;
    private readonly OAuthAuthStore _authStore;
    private readonly SnapshotService _snapshotService;
    private readonly McpServerHost _mcpHost;
    private readonly ConcurrentDictionary<string, int> _activeChannels = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly CameraTileControl[] _tiles = new CameraTileControl[CameraStore.MaxSlots];

    private readonly ToolStrip _toolbar;
    private readonly ToolStripComboBox _gridComboBox;
    private readonly ToolStripComboBox _renderComboBox;
    private readonly ToolStripButton _addCameraButton;
    private readonly ToolStripButton _mcpSettingsButton;
    private readonly ToolStripControlHost _startupCheckBoxHost;
    private readonly CheckBox _startupCheckBox;
    private readonly ToolStripStatusLabel _mcpStatusLabel;
    private readonly StatusStrip _statusStrip;
    private readonly TableLayoutPanel _gridPanel;

    private bool _sdkInitialized;
    private string? _sdkError;
    private bool _suppressToolbarEvents;
    private volatile bool _isClosing;
    private int? _maximizedSlot;
    private Icon? _appIcon;
    private PlayerSdk.RENDER_FORMAT _renderFormat = PlayerSdk.RENDER_FORMAT.DISPLAY_FORMAT_RGB24_GDI;

    public MainForm()
    {
        Text = "Win RTSP Player";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 21, 27);

        if (!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            Icon = _appIcon;
        }

        string baseDir = AppContext.BaseDirectory;
        var protector = new DpapiSecretProtector();
        _store = new CameraStore(baseDir, protector);
        _authStore = new OAuthAuthStore(baseDir, protector);
        _snapshotService = new SnapshotService(_store, new FfmpegSnapshotRunner(), this);
        _mcpHost = new McpServerHost(_store, _snapshotService, _authStore, this);

        _toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 3, 6, 3)
        };

        _gridComboBox = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 62
        };
        _gridComboBox.Items.AddRange(["1", "4", "6", "9", "16"]);
        _gridComboBox.SelectedIndexChanged += OnGridLayoutChanged;

        _renderComboBox = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 86
        };
        _renderComboBox.Items.AddRange(["GDI", "RGB565", "YV12", "YUY2"]);
        _renderComboBox.SelectedIndexChanged += OnRenderFormatChanged;

        _addCameraButton = new ToolStripButton("Add Camera");
        _addCameraButton.Click += OnToolbarAddCameraClick;

        _mcpSettingsButton = new ToolStripButton("MCP Settings");
        _mcpSettingsButton.Click += OnMcpSettingsClick;

        _startupCheckBox = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(6, 2, 4, 2)
        };
        _startupCheckBox.CheckedChanged += OnStartupCheckChanged;
        _startupCheckBoxHost = new ToolStripControlHost(_startupCheckBox);

        _toolbar.Items.Add(new ToolStripLabel("Grid:"));
        _toolbar.Items.Add(_gridComboBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("Render:"));
        _toolbar.Items.Add(_renderComboBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_addCameraButton);
        _toolbar.Items.Add(_mcpSettingsButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_startupCheckBoxHost);

        _statusStrip = new StatusStrip();
        _mcpStatusLabel = new ToolStripStatusLabel("MCP: Starting...")
        {
            IsLink = true
        };
        _mcpStatusLabel.Click += OnMcpSettingsClick;
        _statusStrip.Items.Add(_mcpStatusLabel);

        _gridPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 21, 27),
            Padding = new Padding(4),
            Margin = Padding.Empty
        };

        for (int i = 0; i < CameraStore.MaxSlots; i++)
        {
            int slot = i + 1;
            var tile = new CameraTileControl(slot);
            tile.PlayStopClicked += (_, _) => OnTilePlayStopClicked(tile);
            tile.EditClicked += (_, _) => OnTileEditClicked(tile);
            tile.SetVideoDoubleClick((_, _) => OnTileDoubleClicked(tile));
            _tiles[i] = tile;
        }

        Controls.Add(_gridPanel);
        Controls.Add(_toolbar);
        Controls.Add(_statusStrip);
    }

    public bool IsPlaying(string cameraId)
    {
        if (_isClosing || string.IsNullOrWhiteSpace(cameraId))
        {
            return false;
        }

        return _activeChannels.ContainsKey(cameraId);
    }

    public Task<bool> StartCameraAsync(string cameraId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(ct);
        }

        return RunOnUiAsync(() => StartCameraCore(cameraId), defaultValue: false, ct);
    }

    public Task StopCameraAsync(string cameraId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled(ct);
        }

        return RunOnUiAsync(() =>
        {
            StopCameraCore(cameraId);
            return true;
        }, defaultValue: false, ct);
    }

    public Task<bool> RestartCameraAsync(string cameraId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(ct);
        }

        return RunOnUiAsync(() =>
        {
            StopCameraCore(cameraId);
            return StartCameraCore(cameraId);
        }, defaultValue: false, ct);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            int initResult = PlayerSdk.EasyPlayer_Init("EasyPlayer is Free!");
            _sdkInitialized = initResult >= 0;
            if (!_sdkInitialized)
            {
                _sdkError = $"EasyPlayer_Init returned {initResult}";
            }
        }
        catch (Exception ex)
        {
            _sdkInitialized = false;
            _sdkError = FfmpegSnapshotRunner.SanitizeErrorText(ex.Message);
        }

        string localRtspTxt = Path.Combine(AppContext.BaseDirectory, "rtsp.txt");
        string siblingCppRtspTxt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "C++", "rtsp.txt"));
        string? legacyRtspTxt = File.Exists(localRtspTxt)
            ? localRtspTxt
            : (File.Exists(siblingCppRtspTxt) ? siblingCppRtspTxt : null);

        AppConfig config = _store.LoadOrCreate(legacyRtspTxt);
        _store.CameraChanged += OnStoreCameraChanged;

        _suppressToolbarEvents = true;
        try
        {
            _gridComboBox.SelectedItem = config.GridLayout.ToString();
            if (_gridComboBox.SelectedIndex < 0)
            {
                _gridComboBox.SelectedItem = "4";
            }

            string renderLabel = ToRenderLabel(config.RenderFormat);
            _renderComboBox.SelectedItem = renderLabel;
            _renderFormat = ToSdkRenderFormat(renderLabel);

            _startupCheckBox.Checked = StartupRegistration.IsEnabled();
        }
        finally
        {
            _suppressToolbarEvents = false;
        }

        RefreshTilesFromStore(config);
        RebuildGridLayout(config.GridLayout);

        await StartMcpHostAsync(config.McpPort);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_isClosing || IsDisposed)
        {
            return;
        }

        AppConfig config = _store.GetConfigSnapshot();
        foreach (CameraConfig cam in config.Cameras.Where(c => c.AutoPlay))
        {
            StartCameraCore(cam.Id);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (CameraTileControl tile in _tiles)
            {
                tile.Dispose();
            }

            _appIcon?.Dispose();
            _appIcon = null;
            _shutdownCts.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isClosing = true;
        _store.CameraChanged -= OnStoreCameraChanged;
        _shutdownCts.Cancel();

        foreach (string cameraId in _activeChannels.Keys.ToArray())
        {
            StopCameraCore(cameraId);
        }

        if (_sdkInitialized)
        {
            _sdkInitialized = false;
            try
            {
                PlayerSdk.EasyPlayer_Release();
            }
            catch
            {
                // Best-effort native SDK teardown during exit.
            }
        }

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task.Run(async () =>
            {
                await _mcpHost.StopAsync(stopCts.Token).ConfigureAwait(false);
                await _mcpHost.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Do not hang application exit if Kestrel shutdown exceeds timeout.
        }

        base.OnFormClosing(e);
    }

    private async Task StartMcpHostAsync(int port)
    {
        try
        {
            await _mcpHost.StartAsync(port, _shutdownCts.Token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Error details are exposed via _mcpHost.LastError without crashing the player.
        }

        if (!_isClosing && !IsDisposed)
        {
            UpdateMcpStatusDisplay();
        }
    }

    private void UpdateMcpStatusDisplay()
    {
        string sdkSuffix = _sdkError is null ? string.Empty : $"  |  SDK Error: {_sdkError}";
        if (_mcpHost.IsRunning)
        {
            _mcpStatusLabel.Text = $"MCP: http://127.0.0.1:{_mcpHost.BoundPort}/mcp{sdkSuffix}";
            _mcpStatusLabel.LinkColor = _sdkError is null ? Color.DarkGreen : Color.DarkOrange;
            _mcpStatusLabel.ToolTipText = _sdkError ?? "Click to configure MCP and Gemini OAuth settings";
        }
        else if (!string.IsNullOrWhiteSpace(_mcpHost.LastError))
        {
            _mcpStatusLabel.Text = $"MCP Error: {_mcpHost.LastError}{sdkSuffix}";
            _mcpStatusLabel.LinkColor = Color.Firebrick;
            _mcpStatusLabel.ToolTipText = _mcpHost.LastError;
        }
        else
        {
            _mcpStatusLabel.Text = $"MCP: Stopped{sdkSuffix}";
            _mcpStatusLabel.LinkColor = SystemColors.GrayText;
            _mcpStatusLabel.ToolTipText = _sdkError ?? "Click to configure MCP settings";
        }
    }

    private void OnStoreCameraChanged(CameraChangeEvent evt)
    {
        if (_isClosing || IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => HandleCameraChangedOnUi(evt));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Form is closing; ignore late background notifications.
            }

            return;
        }

        HandleCameraChangedOnUi(evt);
    }

    private void HandleCameraChangedOnUi(CameraChangeEvent evt)
    {
        if (_isClosing || IsDisposed || Disposing)
        {
            return;
        }

        bool wasPlaying = _activeChannels.ContainsKey(evt.Camera.Id);
        if (evt.Kind is CameraChangeKind.Removed or CameraChangeKind.Updated)
        {
            StopCameraCore(evt.Camera.Id);
        }

        AppConfig config = _store.GetConfigSnapshot();
        if (evt.Kind is CameraChangeKind.Added or CameraChangeKind.Updated &&
            evt.Camera.Slot > config.GridLayout)
        {
            int expandedGrid = ValidGridLayouts.FirstOrDefault(g => g >= evt.Camera.Slot, 16);
            _store.UpdateSettings(s => s.GridLayout = expandedGrid);
            config = _store.GetConfigSnapshot();
            _suppressToolbarEvents = true;
            try
            {
                _gridComboBox.SelectedItem = expandedGrid.ToString();
            }
            finally
            {
                _suppressToolbarEvents = false;
            }
        }

        RefreshTilesFromStore(config);
        RebuildGridLayout(config.GridLayout);

        if (evt.Kind == CameraChangeKind.Added && evt.Camera.AutoPlay)
        {
            StartCameraCore(evt.Camera.Id);
        }
        else if (evt.Kind == CameraChangeKind.Updated && (wasPlaying || evt.Camera.AutoPlay))
        {
            StartCameraCore(evt.Camera.Id);
        }
    }

    private void RefreshTilesFromStore(AppConfig config)
    {
        var bySlot = config.Cameras.ToDictionary(c => c.Slot);
        for (int i = 0; i < _tiles.Length; i++)
        {
            int slot = i + 1;
            CameraTileControl tile = _tiles[i];
            string? previousCameraId = tile.CameraId;
            bySlot.TryGetValue(slot, out CameraConfig? cam);
            tile.SetCamera(cam);
            if (cam is not null)
            {
                if (_activeChannels.ContainsKey(cam.Id))
                {
                    tile.SetStatus(TilePlaybackState.Playing);
                }
                else if (!(string.Equals(previousCameraId, cam.Id, StringComparison.Ordinal) &&
                           tile.PlaybackState == TilePlaybackState.Error))
                {
                    tile.SetStatus(TilePlaybackState.Stopped);
                }
            }
        }
    }

    private void RebuildGridLayout(int gridCount)
    {
        int effectiveGrid = Array.IndexOf(ValidGridLayouts, gridCount) >= 0 ? gridCount : 4;

        _gridPanel.SuspendLayout();
        try
        {
            _gridPanel.Controls.Clear();
            _gridPanel.ColumnStyles.Clear();
            _gridPanel.RowStyles.Clear();

            if (_maximizedSlot is int maxSlot && maxSlot is >= 1 and <= CameraStore.MaxSlots)
            {
                _gridPanel.ColumnCount = 1;
                _gridPanel.RowCount = 1;
                _gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                _gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

                for (int i = 0; i < _tiles.Length; i++)
                {
                    CameraTileControl tile = _tiles[i];
                    bool isTarget = tile.SlotNumber == maxSlot;
                    tile.Visible = isTarget;
                    if (isTarget)
                    {
                        _gridPanel.Controls.Add(tile, 0, 0);
                    }
                }

                return;
            }

            (int cols, int rows) = effectiveGrid switch
            {
                1 => (1, 1),
                4 => (2, 2),
                6 => (3, 2),
                9 => (3, 3),
                16 => (4, 4),
                _ => (2, 2)
            };

            _gridPanel.ColumnCount = cols;
            _gridPanel.RowCount = rows;
            for (int c = 0; c < cols; c++)
            {
                _gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            }

            for (int r = 0; r < rows; r++)
            {
                _gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            }

            for (int i = 0; i < _tiles.Length; i++)
            {
                CameraTileControl tile = _tiles[i];
                if (i < effectiveGrid)
                {
                    tile.Visible = true;
                    int col = i % cols;
                    int row = i / cols;
                    _gridPanel.Controls.Add(tile, col, row);
                }
                else
                {
                    tile.Visible = false;
                }
            }
        }
        finally
        {
            _gridPanel.ResumeLayout();
        }
    }

    private bool StartCameraCore(string cameraId)
    {
        if (_isClosing || IsDisposed || string.IsNullOrWhiteSpace(cameraId))
        {
            return false;
        }

        if (_activeChannels.ContainsKey(cameraId))
        {
            return true;
        }

        CameraConfig? cam = _store.FindCamera(cameraId);
        if (cam is null || cam.Slot is < 1 or > CameraStore.MaxSlots)
        {
            return false;
        }

        CameraTileControl tile = _tiles[cam.Slot - 1];
        if (!_sdkInitialized)
        {
            tile.SetStatus(TilePlaybackState.Error, "EasyPlayer native SDK is not initialized.");
            return false;
        }

        tile.SetStatus(TilePlaybackState.Connecting);

        AppConfig config = _store.GetConfigSnapshot();
        string rtspUrl;
        try
        {
            rtspUrl = _store.BuildRtspUrl(cam);
        }
        catch (Exception)
        {
            tile.SetStatus(TilePlaybackState.Error, "Failed to decrypt saved password.");
            return false;
        }

        int rtpOverTcp = string.Equals(cam.Transport, "udp", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        int channelId = PlayerSdk.EasyPlayer_OpenStream(
            rtspUrl,
            tile.VideoHandle,
            _renderFormat,
            rtpOverTcp,
            string.Empty,
            string.Empty,
            null,
            IntPtr.Zero,
            cam.HardDecode);

        if (channelId <= 0)
        {
            tile.SetStatus(TilePlaybackState.Error, $"Failed to open RTSP stream (code {channelId}).");
            return false;
        }

        _activeChannels[cam.Id] = channelId;
        PlayerSdk.EasyPlayer_SetFrameCache(channelId, config.FrameCache);
        PlayerSdk.EasyPlayer_SetShownToScale(channelId, config.ShownToScale ? 1 : 0);
        tile.SetStatus(TilePlaybackState.Playing);
        return true;
    }

    private void StopCameraCore(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            return;
        }

        if (_activeChannels.TryRemove(cameraId, out int channelId) && channelId > 0)
        {
            try
            {
                PlayerSdk.EasyPlayer_CloseStream(channelId);
            }
            catch
            {
                // Ignore native close exceptions during disconnect/teardown.
            }
        }

        CameraConfig? cam = _store.FindCamera(cameraId);
        if (cam is not null && cam.Slot is >= 1 and <= CameraStore.MaxSlots)
        {
            _tiles[cam.Slot - 1].SetStatus(TilePlaybackState.Stopped);
        }
        else
        {
            foreach (CameraTileControl tile in _tiles)
            {
                if (string.Equals(tile.CameraId, cameraId, StringComparison.Ordinal))
                {
                    tile.SetStatus(TilePlaybackState.Stopped);
                }
            }
        }
    }

    private async Task<T> RunOnUiAsync<T>(Func<T> action, T defaultValue, CancellationToken ct)
    {
        if (_isClosing || IsDisposed || Disposing || !IsHandleCreated)
        {
            return defaultValue;
        }

        if (!InvokeRequired)
        {
            return (_isClosing || IsDisposed || Disposing || !IsHandleCreated)
                ? defaultValue
                : action();
        }

        CancellationToken shutdownToken;
        CancellationTokenRegistration ctReg = default;
        CancellationTokenRegistration shutdownReg = default;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            shutdownToken = _shutdownCts.Token;
            ctReg = ct.Register(static state =>
            {
                var (source, token) = ((TaskCompletionSource<T>, CancellationToken))state!;
                source.TrySetCanceled(token);
            }, (tcs, ct));
            shutdownReg = shutdownToken.Register(static state =>
            {
                var (source, fallback) = ((TaskCompletionSource<T>, T))state!;
                source.TrySetResult(fallback);
            }, (tcs, defaultValue));
        }
        catch (ObjectDisposedException)
        {
            ctReg.Dispose();
            shutdownReg.Dispose();
            return defaultValue;
        }

        using (ctReg)
        using (shutdownReg)
        {
            if (_isClosing || shutdownToken.IsCancellationRequested)
            {
                tcs.TrySetResult(defaultValue);
                return await tcs.Task.ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return await tcs.Task.ConfigureAwait(false);
            }

            try
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        if (_isClosing || IsDisposed || Disposing || shutdownToken.IsCancellationRequested)
                        {
                            tcs.TrySetResult(defaultValue);
                            return;
                        }

                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(ct);
                            return;
                        }

                        tcs.TrySetResult(action());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                tcs.TrySetResult(defaultValue);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private void OnTilePlayStopClicked(CameraTileControl tile)
    {
        if (string.IsNullOrEmpty(tile.CameraId))
        {
            return;
        }

        if (_activeChannels.ContainsKey(tile.CameraId))
        {
            StopCameraCore(tile.CameraId);
        }
        else
        {
            StartCameraCore(tile.CameraId);
        }
    }

    private void OnTileEditClicked(CameraTileControl tile)
    {
        CameraConfig? existing = string.IsNullOrEmpty(tile.CameraId)
            ? null
            : _store.FindCamera(tile.CameraId);

        OpenCameraDialog(existing, tile.SlotNumber);
    }

    private void OnToolbarAddCameraClick(object? sender, EventArgs e)
    {
        AppConfig config = _store.GetConfigSnapshot();
        int? freeSlot = Enumerable.Range(1, CameraStore.MaxSlots)
            .Cast<int?>()
            .FirstOrDefault(slot => config.Cameras.All(c => c.Slot != slot));

        if (!freeSlot.HasValue)
        {
            MessageBox.Show(
                this,
                $"All {CameraStore.MaxSlots} camera slots are already occupied.",
                "Add Camera",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        OpenCameraDialog(existing: null, defaultSlot: freeSlot.Value);
    }

    private void OpenCameraDialog(CameraConfig? existing, int defaultSlot)
    {
        string existingPassword = string.Empty;
        if (existing is not null)
        {
            try
            {
                existingPassword = _store.GetPlaintextPassword(existing);
            }
            catch (Exception)
            {
                MessageBox.Show(
                    this,
                    "The saved password for this camera could not be decrypted. Enter a new password and save to replace it, or leave it blank to clear.",
                    "Password Decryption Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        string? ValidateSlot(int slot)
        {
            CameraConfig? occupant = _store.GetConfigSnapshot().Cameras
                .FirstOrDefault(c => c.Slot == slot && !string.Equals(c.Id, existing?.Id, StringComparison.Ordinal));
            return occupant is null
                ? null
                : $"Slot {slot} is already occupied by '{occupant.Name}'. Choose a free slot.";
        }

        using var dialog = new EditCameraDialog(existing, existingPassword, defaultSlot, ValidateSlot);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            if (existing is null)
            {
                _store.AddCamera(dialog.BuildAddRequest());
            }
            else if (dialog.DeleteRequested)
            {
                _store.RemoveCamera(existing.Id);
            }
            else
            {
                _store.UpdateCamera(existing.Id, dialog.BuildUpdateRequest());
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            MessageBox.Show(
                this,
                FfmpegSnapshotRunner.SanitizeErrorText(ex.Message),
                "Camera Configuration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnTileDoubleClicked(CameraTileControl tile)
    {
        _maximizedSlot = _maximizedSlot == tile.SlotNumber ? null : tile.SlotNumber;
        AppConfig config = _store.GetConfigSnapshot();
        RebuildGridLayout(config.GridLayout);
    }

    private void OnGridLayoutChanged(object? sender, EventArgs e)
    {
        if (_suppressToolbarEvents || _gridComboBox.SelectedItem is not string selected)
        {
            return;
        }

        if (!int.TryParse(selected, out int gridCount) || Array.IndexOf(ValidGridLayouts, gridCount) < 0)
        {
            return;
        }

        _maximizedSlot = null;
        _store.UpdateSettings(s => s.GridLayout = gridCount);
        RebuildGridLayout(gridCount);
    }

    private void OnRenderFormatChanged(object? sender, EventArgs e)
    {
        if (_suppressToolbarEvents || _renderComboBox.SelectedItem is not string label)
        {
            return;
        }

        _renderFormat = ToSdkRenderFormat(label);
        string storedFormat = ToStoredRenderFormat(label);
        _store.UpdateSettings(s => s.RenderFormat = storedFormat);

        string[] activeIds = _activeChannels.Keys.ToArray();
        foreach (string cameraId in activeIds)
        {
            StopCameraCore(cameraId);
            StartCameraCore(cameraId);
        }
    }

    private void OnMcpSettingsClick(object? sender, EventArgs e)
    {
        using var dialog = new McpSettingsDialog(_store, _authStore, _mcpHost);
        dialog.ShowDialog(this);
        UpdateMcpStatusDisplay();
    }

    private void OnStartupCheckChanged(object? sender, EventArgs e)
    {
        if (_suppressToolbarEvents)
        {
            return;
        }

        bool desired = _startupCheckBox.Checked;
        try
        {
            StartupRegistration.SetEnabled(desired);
        }
        catch (Exception ex)
        {
            _suppressToolbarEvents = true;
            try
            {
                _startupCheckBox.Checked = !desired;
            }
            finally
            {
                _suppressToolbarEvents = false;
            }

            MessageBox.Show(
                this,
                $"Unable to update Windows startup registration: {ex.Message}",
                "Start with Windows",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static string ToRenderLabel(string storedFormat) => storedFormat.ToUpperInvariant() switch
    {
        "RGB565" => "RGB565",
        "YV12" => "YV12",
        "YUY2" => "YUY2",
        _ => "GDI"
    };

    private static string ToStoredRenderFormat(string label) => label.ToUpperInvariant() switch
    {
        "RGB565" => "RGB565",
        "YV12" => "YV12",
        "YUY2" => "YUY2",
        _ => "RGB24_GDI"
    };

    private static PlayerSdk.RENDER_FORMAT ToSdkRenderFormat(string label) => label.ToUpperInvariant() switch
    {
        "RGB565" => PlayerSdk.RENDER_FORMAT.DISPLAY_FORMAT_RGB565,
        "YV12" => PlayerSdk.RENDER_FORMAT.DISPLAY_FORMAT_YV12,
        "YUY2" => PlayerSdk.RENDER_FORMAT.DISPLAY_FORMAT_YUY2,
        _ => PlayerSdk.RENDER_FORMAT.DISPLAY_FORMAT_RGB24_GDI
    };
}
