using System.Runtime.InteropServices;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using WinRtspPlayer.Core.Mcp;
using WinRtspPlayer.Core.Media;

namespace WinRtspPlayer.Client;

/// <summary>
/// Displays and edits embedded MCP server settings, OAuth 2.1 credentials, and Cloudflare Tunnel setup snippets.
/// </summary>
public sealed class McpSettingsDialog : Form
{
    private readonly CameraStore _store;
    private readonly OAuthAuthStore _authStore;
    private readonly McpServerHost _mcpHost;
    private readonly int _initialPort;

    private readonly Label _statusValueLabel;
    private readonly NumericUpDown _portNumeric;
    private readonly TextBox _publicBaseUrlTextBox;
    private readonly TextBox _mcpUrlTextBox;
    private readonly TextBox _clientIdTextBox;
    private readonly TextBox _clientSecretTextBox;
    private readonly Button _toggleSecretButton;
    private readonly TextBox _consentPasscodeTextBox;
    private readonly Button _togglePasscodeButton;
    private readonly TextBox _tunnelSnippetTextBox;
    private readonly Button _regenerateButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;

    private bool _isSaving;

    public McpSettingsDialog(CameraStore store, OAuthAuthStore authStore, McpServerHost mcpHost)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
        _mcpHost = mcpHost ?? throw new ArgumentNullException(nameof(mcpHost));

        AppConfig config = _store.GetConfigSnapshot();
        _initialPort = config.McpPort;

