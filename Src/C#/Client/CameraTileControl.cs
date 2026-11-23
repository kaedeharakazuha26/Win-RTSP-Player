using WinRtspPlayer.Core.Config;

namespace WinRtspPlayer.Client;

public enum TilePlaybackState
{
    Stopped,
    Connecting,
    Playing,
    Error
}

/// <summary>
/// Displays a single RTSP camera slot with a dark Win32 video surface, sanitized endpoint label,
/// status indicator, and play/stop/edit controls.
/// </summary>
public sealed class CameraTileControl : UserControl
{
    private static readonly Color BorderColor = Color.FromArgb(51, 65, 85);
    private static readonly Color BarBackColor = Color.FromArgb(30, 35, 43);
    private static readonly Color SurfaceBackColor = Color.FromArgb(15, 18, 24);
    private static readonly Color PrimaryTextColor = Color.FromArgb(241, 245, 249);
    private static readonly Color MutedTextColor = Color.FromArgb(148, 163, 184);
    private static readonly Color ButtonBackColor = Color.FromArgb(51, 65, 85);

    private readonly Panel _videoPanel;
    private readonly Label _titleLabel;
    private readonly Label _hostLabel;
    private readonly Label _statusLabel;
    private readonly Button _playStopButton;
    private readonly Button _editButton;
    private readonly ToolTip _toolTip;

    private CameraConfig? _camera;
    private TilePlaybackState _playbackState = TilePlaybackState.Stopped;

    public CameraTileControl(int slotNumber)
    {
        if (slotNumber is < 1 or > CameraStore.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber), $"Slot must be between 1 and {CameraStore.MaxSlots}.");
        }

        SlotNumber = slotNumber;

        BackColor = BorderColor;
        Padding = new Padding(1);
        Margin = new Padding(3);
        Dock = DockStyle.Fill;

        _toolTip = new ToolTip();

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceBackColor,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BarBackColor,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(6, 0, 6, 0)
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _titleLabel.DoubleClick += HandleVideoSurfaceDoubleClick;

        _hostLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            Margin = new Padding(8, 0, 0, 0)
        };
        _hostLabel.DoubleClick += HandleVideoSurfaceDoubleClick;

        headerLayout.Controls.Add(_titleLabel, 0, 0);
        headerLayout.Controls.Add(_hostLabel, 1, 0);

        _videoPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Margin = Padding.Empty
        };
        _videoPanel.DoubleClick += HandleVideoSurfaceDoubleClick;

        var footerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BarBackColor,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(6, 3, 6, 3)
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            Margin = Padding.Empty
        };

        _playStopButton = CreateBarButton("Play", 54);
        _playStopButton.Margin = new Padding(4, 0, 4, 0);
        _playStopButton.Click += (_, _) => PlayStopClicked?.Invoke(this, EventArgs.Empty);

        _editButton = CreateBarButton("Add", 50);
        _editButton.Margin = Padding.Empty;
        _editButton.Click += (_, _) => EditClicked?.Invoke(this, EventArgs.Empty);

        footerLayout.Controls.Add(_statusLabel, 0, 0);
        footerLayout.Controls.Add(_playStopButton, 1, 0);
        footerLayout.Controls.Add(_editButton, 2, 0);

        rootLayout.Controls.Add(headerLayout, 0, 0);
        rootLayout.Controls.Add(_videoPanel, 0, 1);
        rootLayout.Controls.Add(footerLayout, 0, 2);

        Controls.Add(rootLayout);
        ApplyDisplay();
    }

    public int SlotNumber { get; }

    public string? CameraId => _camera?.Id;

    public TilePlaybackState PlaybackState => _playbackState;

    public IntPtr VideoHandle => _videoPanel.Handle;

    public event EventHandler? PlayStopClicked;

    public event EventHandler? EditClicked;

    public event EventHandler? VideoDoubleClicked;

    public void SetVideoDoubleClick(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        VideoDoubleClicked += handler;
    }

    public void SetCamera(CameraConfig? camera)
    {
        bool sameCamera = camera is not null &&
            _camera is not null &&
            string.Equals(_camera.Id, camera.Id, StringComparison.Ordinal);

        _camera = camera?.Clone();
        if (!sameCamera)
        {
            _playbackState = TilePlaybackState.Stopped;
            _toolTip.SetToolTip(_statusLabel, null);
        }

        ApplyDisplay();
    }

    public void SetStatus(TilePlaybackState state, string? detail = null)
    {
        _playbackState = state;
        _toolTip.SetToolTip(_statusLabel, string.IsNullOrWhiteSpace(detail) ? null : detail);

        if (state is TilePlaybackState.Stopped or TilePlaybackState.Error)
        {
            if (_videoPanel.IsHandleCreated)
            {
                _videoPanel.Invalidate();
                _videoPanel.Update();
            }
        }

        ApplyDisplay();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _titleLabel.Font.Dispose();
            _hostLabel.Font.Dispose();
            _statusLabel.Font.Dispose();
            _playStopButton.Font.Dispose();
            _editButton.Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyDisplay()
    {
        if (_camera is null)
        {
            _titleLabel.Text = $"Slot {SlotNumber}  ·  Empty Slot";
            _hostLabel.Text = string.Empty;
            _statusLabel.Text = "Empty";
            _statusLabel.ForeColor = MutedTextColor;
            _playStopButton.Text = "Play";
            _playStopButton.Enabled = false;
            _playStopButton.Visible = false;
            _editButton.Text = "Add";
            return;
        }

        _titleLabel.Text = $"Slot {SlotNumber}  ·  {_camera.Name}";
        _hostLabel.Text = $"{_camera.Ip}:{_camera.Port}";
        _editButton.Text = "Edit";
        _playStopButton.Visible = true;
        _playStopButton.Enabled = true;

        switch (_playbackState)
        {
            case TilePlaybackState.Playing:
                _statusLabel.Text = "● Playing";
                _statusLabel.ForeColor = Color.FromArgb(74, 222, 128);
                _playStopButton.Text = "Stop";
                break;

            case TilePlaybackState.Connecting:
                _statusLabel.Text = "● Connecting";
                _statusLabel.ForeColor = Color.FromArgb(251, 191, 36);
                _playStopButton.Text = "Stop";
                break;

            case TilePlaybackState.Error:
                _statusLabel.Text = "● Error";
                _statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                _playStopButton.Text = "Play";
                break;

            default:
                _statusLabel.Text = "● Stopped";
                _statusLabel.ForeColor = MutedTextColor;
                _playStopButton.Text = "Play";
                break;
        }
    }

    private void HandleVideoSurfaceDoubleClick(object? sender, EventArgs e)
    {
        VideoDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    private static Button CreateBarButton(string text, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 22,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBackColor,
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
