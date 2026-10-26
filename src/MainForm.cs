using System.Diagnostics;

namespace CardapioPrintAgent;

internal sealed class MainForm : Form
{
    private readonly AgentLogger _logger = new();
    private AgentConfig _config = AgentConfig.Load();

    private readonly TextBox _serverText = new();
    private readonly TextBox _tokenText = new();
    private readonly ComboBox _printerCombo = new();
    private readonly ComboBox _paperCombo = new();
    private readonly NumericUpDown _paperWidthInput = new();
    private readonly NumericUpDown _feedAfterPrintInput = new();
    private readonly TextBox _clientIdText = new();
    private readonly NumericUpDown _pollMsInput = new();
    private readonly CheckBox _startupCheck = new();
    private readonly Label _statusLabel = new();
    private readonly Label _statsLabel = new();
    private readonly TextBox _logBox = new();

    private readonly Button _installButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _verifyButton = new();
    private readonly Button _logsButton = new();
    private readonly Button _clearLogsButton = new();
    private readonly Button _refreshButton = new();

    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private sealed class PaperOption
    {
        public PaperOption(string label, string printMode, int paperWidthMm)
        {
            Label = label;
            PrintMode = printMode;
            PaperWidthMm = paperWidthMm;
        }

        public string Label { get; }
        public string PrintMode { get; }
        public int PaperWidthMm { get; }

        public override string ToString() => Label;
    }

    public MainForm()
    {
        Text = $"Agente de Impressao Automatica {AppVersion.Label}";
        Width = 880;
        Height = 690;
        MinimumSize = new Size(840, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _config.Normalize();
        _startupCheck.Checked = _config.StartWithWindows || AgentProcessManager.IsStartupEnabled();

        LoadConfigIntoUi();
        LoadPrinters();
        _printerCombo.SelectedIndexChanged += (_, _) => ApplySuggestedPaperForSelectedPrinter();
        _paperCombo.SelectedIndexChanged += (_, _) => ApplyPaperOptionToWidthInput();

        _refreshTimer.Interval = 3000;
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync(silent: true);
        _refreshTimer.Start();

        Shown += async (_, _) => await RefreshStatusAsync(silent: true);
        FormClosing += (_, _) => _refreshTimer.Stop();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = $"Agente de Impressao Automatica (Nativo .exe) {AppVersion.Label}",
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 8),
        };
        root.Controls.Add(title, 0, 0);

        var configPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        configPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddField(configPanel, "Servidor", _serverText, 0);
        AddField(configPanel, "Token", _tokenText, 1);
        _tokenText.UseSystemPasswordChar = true;

        AddField(configPanel, "Impressora", _printerCombo, 2);
        _printerCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        AddField(configPanel, "Formato", _paperCombo, 3);
        _paperCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        AddField(configPanel, "Largura papel (mm)", _paperWidthInput, 4);
        _paperWidthInput.Minimum = 40;
        _paperWidthInput.Maximum = 210;
        _paperWidthInput.Increment = 1;

        AddField(configPanel, "Avanco final (mm)", _feedAfterPrintInput, 5);
        _feedAfterPrintInput.Minimum = 0;
        _feedAfterPrintInput.Maximum = 80;
        _feedAfterPrintInput.Increment = 1;

        AddField(configPanel, "Client ID", _clientIdText, 6);
        AddField(configPanel, "Poll (ms)", _pollMsInput, 7);
        _pollMsInput.Minimum = 500;
        _pollMsInput.Maximum = 120000;
        _pollMsInput.Increment = 100;

        _startupCheck.Text = "Iniciar em segundo plano com o Windows";
        _startupCheck.AutoSize = true;
        _startupCheck.Margin = new Padding(3, 6, 3, 2);
        configPanel.Controls.Add(_startupCheck, 1, 8);

        root.Controls.Add(configPanel, 0, 1);

