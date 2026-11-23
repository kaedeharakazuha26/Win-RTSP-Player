using WinRtspPlayer.Core.Config;

namespace WinRtspPlayer.Client;

/// <summary>
/// Modal dialog for adding, editing, parsing, or removing a persisted RTSP camera configuration.
/// </summary>
public sealed class EditCameraDialog : Form
{
    private readonly CameraConfig? _existingCamera;

    private readonly TextBox _rtspUrlTextBox;
    private readonly TextBox _nameTextBox;
    private readonly NumericUpDown _slotNumeric;
    private readonly TextBox _ipTextBox;
    private readonly NumericUpDown _portNumeric;
    private readonly TextBox _pathTextBox;
    private readonly TextBox _usernameTextBox;
    private readonly TextBox _passwordTextBox;
    private readonly CheckBox _showPasswordCheckBox;
    private readonly ComboBox _transportComboBox;
    private readonly CheckBox _hardDecodeCheckBox;
    private readonly CheckBox _autoPlayCheckBox;

    public EditCameraDialog(CameraConfig? existingCamera, string existingPassword, int defaultSlot)
    {
        _existingCamera = existingCamera;

        Text = existingCamera is null ? "Add Camera" : $"Edit Camera (Slot {existingCamera.Slot})";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(500, 395);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 12
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 11; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

        // Row 0: Quick RTSP URL parser
        _rtspUrlTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "rtsp://user:password@192.168.1.50:554/stream1"
        };
        var parseButton = new Button
        {
            Text = "Parse",
            Width = 70,
            Dock = DockStyle.Right
        };
        parseButton.Click += OnParseUrlClick;
        var urlPanel = BuildInputWithTrailingControl(_rtspUrlTextBox, parseButton);
        AddLabeledRow(root, 0, "Paste RTSP URL:", urlPanel);

