using FMGenieScout2005.Core.Diagnostics;

namespace FMGenieScout2005.App;

public sealed class MainForm : Form
{
    private readonly TextBox _save1Path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _save2Path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _openSave1Button = new() { Text = "Abrir Save 1...", AutoSize = true };
    private readonly Button _openSave2Button = new() { Text = "Abrir Save 2...", AutoSize = true };
    private readonly Button _analyzeButton = new() { Text = "Comparar identidade dos clubes...", AutoSize = true, Enabled = false };
    private readonly Button _openOutputButton = new() { Text = "Abrir pasta de saída", AutoSize = true, Enabled = false };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F) };
    private readonly ToolStripStatusLabel _status = new("Pronto");
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly MultiSaveClubIdentityDiagnostic _diagnostic = new();
    private string? _outputDirectory;

    public MainForm()
    {
        Text = "FM Genie Scout 2005 — MultiSaveClubIdentityDiagnostic 0.0.18";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 700);
        Size = new Size(1320, 860);

        var header = new Label { Text = "FM GENIE SCOUT 2005", AutoSize = true, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        var subtitle = new Label { Text = "Teste multi-save: ClubDatabaseId fixo × SaveClubIndex local — somente leitura", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 0, 14) };
        var files = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2 };
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        files.Controls.Add(new Label { Text = "Save 1", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 0);
        files.Controls.Add(_save1Path, 1, 0); files.Controls.Add(_openSave1Button, 2, 0);
        files.Controls.Add(new Label { Text = "Save 2", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 1);
        files.Controls.Add(_save2Path, 1, 1); files.Controls.Add(_openSave2Button, 2, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
        actions.Controls.Add(_analyzeButton); actions.Controls.Add(_openOutputButton);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(header, 0, 0); content.Controls.Add(subtitle, 0, 1); content.Controls.Add(files, 0, 2); content.Controls.Add(actions, 0, 3); content.Controls.Add(_output, 0, 4);
        var status = new StatusStrip(); status.Items.Add(_status); status.Items.Add(new ToolStripStatusLabel { Spring = true }); status.Items.Add(_progress);
        Controls.Add(content); Controls.Add(status);

        _openSave1Button.Click += (_, _) => SelectFile(_save1Path, "Selecione o game_db.payload.bin do Save 1");
        _openSave2Button.Click += (_, _) => SelectFile(_save2Path, "Selecione o game_db.payload.bin do Save 2");
        _analyzeButton.Click += Analyze_Click;
        _openOutputButton.Click += (_, _) => { if (_outputDirectory is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _outputDirectory, UseShellExecute = true }); };
    }

    private void SelectFile(TextBox target, string title)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = "Payload (*.bin)|*.bin|Todos (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dialog.FileName; _openOutputButton.Enabled = false; _outputDirectory = null;
        _analyzeButton.Enabled = !string.IsNullOrWhiteSpace(_save1Path.Text) && !string.IsNullOrWhiteSpace(_save2Path.Text);
        _output.Text = "Dois arquivos selecionados. Clique em Comparar identidade dos clubes...";
    }

    private async void Analyze_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_save1Path.Text) || string.IsNullOrWhiteSpace(_save2Path.Text)) return;
        using var dialog = new FolderBrowserDialog { Description = "Escolha a pasta de saída", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _outputDirectory = Path.Combine(dialog.SelectedPath, $"FMGenieScout2005-multisave-{DateTime.Now:yyyyMMdd-HHmmss}");
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var report = await _diagnostic.AnalyzeAsync(_save1Path.Text, _save2Path.Text, _outputDirectory, progress);
            _output.Text = MultiSaveClubIdentityDiagnostic.FormatReport(report);
            _openOutputButton.Enabled = true;
            _status.Text = $"Concluído: {report.SharedClubCount} compartilhados; {report.ChangedIndexCount} índices alterados.";
        }
        catch (Exception ex)
        {
            _output.Text = ex.ToString(); MessageBox.Show(this, ex.Message, "Falha", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _openSave1Button.Enabled = !busy; _openSave2Button.Enabled = !busy;
        _analyzeButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_save1Path.Text) && !string.IsNullOrWhiteSpace(_save2Path.Text);
        _openOutputButton.Enabled = !busy && _outputDirectory is not null && Directory.Exists(_outputDirectory);
        _progress.Visible = busy; UseWaitCursor = busy;
    }
}