        Text = "MCP & Gemini Connection Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 415);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 9
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Status
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Port
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Public Base URL
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // MCP URL
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Client ID
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Client Secret
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Consent Passcode
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 115f)); // Tunnel instructions
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f)); // Buttons

        // Row 0: Live MCP server status
        _statusValueLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        AddLabeledRow(root, 0, "Server Status:", _statusValueLabel);

        // Row 1: Port
        _portNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(config.McpPort, 1, 65535),
            Width = 90
        };
        _portNumeric.ValueChanged += (_, _) => UpdateDerivedUrls();
        AddLabeledRow(root, 1, "Local MCP Port:", _portNumeric);

        // Row 2: Public HTTPS Base URL
        _publicBaseUrlTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "https://cameras.yourdomain.com (optional until tunnel is active)",
            Text = config.McpPublicBaseUrl ?? string.Empty
        };
        _publicBaseUrlTextBox.TextChanged += (_, _) => UpdateDerivedUrls();
        AddLabeledRow(root, 2, "Public HTTPS Base URL:", _publicBaseUrlTextBox);

        // Row 3: MCP Server URL (read-only + Copy)
        _mcpUrlTextBox = CreateReadOnlyTextBox();
        AddLabeledRow(root, 3, "MCP Server URL:", BuildCopyableRow(_mcpUrlTextBox, copyButton =>
        {
            copyButton.Click += (_, _) => CopyToClipboard(_mcpUrlTextBox.Text);
        }));

        // Row 4: OAuth Client ID
        _clientIdTextBox = CreateReadOnlyTextBox();
        AddLabeledRow(root, 4, "OAuth Client ID:", BuildCopyableRow(_clientIdTextBox, copyButton =>
        {
            copyButton.Click += (_, _) => CopyToClipboard(_clientIdTextBox.Text);
        }));

        // Row 5: OAuth Client Secret (masked + Show/Hide + Copy)
        _clientSecretTextBox = CreateReadOnlyTextBox();
        _clientSecretTextBox.UseSystemPasswordChar = true;
        _toggleSecretButton = CreateRowButton("Show", 56);
        _toggleSecretButton.Click += (_, _) => ToggleMask(_clientSecretTextBox, _toggleSecretButton);
        AddLabeledRow(root, 5, "OAuth Client Secret:", BuildSecretCopyRow(_clientSecretTextBox, _toggleSecretButton, () => CopyToClipboard(_clientSecretTextBox.Text)));

        // Row 6: Consent Passcode (masked + Show/Hide + Copy)
        _consentPasscodeTextBox = CreateReadOnlyTextBox();
        _consentPasscodeTextBox.UseSystemPasswordChar = true;
        _togglePasscodeButton = CreateRowButton("Show", 56);
        _togglePasscodeButton.Click += (_, _) => ToggleMask(_consentPasscodeTextBox, _togglePasscodeButton);
        AddLabeledRow(root, 6, "Consent Passcode:", BuildSecretCopyRow(_consentPasscodeTextBox, _togglePasscodeButton, () => CopyToClipboard(_consentPasscodeTextBox.Text)));

        // Row 7: Cloudflare Tunnel command snippet
        _tunnelSnippetTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window
        };
        var copyTunnelButton = CreateRowButton("Copy Cmd", 74);
        copyTunnelButton.Dock = DockStyle.Top;
        copyTunnelButton.Height = 26;
        copyTunnelButton.Click += (_, _) => CopyToClipboard($"cloudflared tunnel --url http://127.0.0.1:{(int)_portNumeric.Value}");
        var tunnelPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) };
        var tunnelSpacer = new Panel { Width = 6, Dock = DockStyle.Right };
        tunnelPanel.Controls.Add(_tunnelSnippetTextBox);
        tunnelPanel.Controls.Add(tunnelSpacer);
        tunnelPanel.Controls.Add(copyTunnelButton);
        AddLabeledRow(root, 7, "Cloudflare Tunnel:", tunnelPanel);

        // Row 8: Action buttons
        var buttonsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        _regenerateButton = new Button
        {
            Text = "Regenerate Secrets",
            Width = 140,
            Height = 28,
            Dock = DockStyle.Left
        };
        _regenerateButton.Click += OnRegenerateSecretsClick;

        _saveButton = new Button
        {
            Text = "Save",
            Width = 86,
            Height = 28,
            Dock = DockStyle.Right
        };
        _saveButton.Click += OnSaveClick;

        _cancelButton = new Button
        {
            Text = "Cancel",
            Width = 86,
            Height = 28,
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Right
        };

        var btnSpacer = new Panel { Width = 8, Dock = DockStyle.Right };
        buttonsPanel.Controls.Add(_regenerateButton);
        buttonsPanel.Controls.Add(_saveButton);
        buttonsPanel.Controls.Add(btnSpacer);
        buttonsPanel.Controls.Add(_cancelButton);

        root.Controls.Add(buttonsPanel, 0, 8);
        root.SetColumnSpan(buttonsPanel, 2);

        Controls.Add(root);
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        RefreshStatusLabel();
        LoadCredentials(_authStore.GetCredentials());
        UpdateDerivedUrls();
    }

    private void RefreshStatusLabel()
    {
        if (_mcpHost.IsRunning)
        {
            _statusValueLabel.Text = $"Listening on http://127.0.0.1:{_mcpHost.BoundPort}";
            _statusValueLabel.ForeColor = Color.DarkGreen;
        }
        else if (!string.IsNullOrWhiteSpace(_mcpHost.LastError))
        {
            _statusValueLabel.Text = $"Error: {_mcpHost.LastError}";
            _statusValueLabel.ForeColor = Color.Firebrick;
        }
        else
        {
            _statusValueLabel.Text = "Stopped";
            _statusValueLabel.ForeColor = SystemColors.GrayText;
        }
    }

    private void LoadCredentials(OAuthCredentialsView creds)
    {
        _clientIdTextBox.Text = creds.ClientId;
        _clientSecretTextBox.Text = creds.ClientSecret;
        _consentPasscodeTextBox.Text = creds.ConsentPasscode;
    }

    private void UpdateDerivedUrls()
    {
        int port = (int)_portNumeric.Value;
        string publicBase = _publicBaseUrlTextBox.Text.Trim().TrimEnd('/');
        string effectiveBase = string.IsNullOrEmpty(publicBase)
            ? $"http://127.0.0.1:{port}"
            : publicBase;

        _mcpUrlTextBox.Text = $"{effectiveBase}/mcp";
        _tunnelSnippetTextBox.Text =
            $"Quick tunnel (temporary URL):{Environment.NewLine}" +
            $"cloudflared tunnel --url http://127.0.0.1:{port}{Environment.NewLine}{Environment.NewLine}" +
            $"Named tunnel (permanent URL, recommended):{Environment.NewLine}" +
            $"cloudflared tunnel run winrtsp-mcp";
    }

    private void OnRegenerateSecretsClick(object? sender, EventArgs e)
    {
        DialogResult confirm = MessageBox.Show(
            this,
            "Regenerating OAuth secrets invalidates existing Gemini tokens and requires re-connecting Gemini. Continue?",
            "Regenerate OAuth Secrets",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        OAuthCredentialsView updated = _authStore.RegenerateSecrets();
        LoadCredentials(updated);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isSaving)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        string rawBaseUrl = _publicBaseUrlTextBox.Text.Trim().TrimEnd('/');
        string? normalizedBaseUrl = string.IsNullOrEmpty(rawBaseUrl) ? null : rawBaseUrl;
        if (normalizedBaseUrl is not null)
        {
            if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out Uri? parsed) ||
                (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    this,
                    "Public HTTPS Base URL must be empty or a valid http/https URL (e.g. https://cameras.yourdomain.com).",
                    "Invalid Public Base URL",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _publicBaseUrlTextBox.Focus();
                return;
            }
        }

        int newPort = (int)_portNumeric.Value;
        bool needsRestart = newPort != _initialPort || !_mcpHost.IsRunning;
        if (needsRestart)
        {
            bool wasRunning = _mcpHost.IsRunning;
            _isSaving = true;
            SetUiBusy(true);
            try
            {
                await _mcpHost.RestartAsync(newPort);
            }
            catch (Exception ex)
            {
                if (wasRunning && newPort != _initialPort)
                {
                    try
                    {
                        await _mcpHost.RestartAsync(_initialPort);
                    }
                    catch
                    {
                        // Best-effort restore of previous listening port.
                    }
                }

                if (IsDisposed)
                {
                    return;
                }

                _isSaving = false;
                RefreshStatusLabel();
                SetUiBusy(false);
                MessageBox.Show(
                    this,
                    $"Failed to start MCP server on port {newPort}: {FfmpegSnapshotRunner.SanitizeErrorText(ex.Message)}",
                    "MCP Server Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            finally
            {
                _isSaving = false;
                if (!IsDisposed)
                {
                    SetUiBusy(false);
                }
            }

            if (IsDisposed)
            {
                return;
            }
        }

        _store.UpdateSettings(s =>
        {
            s.McpPort = newPort;
            s.McpPublicBaseUrl = normalizedBaseUrl;
        });

        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetUiBusy(bool busy)
    {
        _saveButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _regenerateButton.Enabled = !busy;
        _portNumeric.Enabled = !busy;
        _publicBaseUrlTextBox.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private static void ToggleMask(TextBox textBox, Button button)
    {
        bool show = textBox.UseSystemPasswordChar;
        textBox.UseSystemPasswordChar = !show;
        button.Text = show ? "Hide" : "Show";
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, $"Could not copy to clipboard: {ex.Message}", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static TextBox CreateReadOnlyTextBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window
    };

    private static Button CreateRowButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Dock = DockStyle.Right
    };

    private static void AddLabeledRow(TableLayoutPanel table, int row, string labelText, Control content)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(content, 1, row);
    }

    private static Panel BuildCopyableRow(TextBox textBox, Action<Button> configureCopyButton)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var copyButton = CreateRowButton("Copy", 56);
        configureCopyButton(copyButton);
        var spacer = new Panel { Width = 6, Dock = DockStyle.Right };
        panel.Controls.Add(textBox);
        panel.Controls.Add(spacer);
        panel.Controls.Add(copyButton);
        return panel;
    }

    private static Panel BuildSecretCopyRow(TextBox textBox, Button toggleButton, Action onCopy)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var copyButton = CreateRowButton("Copy", 56);
        copyButton.Click += (_, _) => onCopy();
        var spacer1 = new Panel { Width = 6, Dock = DockStyle.Right };
        var spacer2 = new Panel { Width = 6, Dock = DockStyle.Right };
        panel.Controls.Add(textBox);
        panel.Controls.Add(spacer1);
        panel.Controls.Add(toggleButton);
        panel.Controls.Add(spacer2);
        panel.Controls.Add(copyButton);
        return panel;
    }
}