        // Row 1: Name
        _nameTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = existingCamera?.Name ?? $"Camera {Math.Clamp(defaultSlot, 1, CameraStore.MaxSlots)}"
        };
        AddLabeledRow(root, 1, "Name:", _nameTextBox);

        // Row 2: Slot
        _slotNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = CameraStore.MaxSlots,
            Value = Math.Clamp(existingCamera?.Slot ?? defaultSlot, 1, CameraStore.MaxSlots),
            Width = 80
        };
        AddLabeledRow(root, 2, "Tile Slot:", _slotNumeric);

        // Row 3: IP / Host
        _ipTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = existingCamera?.Ip ?? string.Empty
        };
        AddLabeledRow(root, 3, "IP / Host:", _ipTextBox);

        // Row 4: Port
        _portNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = existingCamera is not null && existingCamera.Port is >= 1 and <= 65535
                ? existingCamera.Port
                : 554,
            Width = 90
        };
        AddLabeledRow(root, 4, "Port:", _portNumeric);

        // Row 5: Stream Path
        _pathTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "/cam/realmonitor?channel=1&subtype=0",
            Text = existingCamera?.Path ?? string.Empty
        };
        AddLabeledRow(root, 5, "Stream Path:", _pathTextBox);

        // Row 6: Username
        _usernameTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = existingCamera?.Username ?? string.Empty
        };
        AddLabeledRow(root, 6, "Username:", _usernameTextBox);

        // Row 7: Password + Show/Hide toggle
        _passwordTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            Text = existingPassword ?? string.Empty
        };
        _showPasswordCheckBox = new CheckBox
        {
            Text = "Show",
            AutoSize = true,
            Dock = DockStyle.Right,
            Padding = new Padding(6, 3, 0, 0)
        };
        _showPasswordCheckBox.CheckedChanged += (_, _) =>
        {
            _passwordTextBox.UseSystemPasswordChar = !_showPasswordCheckBox.Checked;
        };
        AddLabeledRow(root, 7, "Password:", BuildInputWithTrailingControl(_passwordTextBox, _showPasswordCheckBox));

        // Row 8: Transport
        _transportComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 90
        };
        _transportComboBox.Items.AddRange(["tcp", "udp"]);
        _transportComboBox.SelectedItem = string.Equals(existingCamera?.Transport, "udp", StringComparison.OrdinalIgnoreCase)
            ? "udp"
            : "tcp";
        AddLabeledRow(root, 8, "Transport:", _transportComboBox);

        // Row 9: Hardware Decode
        _hardDecodeCheckBox = new CheckBox
        {
            Text = "Enable hardware video decoding",
            AutoSize = true,
            Checked = existingCamera?.HardDecode ?? false
        };
        root.Controls.Add(_hardDecodeCheckBox, 1, 9);

        // Row 10: Auto-play on startup
        _autoPlayCheckBox = new CheckBox
        {
            Text = "Auto-play on startup",
            AutoSize = true,
            Checked = existingCamera?.AutoPlay ?? true
        };
        root.Controls.Add(_autoPlayCheckBox, 1, 10);

        // Row 11: Action buttons
        var buttonsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };

        if (existingCamera is not null)
        {
            var deleteButton = new Button
            {
                Text = "Delete",
                Width = 82,
                Height = 28,
                ForeColor = Color.Firebrick,
                Dock = DockStyle.Left
            };
            deleteButton.Click += OnDeleteClick;
            buttonsPanel.Controls.Add(deleteButton);
        }

        var saveButton = new Button
        {
            Text = "Save",
            Width = 86,
            Height = 28,
            Dock = DockStyle.Right
        };
        saveButton.Click += OnSaveClick;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 86,
            Height = 28,
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Right
        };

        var spacer = new Panel { Width = 8, Dock = DockStyle.Right };
        buttonsPanel.Controls.Add(saveButton);
        buttonsPanel.Controls.Add(spacer);
        buttonsPanel.Controls.Add(cancelButton);

        root.Controls.Add(buttonsPanel, 0, 11);
        root.SetColumnSpan(buttonsPanel, 2);

        Controls.Add(root);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public bool DeleteRequested { get; private set; }

    public CameraAddRequest BuildAddRequest() => new(
        Name: _nameTextBox.Text.Trim(),
        Ip: _ipTextBox.Text.Trim(),
        Port: (int)_portNumeric.Value,
        Path: _pathTextBox.Text.Trim(),
        Username: _usernameTextBox.Text,
        Password: string.IsNullOrEmpty(_passwordTextBox.Text) ? null : _passwordTextBox.Text,
        Transport: _transportComboBox.SelectedItem as string ?? "tcp",
        Slot: (int)_slotNumeric.Value,
        HardDecode: _hardDecodeCheckBox.Checked,
        AutoPlay: _autoPlayCheckBox.Checked);

    public CameraUpdateRequest BuildUpdateRequest()
    {
        bool clearPassword = string.IsNullOrEmpty(_passwordTextBox.Text);
        return new CameraUpdateRequest(
            Name: _nameTextBox.Text.Trim(),
            Ip: _ipTextBox.Text.Trim(),
            Port: (int)_portNumeric.Value,
            Path: _pathTextBox.Text.Trim(),
            Username: _usernameTextBox.Text,
            Password: clearPassword ? null : _passwordTextBox.Text,
            ClearPassword: clearPassword,
            Transport: _transportComboBox.SelectedItem as string ?? "tcp",
            Slot: (int)_slotNumeric.Value,
            HardDecode: _hardDecodeCheckBox.Checked,
            AutoPlay: _autoPlayCheckBox.Checked);
    }

    private void OnParseUrlClick(object? sender, EventArgs e)
    {
        if (!RtspUrlCodec.TryParseUrl(_rtspUrlTextBox.Text, out ParsedRtspUrl? parsed, out string? error) || parsed is null)
        {
            MessageBox.Show(this, error ?? "Invalid RTSP URL.", "Parse RTSP URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _ipTextBox.Text = parsed.Ip;
        _portNumeric.Value = Math.Clamp(parsed.Port, 1, 65535);
        _pathTextBox.Text = parsed.Path;
        _usernameTextBox.Text = parsed.Username;
        _passwordTextBox.Text = parsed.Password;
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        string name = _nameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Camera name must not be empty.", "Invalid Camera Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _nameTextBox.Focus();
            return;
        }

        string ip = _ipTextBox.Text.Trim();
        int port = (int)_portNumeric.Value;
        string path = _pathTextBox.Text.Trim();
        string transport = _transportComboBox.SelectedItem as string ?? "tcp";

        string? validationError = RtspUrlCodec.ValidateCameraInput(ip, port, path, transport);
        if (validationError is not null)
        {
            MessageBox.Show(this, validationError, "Invalid Camera Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ipTextBox.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        if (_existingCamera is null)
        {
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            $"Delete camera '{_existingCamera.Name}' from Slot {_existingCamera.Slot}?",
            "Delete Camera",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm == DialogResult.Yes)
        {
            DeleteRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private static void AddLabeledRow(TableLayoutPanel table, int row, string labelText, Control inputControl)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(inputControl, 1, row);
    }

    private static Panel BuildInputWithTrailingControl(Control mainInput, Control trailing)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var spacer = new Panel { Width = 6, Dock = DockStyle.Right };
        panel.Controls.Add(mainInput);
        panel.Controls.Add(spacer);
        panel.Controls.Add(trailing);
        return panel;
    }
}
