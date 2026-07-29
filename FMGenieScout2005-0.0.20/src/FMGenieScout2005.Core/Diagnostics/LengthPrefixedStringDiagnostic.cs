using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class LengthPrefixedStringDiagnostic
{
    private const int MinimumLength = 2;
    private const int MaximumLength = 120;
    private const int MaximumSequenceGap = 96;
    private const int ContextRadius = 64;

    private static readonly string[] DefaultQueries = [
        "Boca Juniors", "Banfield", "Independiente", "Newell's Old Boys",
        "Flamengo", "Palmeiras", "Corinthians", "São Paulo",
        "Ronaldo", "Ronaldo Luís", "Nazário", "Ronaldinho", "Ronaldo de Assis",
        "Robinho", "Robson de Souza", "Adriano", "Adriano Leite",
        "Kaká", "Kaka", "Ricardo Izecson"
    ];

    public async Task<LengthPrefixedStringReport> AnalyzeAsync(
        string inputFile,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string source = Path.GetFullPath(inputFile);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo payload do game_db...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        progress?.Report("Localizando strings com comprimento Int32...");
        List<LengthPrefixedStringEntry> entries = Scan(data, cancellationToken);

        progress?.Report("Identificando sequências estruturais...");
        AssignSequences(entries);
        IReadOnlyList<LengthPrefixedSequence> sequences = BuildSequences(entries);

        progress?.Report("Executando buscas dirigidas...");
        IReadOnlyList<LengthPrefixedSearchHit> hits = BuildSearchHits(entries, DefaultQueries);

        progress?.Report("Classificando possíveis registros de nomes...");
        IReadOnlyList<PossibleNameRecord> nameRecords = BuildPossibleNameRecords(entries);

        var report = new LengthPrefixedStringReport(
            source, data.LongLength, sha, entries, sequences, hits, nameRecords, output, DateTimeOffset.UtcNow);

        progress?.Report("Gravando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "length-prefixed-strings.csv"), FormatEntriesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "sequences.csv"), FormatSequencesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "search-hits.csv"), FormatHitsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "possible-name-records.csv"), FormatNameRecordsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "length-prefixed-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

        progress?.Report("Diagnóstico concluído.");
        return report;
    }

    private static List<LengthPrefixedStringEntry> Scan(byte[] data, CancellationToken cancellationToken)
    {
        var list = new List<LengthPrefixedStringEntry>();
        int index = 1;
        long? previous = null;

        for (int offset = 0; offset <= data.Length - 8; offset++)
        {
            if ((offset & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();

            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (length is < MinimumLength or > MaximumLength) continue;

            int textOffset = offset + 4;
            int byteLength;
            try { byteLength = checked(length * 2); }
            catch (OverflowException) { continue; }
            if (textOffset + byteLength > data.Length) continue;

            ReadOnlySpan<byte> bytes = data.AsSpan(textOffset, byteLength);
            if (!HasUtf16LeShape(bytes)) continue;

            string text;
            try { text = Encoding.Unicode.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }

            double score = PlausibilityScore(text);
            if (score < 0.92 || !HasUsefulContent(text)) continue;

            bool terminator = textOffset + byteLength + 1 < data.Length && data[textOffset + byteLength] == 0 && data[textOffset + byteLength + 1] == 0;
            long? distance = previous.HasValue ? offset - previous.Value : null;
            var entry = new LengthPrefixedStringEntry(
                index++, offset, textOffset, length, text, terminator, score, 0,
                previous, distance,
                ReadInt32(data, offset - 16), ReadInt32(data, offset - 12),
                ReadInt32(data, offset - 8), ReadInt32(data, offset - 4),
                ContextHex(data, textOffset, ContextRadius));
            list.Add(entry);
            previous = offset;

            // Evita reconhecer subcadeias dentro do mesmo texto, mas não pula o possível terminador.
            offset = textOffset + byteLength - 1;
        }

        return list;
    }

    private static bool HasUtf16LeShape(ReadOnlySpan<byte> bytes)
    {
        int zeroHigh = 0;
        int pairs = bytes.Length / 2;
        for (int i = 1; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0) zeroHigh++;
        }
        // Textos latinos do FM 2005 possuem predominantemente byte alto zero.
        return pairs > 0 && zeroHigh >= Math.Max(1, (int)Math.Ceiling(pairs * 0.72));
    }

    private static double PlausibilityScore(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int good = 0;
        foreach (char c in text)
        {
            if (IsPlausibleCharacter(c)) good++;
        }
        return (double)good / text.Length;
    }

    private static bool IsPlausibleCharacter(char c)
    {
        if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)) return true;
        return "-'’._/&()[]+,áàâãäåéèêëíìîïóòôõöøúùûüçñÁÀÂÃÄÅÉÈÊËÍÌÎÏÓÒÔÕÖØÚÙÛÜÇÑß".Contains(c);
    }

    private static bool HasUsefulContent(string text)
    {
        int letters = text.Count(char.IsLetter);
        if (letters == 0) return false;
        if (text.Length >= 4 && text.Distinct().Count() == 1) return false;
        if (text.Any(c => c is >= '\u2E80' and <= '\u9FFF')) return false;
        return true;
    }

    private static void AssignSequences(List<LengthPrefixedStringEntry> entries)
    {
        int sequence = 0;
        long previousEnd = long.MinValue;
        for (int i = 0; i < entries.Count; i++)
        {
            LengthPrefixedStringEntry item = entries[i];
            long end = item.TextOffset + item.Length * 2L + (item.HasNullTerminator ? 2 : 0);
            if (i == 0 || item.LengthOffset - previousEnd > MaximumSequenceGap) sequence++;
            entries[i] = item with { Sequence = sequence };
            previousEnd = end;
        }
    }

    private static IReadOnlyList<LengthPrefixedSequence> BuildSequences(IReadOnlyList<LengthPrefixedStringEntry> entries) => entries
        .GroupBy(x => x.Sequence)
        .Select(group =>
        {
            var ordered = group.OrderBy(x => x.LengthOffset).ToArray();
            double averageGap = ordered.Length <= 1 ? 0 : ordered.Skip(1).Select((x, i) => (double)(x.LengthOffset - (ordered[i].TextOffset + ordered[i].Length * 2L))).Average();
            return new LengthPrefixedSequence(
                group.Key,
                ordered[0].LengthOffset,
                ordered[^1].TextOffset + ordered[^1].Length * 2L,
                ordered.Length,
                averageGap,
                string.Join(" | ", ordered.Take(8).Select(x => x.Text)));
        })
        .ToArray();

    private static IReadOnlyList<LengthPrefixedSearchHit> BuildSearchHits(
        IReadOnlyList<LengthPrefixedStringEntry> entries,
        IEnumerable<string> queries)
    {
        var hits = new List<LengthPrefixedSearchHit>();
        foreach (string query in queries)
        {
            string normalizedQuery = Normalize(query);
            foreach (LengthPrefixedStringEntry item in entries)
            {
                if (Normalize(item.Text).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new LengthPrefixedSearchHit(query, item.LengthOffset, item.TextOffset, item.Text, item.Sequence, item.ContextHex));
            }
        }
        return hits;
    }

    private static IReadOnlyList<PossibleNameRecord> BuildPossibleNameRecords(IReadOnlyList<LengthPrefixedStringEntry> entries)
    {
        var result = new List<PossibleNameRecord>();
        int index = 1;
        foreach (LengthPrefixedStringEntry item in entries)
        {
            bool nameLike = item.Text.Length <= 60 && item.Text.Count(char.IsLetter) >= 2 && item.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
            if (!nameLike) continue;

            bool sequentialIdShape = item.BeforeInt32Minus8 >= 0 && item.BeforeInt32Minus8 < 50_000_000;
            bool referenceShape = item.BeforeInt32Minus4 >= -1;
            string confidence = sequentialIdShape && referenceShape ? "HIGH" : item.Sequence > 0 ? "MEDIUM" : "LOW";
            if (confidence == "LOW") continue;

            result.Add(new PossibleNameRecord(
                index++, Math.Max(0, item.LengthOffset - 16), item.LengthOffset,
                item.BeforeInt32Minus16, item.BeforeInt32Minus12,
                item.BeforeInt32Minus8, item.BeforeInt32Minus4,
                item.Length, item.Text, item.Sequence, confidence));
        }
        return result;
    }

    private static string Normalize(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) return 0;
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static string ContextHex(byte[] data, int center, int radius)
    {
        int start = Math.Max(0, center - radius);
        int length = Math.Min(data.Length - start, radius * 2);
        return Convert.ToHexString(data.AsSpan(start, length));
    }

    public static string FormatReport(LengthPrefixedStringReport report)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var builder = new StringBuilder();
        builder.AppendLine("FM Genie Scout 2005 — LengthPrefixedStringDiagnostic 0.0.6");
        builder.AppendLine(new string('=', 78));
        builder.AppendLine($"Arquivo: {report.SourceFile}");
        builder.AppendLine($"Tamanho: {report.SourceSize.ToString("N0", culture)} bytes");
        builder.AppendLine($"SHA-256: {report.Sha256}");
        builder.AppendLine($"Analisado em UTC: {report.AnalyzedAtUtc:O}");
        builder.AppendLine($"Strings prefixadas válidas: {report.Entries.Count.ToString("N0", culture)}");
        builder.AppendLine($"Sequências estruturais: {report.Sequences.Count.ToString("N0", culture)}");
        builder.AppendLine($"Possíveis registros de nomes: {report.PossibleNameRecords.Count.ToString("N0", culture)}");
        builder.AppendLine($"Ocorrências dirigidas: {report.SearchHits.Count.ToString("N0", culture)}");
        builder.AppendLine();
        builder.AppendLine("Buscas dirigidas:");
        if (report.SearchHits.Count == 0) builder.AppendLine("  Nenhuma ocorrência.");
        foreach (LengthPrefixedSearchHit hit in report.SearchHits)
            builder.AppendLine($"  len=0x{hit.LengthOffset:X8} texto=0x{hit.TextOffset:X8} seq={hit.Sequence,5} | {hit.Query} | {hit.Text}");
        builder.AppendLine();
        builder.AppendLine("Maiores sequências:");
        foreach (LengthPrefixedSequence sequence in report.Sequences.OrderByDescending(x => x.EntryCount).Take(30))
            builder.AppendLine($"  seq={sequence.Sequence,5} entradas={sequence.EntryCount,5} faixa=0x{sequence.StartOffset:X8}-0x{sequence.EndOffset:X8} gap_médio={sequence.AverageGap,7:F2} | {sequence.Preview}");
        builder.AppendLine();
        builder.AppendLine("Arquivos gerados:");
        builder.AppendLine("  length-prefixed-strings.csv");
        builder.AppendLine("  sequences.csv");
        builder.AppendLine("  search-hits.csv");
        builder.AppendLine("  possible-name-records.csv");
        builder.AppendLine("  length-prefixed-report.txt");
        builder.AppendLine("O arquivo de origem não foi modificado.");
        return builder.ToString();
    }

    private static string FormatEntriesCsv(LengthPrefixedStringReport report)
    {
        var builder = new StringBuilder("index,length_offset,text_offset,length,has_null_terminator,plausibility,sequence,previous_length_offset,distance_from_previous,before_i32_minus16,before_i32_minus12,before_i32_minus8,before_i32_minus4,text,context_hex\n");
        foreach (LengthPrefixedStringEntry item in report.Entries)
        {
            builder.Append(item.Index).Append(',').Append($"0x{item.LengthOffset:X8}").Append(',').Append($"0x{item.TextOffset:X8}").Append(',')
                .Append(item.Length).Append(',').Append(item.HasNullTerminator ? "true" : "false").Append(',')
                .Append(item.Plausibility.ToString("F4", CultureInfo.InvariantCulture)).Append(',').Append(item.Sequence).Append(',')
                .Append(item.PreviousLengthOffset.HasValue ? $"0x{item.PreviousLengthOffset:X8}" : "").Append(',').Append(item.DistanceFromPrevious?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(item.BeforeInt32Minus16).Append(',').Append(item.BeforeInt32Minus12).Append(',').Append(item.BeforeInt32Minus8).Append(',').Append(item.BeforeInt32Minus4).Append(',')
                .Append(Csv(item.Text)).Append(',').Append(item.ContextHex).AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatSequencesCsv(LengthPrefixedStringReport report)
    {
        var builder = new StringBuilder("sequence,start_offset,end_offset,entry_count,average_gap,preview\n");
        foreach (LengthPrefixedSequence item in report.Sequences)
            builder.Append(item.Sequence).Append(',').Append($"0x{item.StartOffset:X8}").Append(',').Append($"0x{item.EndOffset:X8}").Append(',').Append(item.EntryCount).Append(',').Append(item.AverageGap.ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(Csv(item.Preview)).AppendLine();
        return builder.ToString();
    }

    private static string FormatHitsCsv(LengthPrefixedStringReport report)
    {
        var builder = new StringBuilder("query,length_offset,text_offset,sequence,text,context_hex\n");
        foreach (LengthPrefixedSearchHit item in report.SearchHits)
            builder.Append(Csv(item.Query)).Append(',').Append($"0x{item.LengthOffset:X8}").Append(',').Append($"0x{item.TextOffset:X8}").Append(',').Append(item.Sequence).Append(',').Append(Csv(item.Text)).Append(',').Append(item.ContextHex).AppendLine();
        return builder.ToString();
    }

    private static string FormatNameRecordsCsv(LengthPrefixedStringReport report)
    {
        var builder = new StringBuilder("index,record_offset,length_offset,field_minus16,field_minus12,field_minus8,field_minus4,name_length,name,sequence,confidence\n");
        foreach (PossibleNameRecord item in report.PossibleNameRecords)
            builder.Append(item.Index).Append(',').Append($"0x{item.RecordOffset:X8}").Append(',').Append($"0x{item.LengthOffset:X8}").Append(',')
                .Append(item.FieldMinus16).Append(',').Append(item.FieldMinus12).Append(',').Append(item.FieldMinus8).Append(',').Append(item.FieldMinus4).Append(',')
                .Append(item.NameLength).Append(',').Append(Csv(item.Name)).Append(',').Append(item.Sequence).Append(',').Append(item.Confidence).AppendLine();
        return builder.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
