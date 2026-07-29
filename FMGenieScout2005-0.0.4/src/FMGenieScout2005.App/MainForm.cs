using FMGenieScout2005.Core.Diagnostics;

namespace FMGenieScout2005.App;

public sealed class MainForm : Form
{
    private readonly TextBox _filePath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _openButton = new() { Text = "Abrir game_db...", AutoSize = true };
    private readonly Button _analyzeButton = new() { Text = "Analisar estrutura...", AutoSize = true, Enabled = false };
    private readonly Button _openOutputButton = new() { Text = "Abrir pasta de saída", AutoSize = true, Enabled = false };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F) };
    private readonly ToolStripStatusLabel _status = new("Pronto");
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly GameDbStructureDiagnostic _diagnostic = new();
    private string? _outputDirectory;

    public MainForm()
    {
        Text = "FM Genie Scout 2005 — GameDbStructureDiagnostic 0.0.4";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(940, 620); Size = new Size(1220, 780);
        var header = new Label { Text = "FM GENIE SCOUT 2005", AutoSize = true, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(0,0,0,4) };
        var subtitle = new Label { Text = "Mapeamento experimental do game_db.dat — somente leitura", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2,0,0,14) };
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_filePath,0,0); row.Controls.Add(_openButton,1,0); row.Controls.Add(_analyzeButton,2,0); row.Controls.Add(_openOutputButton,3,0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent,100F));
        content.Controls.Add(header,0,0); content.Controls.Add(subtitle,0,1); content.Controls.Add(row,0,2); content.Controls.Add(_output,0,3);
        var status = new StatusStrip(); status.Items.Add(_status); status.Items.Add(new ToolStripStatusLabel { Spring = true }); status.Items.Add(_progress);
        Controls.Add(content); Controls.Add(status);
        _openButton.Click += Open_Click; _analyzeButton.Click += Analyze_Click; _openOutputButton.Click += OpenOutput_Click;
    }

    private void Open_Click(object? sender, EventArgs e)
    {
        using var d = new OpenFileDialog { Title = "Selecione game_db.dat.raw.bin", Filter = "game_db extraído (*.bin)|*.bin|Todos os arquivos (*.*)|*.*", CheckFileExists = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _filePath.Text = d.FileName; _analyzeButton.Enabled = true; _openOutputButton.Enabled = false; _outputDirectory = null; _output.Text = "Arquivo selecionado. Clique em Analisar estrutura...";
    }

    private async void Analyze_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_filePath.Text)) return;
        using var d = new FolderBrowserDialog { Description = "Escolha a pasta de saída", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _outputDirectory = Path.Combine(d.SelectedPath, $"FMGenieScout2005-gamedb-{DateTime.Now:yyyyMMdd-HHmmss}");
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(m => _status.Text = m);
            var report = await _diagnostic.AnalyzeAsync(_filePath.Text, _outputDirectory, progress);
            _output.Text = GameDbStructureDiagnostic.FormatReport(report); _openOutputButton.Enabled = true;
            _status.Text = $"Concluído: {report.Strings.Count} strings, {report.Groups.Count} grupos, {report.SearchHits.Count} ocorrências.";
        }
        catch (Exception ex) { _output.Text = ex.ToString(); MessageBox.Show(this, ex.Message, "Falha no diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private void OpenOutput_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _outputDirectory, UseShellExecute = true });
    }
    private void SetBusy(bool busy) { _openButton.Enabled = !busy; _analyzeButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_filePath.Text); _openOutputButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_outputDirectory) && Directory.Exists(_outputDirectory); _progress.Visible = busy; UseWaitCursor = busy; }
}
