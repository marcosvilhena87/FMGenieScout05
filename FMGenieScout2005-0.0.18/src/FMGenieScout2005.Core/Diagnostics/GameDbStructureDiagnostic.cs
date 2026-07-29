using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class GameDbStructureDiagnostic
{
    private static readonly string[] DefaultQueries = [
        "Boca Juniors", "Banfield", "Independiente", "Newell's Old Boys",
        "Ronaldinho", "Ronaldo", "Kaká", "Kaka", "Adriano", "Robinho"
    ];

    public async Task<GameDbStructureReport> AnalyzeAsync(string inputFile, string outputDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string source = Path.GetFullPath(inputFile);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db não existe.", source);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo game_db...");
        byte[] raw = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        int payloadOffset = DetectPayloadOffset(raw);
        byte[] payload = raw[payloadOffset..];
        await File.WriteAllBytesAsync(Path.Combine(output, "game_db.payload.bin"), payload, cancellationToken).ConfigureAwait(false);

        progress?.Report("Indexando strings UTF-16LE...");
        var entries = ScanUtf16Strings(payload, minimumLength: 3, maximumLength: 160);
        AssignGroups(entries, 96);
        var groups = BuildGroups(entries);
        var hits = BuildSearchHits(payload, entries, DefaultQueries);

        var report = new GameDbStructureReport(source, raw.LongLength, sha, payloadOffset, payload.LongLength, output, entries, groups, hits, DateTimeOffset.UtcNow);
        progress?.Report("Gravando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "strings.csv"), FormatStringsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "groups.csv"), FormatGroupsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "search-hits.csv"), FormatHitsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "game-db-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico concluído.");
        return report;
    }

    private static int DetectPayloadOffset(byte[] data)
    {
        // Cabeçalho observado: marcador + campo de nome fixo + extensão. O payload costuma começar em 0x220.
        int[] candidates = [0x220, 0x21F, 0x221, 0x218];
        foreach (int candidate in candidates)
        {
            if (candidate < data.Length && HasNonZeroData(data, candidate, Math.Min(64, data.Length - candidate))) return candidate;
        }
        return Math.Min(0x220, data.Length);
    }

    private static bool HasNonZeroData(byte[] data, int offset, int count)
    {
        int nonZero = 0;
        for (int i = 0; i < count; i++) if (data[offset + i] != 0) nonZero++;
        return nonZero >= 4;
    }

    private static List<GameDbStringEntry> ScanUtf16Strings(byte[] data, int minimumLength, int maximumLength)
    {
        var list = new List<GameDbStringEntry>();
        int index = 1;
        long? previous = null;
        for (int offset = 0; offset + 1 < data.Length;)
        {
            int cursor = offset;
            var chars = new List<char>();
            while (cursor + 1 < data.Length && chars.Count < maximumLength)
            {
                ushort value = (ushort)(data[cursor] | data[cursor + 1] << 8);
                if (value == 0) break;
                char c = (char)value;
                if (!IsPlausible(c)) break;
                chars.Add(c);
                cursor += 2;
            }
            if (chars.Count >= minimumLength && cursor + 1 < data.Length && data[cursor] == 0 && data[cursor + 1] == 0)
            {
                string text = new(chars.ToArray());
                long? distance = previous.HasValue ? offset - previous.Value : null;
                list.Add(new GameDbStringEntry(index++, offset, chars.Count, text, previous, distance, 0));
                previous = offset;
                offset = cursor + 2;
            }
            else offset++;
        }
        return list;
    }

    private static bool IsPlausible(char c) => !char.IsControl(c) && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || "-_'./&()[]áàâãäéèêëíìîïóòôõöúùûüçÁÀÂÃÄÉÈÊËÍÌÎÏÓÒÔÕÖÚÙÛÜÇ".Contains(c));

    private static void AssignGroups(List<GameDbStringEntry> entries, int maximumGap)
    {
        int group = 0;
        long previousEnd = long.MinValue;
        for (int i = 0; i < entries.Count; i++)
        {
            var item = entries[i];
            if (i == 0 || item.Offset - previousEnd > maximumGap) group++;
            entries[i] = item with { Group = group };
            previousEnd = item.Offset + item.Length * 2L + 2;
        }
    }

    private static List<GameDbGroup> BuildGroups(List<GameDbStringEntry> entries) => entries
        .GroupBy(x => x.Group)
        .Select(g => new GameDbGroup(g.Key, g.Min(x => x.Offset), g.Max(x => x.Offset + x.Length * 2L + 2), g.Count(), string.Join(" | ", g.Take(6).Select(x => x.Text))))
        .ToList();

    private static List<GameDbSearchHit> BuildSearchHits(byte[] payload, List<GameDbStringEntry> entries, IEnumerable<string> queries)
    {
        var hits = new List<GameDbSearchHit>();
        foreach (string query in queries)
        {
            foreach (var item in entries.Where(x => x.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
                hits.Add(new GameDbSearchHit(query, item.Offset, item.Text, ContextHex(payload, checked((int)item.Offset), 64)));
        }
        return hits;
    }

    private static string ContextHex(byte[] data, int center, int radius)
    {
        int start = Math.Max(0, center - radius);
        int length = Math.Min(data.Length - start, radius * 2);
        return Convert.ToHexString(data.AsSpan(start, length));
    }

    public static string FormatReport(GameDbStructureReport r)
    {
        var b = new StringBuilder();
        b.AppendLine("FM Genie Scout 2005 — GameDbStructureDiagnostic 0.0.4");
        b.AppendLine(new string('=', 72));
        b.AppendLine($"Arquivo: {r.SourceFile}");
        b.AppendLine($"Tamanho bruto: {r.SourceSize.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} bytes");
        b.AppendLine($"SHA-256: {r.Sha256}");
        b.AppendLine($"Offset estimado do payload: 0x{r.PayloadOffset:X}");
        b.AppendLine($"Tamanho do payload: {r.PayloadSize.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} bytes");
        b.AppendLine($"Strings UTF-16LE: {r.Strings.Count}");
        b.AppendLine($"Grupos por proximidade: {r.Groups.Count}");
        b.AppendLine($"Ocorrências dirigidas: {r.SearchHits.Count}");
        b.AppendLine();
        b.AppendLine("Buscas dirigidas:");
        if (r.SearchHits.Count == 0) b.AppendLine("  Nenhuma ocorrência.");
        foreach (var hit in r.SearchHits) b.AppendLine($"  0x{hit.Offset:X8} | {hit.Query} | {hit.Text}");
        b.AppendLine();
        b.AppendLine("Maiores grupos de strings:");
        foreach (var g in r.Groups.OrderByDescending(x => x.StringCount).Take(30))
            b.AppendLine($"  grupo={g.Group,5} strings={g.StringCount,4} faixa=0x{g.StartOffset:X8}-0x{g.EndOffset:X8} | {g.Preview}");
        b.AppendLine();
        b.AppendLine("Arquivos gerados: game_db.payload.bin, strings.csv, groups.csv, search-hits.csv e game-db-report.txt.");
        b.AppendLine("O arquivo de origem não foi modificado.");
        return b.ToString();
    }

    private static string FormatStringsCsv(GameDbStructureReport r)
    {
        var b = new StringBuilder("index,offset,length,group,previous_offset,distance_from_previous,text\n");
        foreach (var x in r.Strings) b.Append(x.Index).Append(',').Append($"0x{x.Offset:X8}").Append(',').Append(x.Length).Append(',').Append(x.Group).Append(',').Append(x.PreviousOffset.HasValue ? $"0x{x.PreviousOffset:X8}" : "").Append(',').Append(x.DistanceFromPrevious?.ToString() ?? "").Append(',').Append(Csv(x.Text)).AppendLine();
        return b.ToString();
    }
    private static string FormatGroupsCsv(GameDbStructureReport r)
    {
        var b = new StringBuilder("group,start_offset,end_offset,string_count,preview\n");
        foreach (var x in r.Groups) b.Append(x.Group).Append(',').Append($"0x{x.StartOffset:X8}").Append(',').Append($"0x{x.EndOffset:X8}").Append(',').Append(x.StringCount).Append(',').Append(Csv(x.Preview)).AppendLine();
        return b.ToString();
    }
    private static string FormatHitsCsv(GameDbStructureReport r)
    {
        var b = new StringBuilder("query,offset,text,context_hex\n");
        foreach (var x in r.SearchHits) b.Append(Csv(x.Query)).Append(',').Append($"0x{x.Offset:X8}").Append(',').Append(Csv(x.Text)).Append(',').Append(x.ContextHex).AppendLine();
        return b.ToString();
    }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
