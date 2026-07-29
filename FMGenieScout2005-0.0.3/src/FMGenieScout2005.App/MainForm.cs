using FMGenieScout2005.Core.Diagnostics;

namespace FMGenieScout2005.App;

public sealed class MainForm : Form
{
    private readonly TextBox _filePath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _openButton = new() { Text = "Abrir save...", AutoSize = true };
    private readonly Button _extractButton = new() { Text = "Extrair componentes...", AutoSize = true, Enabled = false };
    private readonly Button _openOutputButton = new() { Text = "Abrir pasta de saída", AutoSize = true, Enabled = false };
    private readonly TextBox _output = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 10F)
    };
    private readonly ToolStripStatusLabel _status = new("Pronto");
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly ContainerRecordExtractorDiagnostic _diagnostic = new();
    private string? _outputDirectory;

    public MainForm()
    {
        Text = "FM Genie Scout 2005 — ContainerRecordExtractorDiagnostic 0.0.3";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(940, 620);
        Size = new Size(1220, 780);

        var header = new Label
        {
            Text = "FM GENIE SCOUT 2005",
            AutoSize = true,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        var subtitle = new Label
        {
            Text = "Extração experimental dos componentes internos do FM 2005 — somente leitura",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(2, 0, 0, 14)
        };

        var fileRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.Controls.Add(_filePath, 0, 0);
        fileRow.Controls.Add(_openButton, 1, 0);
        fileRow.Controls.Add(_extractButton, 2, 0);
        fileRow.Controls.Add(_openOutputButton, 3, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            RowCount = 4,
            ColumnCount = 1
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(subtitle, 0, 1);
        content.Controls.Add(fileRow, 0, 2);
        content.Controls.Add(_output, 0, 3);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        statusStrip.Items.Add(_progress);

        Controls.Add(content);
        Controls.Add(statusStrip);
        _openButton.Click += OpenButton_Click;
        _extractButton.Click += ExtractButton_Click;
        _openOutputButton.Click += OpenOutputButton_Click;
    }

    private void OpenButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione um save do Football Manager 2005",
            Filter = "Save do Football Manager (*.fm)|*.fm|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _filePath.Text = dialog.FileName;
        _extractButton.Enabled = true;
        _openOutputButton.Enabled = false;
        _outputDirectory = null;
        _output.Text = "Save selecionado. Clique em Extrair componentes...";
    }

    private async void ExtractButton_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_filePath.Text)) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta onde os componentes extraídos serão gravados",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string folderName = $"FMGenieScout2005-extracted-{DateTime.Now:yyyyMMdd-HHmmss}";
        _outputDirectory = Path.Combine(dialog.SelectedPath, folderName);
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var report = await _diagnostic.AnalyzeAndExtractAsync(_filePath.Text, _outputDirectory, progress);
            _output.Text = ContainerRecordExtractorDiagnostic.FormatReport(report);
            _openOutputButton.Enabled = true;
            _status.Text = $"Concluído: {report.Records.Count(x => x.Extracted)} registros extraídos; {report.RejectedMarkers.Count} marcadores rejeitados.";
        }
        catch (Exception exception)
        {
            _output.Text = exception.ToString();
            MessageBox.Show(this, exception.Message, "Falha na extração", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenOutputButton_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _outputDirectory,
            UseShellExecute = true
        });
    }

    private void SetBusy(bool busy)
    {
        _openButton.Enabled = !busy;
        _extractButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_filePath.Text);
        _openOutputButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_outputDirectory) && Directory.Exists(_outputDirectory);
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }
}