        var statusWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 8),
        };

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _statusLabel.Text = "Status: verificando";

        _statsLabel.AutoSize = true;
        _statsLabel.Margin = new Padding(18, 4, 0, 0);
        _statsLabel.Text = "Fila: --";

        statusWrap.Controls.Add(_statusLabel);
        statusWrap.Controls.Add(_statsLabel);

        root.Controls.Add(statusWrap, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };

        ConfigureActionButton(_installButton, "Instalar app", async (_, _) => await InstallAsync());
        ConfigureActionButton(_startButton, "Start", async (_, _) => await StartAgentAsync());
        ConfigureActionButton(_stopButton, "Stop", (_, _) => StopAgent());
        ConfigureActionButton(_verifyButton, "Verificar", async (_, _) => await VerifyAsync());
        ConfigureActionButton(_logsButton, "Abrir logs", (_, _) => OpenLogs());
        ConfigureActionButton(_clearLogsButton, "Limpar avisos", (_, _) => ClearLogs());
        ConfigureActionButton(_refreshButton, "Atualizar", async (_, _) => await RefreshStatusAsync(silent: false));

        actions.Controls.AddRange([
            _installButton,
            _startButton,
            _stopButton,
            _verifyButton,
            _logsButton,
            _clearLogsButton,
            _refreshButton,
        ]);

        root.Controls.Add(actions, 0, 3);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 9f);
        _logBox.Dock = DockStyle.Fill;

        root.Controls.Add(_logBox, 0, 4);
    }

    private static void AddField(TableLayoutPanel panel, string label, Control control, int row)
    {
        while (panel.RowStyles.Count <= row)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 4),
        };

        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 4, 3, 4);

        panel.Controls.Add(labelControl, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void ConfigureActionButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Padding = new Padding(12, 6, 12, 6);
        button.Click += onClick;
    }

    private void LoadConfigIntoUi()
    {
        _serverText.Text = _config.ServerUrl;
        _tokenText.Text = _config.Token;
        _clientIdText.Text = _config.ClientId;
        _pollMsInput.Value = Math.Clamp(_config.PollMs, _pollMsInput.Minimum, _pollMsInput.Maximum);
        LoadPaperOptions();
        _paperWidthInput.Value = Math.Clamp(_config.PaperWidthMm, _paperWidthInput.Minimum, _paperWidthInput.Maximum);
        _feedAfterPrintInput.Value = Math.Clamp(_config.FeedAfterPrintMm, _feedAfterPrintInput.Minimum, _feedAfterPrintInput.Maximum);
        UpdatePaperWidthInputState();
    }

    private void LoadPaperOptions()
    {
        _paperCombo.Items.Clear();
        _paperCombo.Items.Add(new PaperOption("Termica 58mm (cupom pequeno)", "thermal", 58));
        _paperCombo.Items.Add(new PaperOption("Termica 80mm (cupom fiscal / MP-4200 TH)", "thermal", 80));
        _paperCombo.Items.Add(new PaperOption("A4 / impressora comum", "a4", 210));

        var normalizedMode = AgentConfig.NormalizePrintMode(_config.PrintMode, _config.PaperWidthMm);
        var normalizedWidth = AgentConfig.NormalizePaperWidth(normalizedMode, _config.PaperWidthMm);
        var selectedIndex = normalizedMode == "a4"
            ? 2
            : normalizedWidth >= 75
                ? 1
                : 0;
        _paperCombo.SelectedIndex = selectedIndex;
    }

    private void SelectPaperOption(string printMode, int paperWidthMm)
    {
        var normalizedMode = AgentConfig.NormalizePrintMode(printMode, paperWidthMm);
        var normalizedWidth = AgentConfig.NormalizePaperWidth(normalizedMode, paperWidthMm);
        _paperCombo.SelectedIndex = normalizedMode == "a4"
            ? 2
            : normalizedWidth >= 75
                ? 1
                : 0;
        _paperWidthInput.Value = Math.Clamp(normalizedWidth, _paperWidthInput.Minimum, _paperWidthInput.Maximum);
        UpdatePaperWidthInputState();
    }

    private void ApplyPaperOptionToWidthInput()
    {
        if (_paperCombo.SelectedItem is not PaperOption option) return;
        var width = AgentConfig.NormalizePaperWidth(option.PrintMode, option.PaperWidthMm);
        _paperWidthInput.Value = Math.Clamp(width, _paperWidthInput.Minimum, _paperWidthInput.Maximum);
        UpdatePaperWidthInputState();
    }

    private void UpdatePaperWidthInputState()
    {
        var selectedPaper = _paperCombo.SelectedItem as PaperOption;
        _paperWidthInput.Enabled = selectedPaper?.PrintMode != "a4";
    }

    private void ApplySuggestedPaperForSelectedPrinter()
    {
        if (_printerCombo.SelectedIndex < 0) return;
        var printerName = _printerCombo.SelectedIndex <= 0
            ? string.Empty
            : _printerCombo.SelectedItem?.ToString() ?? string.Empty;
        var detectedWidthMm = NativePrinter.TryDetectThermalPaperWidthMm(printerName);
        if (detectedWidthMm.HasValue)
        {
            SelectPaperOption("thermal", detectedWidthMm.Value);
            return;
        }

        if (_printerCombo.SelectedIndex <= 0) return;
        if (IsLikely80MmReceiptPrinter(printerName))
        {
            SelectPaperOption("thermal", 80);
        }
    }

    private static bool IsLikely80MmReceiptPrinter(string printerName)
    {
        var normalized = printerName
            .Trim()
            .ToLowerInvariant()
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty);

        return normalized.Contains("mp4200")
            || normalized.Contains("mp4000")
            || normalized.Contains("bematech")
            || normalized.Contains("bema")
            || normalized.Contains("tm-t20")
            || normalized.Contains("tmt20")
            || normalized.Contains("tm-t88")
            || normalized.Contains("tmt88");
    }

    private void LoadPrinters()
    {
        var current = (_config.PrinterName ?? string.Empty).Trim();
        _printerCombo.Items.Clear();
        _printerCombo.Items.Add("Padrao do Windows");

        foreach (var printer in NativePrinter.ListPrinters())
        {
            _printerCombo.Items.Add(printer);
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            var index = _printerCombo.Items.IndexOf(current);
            _printerCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            _printerCombo.SelectedIndex = 0;
        }
    }

    private AgentConfig ReadConfigFromUi()
    {
        var selectedPaper = _paperCombo.SelectedItem as PaperOption
            ?? new PaperOption("Termica 58mm (cupom pequeno)", "thermal", 58);
        var paperWidthMm = selectedPaper.PrintMode == "a4"
            ? 210
            : (int)_paperWidthInput.Value;

        var config = new AgentConfig
        {
            ServerUrl = _serverText.Text,
            Token = _tokenText.Text,
            PrinterName = _printerCombo.SelectedIndex <= 0 ? string.Empty : _printerCombo.SelectedItem?.ToString() ?? string.Empty,
            PrintMode = selectedPaper.PrintMode,
            PaperWidthMm = paperWidthMm,
            FeedAfterPrintMm = (int)_feedAfterPrintInput.Value,
            ClientId = _clientIdText.Text,
            PollMs = (int)_pollMsInput.Value,
            ErrorPollMs = _config.ErrorPollMs,
            RequestTimeoutMs = _config.RequestTimeoutMs,
            PrintTimeoutMs = _config.PrintTimeoutMs,
            StartWithWindows = _startupCheck.Checked,
        };
        config.Normalize();
        return config;
    }

    private async Task SaveConfigFromUiAsync()
    {
        _config = ReadConfigFromUi();
        await _config.SaveAsync();
    }

    private async Task InstallAsync()
    {
        try
        {
            await SaveConfigFromUiAsync();
            AgentProcessManager.StopAgentProcess();
            var installedPath = AgentProcessManager.EnsureInstalledBundle();
            AgentProcessManager.ConfigureStartup(_startupCheck.Checked, installedPath);
            ShowInfo($"App instalado em:\n{installedPath}");
            await RefreshStatusAsync(silent: true);
        }
        catch (Exception error)
        {
            ShowError($"Falha ao instalar: {error.Message}");
        }
    }

    private async Task StartAgentAsync()
    {
        try
        {
            await SaveConfigFromUiAsync();
            if (string.IsNullOrWhiteSpace(_config.Token))
            {
                ShowError("Token do agente vazio.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            {
                ShowError("URL do servidor vazia.");
                return;
            }

            AgentProcessManager.StopAgentProcess();
            var installedPath = AgentProcessManager.EnsureInstalledBundle();
            AgentProcessManager.ConfigureStartup(_startupCheck.Checked, installedPath);

            await AgentProcessManager.StartAgentProcessAsync(installedPath);
            await Task.Delay(600);

            await RefreshStatusAsync(silent: true);
            ShowInfo("Agente iniciado em segundo plano.");
        }
        catch (Exception error)
        {
            ShowError($"Falha ao iniciar: {error.Message}");
        }
    }

    private void StopAgent()
    {
        try
        {
            AgentProcessManager.StopAgentProcess();
            _statusLabel.Text = "Status: parado";
            _statusLabel.ForeColor = Color.FromArgb(153, 27, 27);
            _startButton.Enabled = true;
            _stopButton.Enabled = false;
            ShowInfo("Agente parado.");
        }
        catch (Exception error)
        {
            ShowError($"Falha ao parar: {error.Message}");
        }
    }

    private async Task VerifyAsync()
    {
        try
        {
            await SaveConfigFromUiAsync();

            var checks = new List<string>
            {
                $"Windows: {(OperatingSystem.IsWindows() ? "OK" : "Nao identificado")}",
                $"Arquivo app: {(File.Exists(AgentProcessManager.EnsureInstalledBundle()) ? "OK" : "ausente")}",
                $"Impressoras: {NativePrinter.ListPrinters().Count} encontrada(s)",
                $"Formato: {_config.PaperProfileLabel} ({_config.PrintMode}, {_config.PaperWidthMm}mm, {_config.ReceiptColumns} colunas)",
                $"Avanco final: {_config.FeedAfterPrintMm}mm",
            };

            var api = new PrintAgentApi(_config, _logger);
            try
            {
                var stats = await api.GetStatsAsync(CancellationToken.None);
                checks.Add("Servidor: conectado");
                checks.Add($"Fila: pendentes {stats.Pending}, imprimindo {stats.Processing}, concluidas {stats.Done}, falhas {stats.Failed}");
            }
            catch (Exception error)
            {
                checks.Add($"Servidor: falhou - {error.Message}");
            }

            ShowInfo(string.Join(Environment.NewLine, checks));
            await RefreshStatusAsync(silent: true);
        }
        catch (Exception error)
        {
            ShowError($"Falha na verificacao: {error.Message}");
        }
    }

    private void OpenLogs()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.LogPath))
        {
            File.WriteAllText(AppPaths.LogPath, "Sem logs ainda." + Environment.NewLine);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{AppPaths.LogPath}\"",
            UseShellExecute = false,
        });
    }

    private void ClearLogs()
    {
        try
        {
            _logger.Clear();
            _logBox.Text = "Logs limpos.";
        }
        catch (Exception error)
        {
            ShowError($"Falha ao limpar avisos: {error.Message}");
        }
    }

    private async Task RefreshStatusAsync(bool silent)
    {
        try
        {
            var running = AgentProcessManager.IsAgentRunning();
            _statusLabel.Text = running ? "Status: rodando em segundo plano" : "Status: parado";
            _statusLabel.ForeColor = running ? Color.FromArgb(22, 101, 52) : Color.FromArgb(153, 27, 27);
            _startButton.Enabled = !running;
            _stopButton.Enabled = running;

            _logBox.Text = _logger.Tail();
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();

            var uiConfig = ReadConfigFromUi();
            if (string.IsNullOrWhiteSpace(uiConfig.Token) || string.IsNullOrWhiteSpace(uiConfig.ServerUrl))
            {
                _statsLabel.Text = "Fila: configure servidor e token";
                return;
            }

            var api = new PrintAgentApi(uiConfig, _logger);
            var stats = await api.GetStatsAsync(CancellationToken.None);
            _statsLabel.Text = $"Fila: pendentes {stats.Pending}, imprimindo {stats.Processing}, concluidas {stats.Done}, falhas {stats.Failed}";
        }
        catch (Exception error)
        {
            _statsLabel.Text = "Fila: sem conexao com servidor";
            if (!silent)
            {
                ShowError($"Falha ao atualizar status: {error.Message}");
            }
        }
    }

    private void ShowInfo(string message)
    {
        MessageBox.Show(this, message, "Agente de Impressao", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "Agente de Impressao", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
