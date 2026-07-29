using FMGenieScout2005.Core.Diagnostics;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.App;

public sealed class MainForm : Form
{
    private readonly TextBox _payloadPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _openPayloadButton = new() { Text = "Abrir game_db.payload.bin...", AutoSize = true };
    private readonly Button _analyzeButton = new() { Text = "Investigar jogador → clube...", AutoSize = true, Enabled = false };
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
    private readonly PlayerClubReferenceDiagnostic _diagnostic = new();
    private string? _outputDirectory;

    public MainForm()
    {
        Text = "FM Genie Scout 2005 — PlayerClubReferenceDiagnostic 0.0.20";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 700);
        Size = new Size(1320, 860);

        var header = new Label
        {
            Text = "FM GENIE SCOUT 2005",
            AutoSize = true,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        var subtitle = new Label
        {
            Text = "Diagnóstico 0.0.20: descobrir como jogadores apontam para o clube — somente leitura",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(2, 0, 0, 14)
        };

        var files = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1
        };
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        files.Controls.Add(new Label
        {
            Text = "Payload do save",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 0)
        }, 0, 0);
        files.Controls.Add(_payloadPath, 1, 0);
        files.Controls.Add(_openPayloadButton, 2, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 10)
        };
        actions.Controls.Add(_analyzeButton);
        actions.Controls.Add(_openOutputButton);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            RowCount = 5,
            ColumnCount = 1
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(subtitle, 0, 1);
        content.Controls.Add(files, 0, 2);
        content.Controls.Add(actions, 0, 3);
        content.Controls.Add(_output, 0, 4);

        var status = new StatusStrip();
        status.Items.Add(_status);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(_progress);
        Controls.Add(content);
        Controls.Add(status);

        _openPayloadButton.Click += (_, _) => SelectPayload();
        _analyzeButton.Click += Analyze_Click;
        _openOutputButton.Click += (_, _) => OpenOutputDirectory();
    }

    private void SelectPayload()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o game_db.payload.bin do save que contém o elenco do Flamengo",
            Filter = "Payload (*.bin)|*.bin|Todos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _payloadPath.Text = dialog.FileName;
        _outputDirectory = null;
        _openOutputButton.Enabled = false;
        _analyzeButton.Enabled = true;
        _output.Text = "Payload selecionado. Clique em Investigar jogador → clube...";
        _status.Text = "Payload pronto para análise.";
    }

    private async void Analyze_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_payloadPath.Text)) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha onde criar a pasta dos resultados da investigação jogador → clube",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _outputDirectory = Path.Combine(
            dialog.SelectedPath,
            $"FMGenieScout2005-player-club-{DateTime.Now:yyyyMMdd-HHmmss}");

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            PlayerClubReferenceReport report = await _diagnostic.AnalyzeAsync(
                _payloadPath.Text,
                _outputDirectory,
                progress);

            _output.Text = PlayerClubReferenceDiagnostic.FormatReport(report);
            _status.Text = BuildCompletionStatus(report);
        }
        catch (Exception ex)
        {
            _output.Text = ex.ToString();
            _status.Text = "Falha na análise.";
            MessageBox.Show(this, ex.Message, "Falha", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string BuildCompletionStatus(PlayerClubReferenceReport report)
    {
        PlayerReferenceOffsetSummary? best = report.OffsetSummaries.FirstOrDefault();
        return best is null
            ? "Concluído, mas nenhuma referência candidata foi encontrada."
            : $"Concluído: melhor candidato {best.ReferenceKind}, offset {best.RelativeToNameStart:+0;-0;0}, {best.PlayerCount} jogadores.";
    }

    private void OpenOutputDirectory()
    {
        if (_outputDirectory is null || !Directory.Exists(_outputDirectory)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _outputDirectory,
            UseShellExecute = true
        });
    }

    private void SetBusy(bool busy)
    {
        _openPayloadButton.Enabled = !busy;
        _analyzeButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_payloadPath.Text);
        _openOutputButton.Enabled = !busy && _outputDirectory is not null && Directory.Exists(_outputDirectory);
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }
}
