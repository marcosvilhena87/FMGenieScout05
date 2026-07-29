using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class NameTableRecordDiagnostic
{
    private const int HeaderSize = 14;
    private const int MinimumLength = 2;
    private const int MaximumLength = 120;
    private const int ContextRadius = 80;
    private const int MaximumReferenceHitsPerValue = 100;

    private static readonly string[] PlayerQueries = [
        "Ronaldo", "Nazário de Lima", "Ronaldinho", "Robinho",
        "Adriano Leite", "Kaká", "Ricardo Izecson"
    ];

    private static readonly string[] ClubQueries = [
        "CR Flamengo", "Flamengo", "SC Corinthians Paulista", "Corinthians",
        "SE Palmeiras", "Palmeiras", "São Paulo FC", "São Paulo",
        "Boca Juniors", "Banfield", "Independiente", "Newell's Old Boys"
    ];

    public async Task<NameTableRecordReport> AnalyzeAsync(
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

        progress?.Report("Interpretando registros de nomes com cabeçalho de 14 bytes...");
        List<NameTableRecord> records = ScanRecords(data, cancellationToken);
        AssignSequencesAndConfidence(records);

        progress?.Report("Calculando estatísticas por tipo e categoria...");
        IReadOnlyList<NameTypeStatistic> statistics = BuildStatistics(records);

        progress?.Report("Localizando jogadores dirigidos...");
        IReadOnlyList<TargetedNameRecord> targeted = BuildTargetedRecords(records, PlayerQueries);

        progress?.Report("Rastreando índices e referências no payload...");
        IReadOnlyList<ReferenceOccurrence> references = FindReferenceOccurrences(data, targeted, cancellationToken);

        progress?.Report("Comparando pares de nomes de clubes...");
        IReadOnlyList<ClubStringPair> clubPairs = FindClubPairs(data, ClubQueries, cancellationToken);

        var report = new NameTableRecordReport(
            source, data.LongLength, sha, records, statistics, targeted,
            references, clubPairs, output, DateTimeOffset.UtcNow);

        progress?.Report("Gravando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "name-table-records.csv"), FormatRecordsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "name-type-statistics.csv"), FormatStatisticsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "targeted-name-records.csv"), FormatTargetedCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "reference-occurrences.csv"), FormatReferencesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-string-pairs.csv"), FormatClubPairsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "name-table-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

        progress?.Report("Diagnóstico concluído.");
        return report;
    }

    private static List<NameTableRecord> ScanRecords(byte[] data, CancellationToken cancellationToken)
    {
        var records = new List<NameTableRecord>();
        int row = 1;

        for (int recordOffset = 0; recordOffset <= data.Length - HeaderSize - 8; recordOffset++)
        {
            if ((recordOffset & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(recordOffset, 2));
            uint category = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset + 2, 4));
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset + 6, 4));
            uint reference = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset + 10, 4));
            int lengthOffset = recordOffset + HeaderSize;
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(lengthOffset, 4));

            if (length is < MinimumLength or > MaximumLength) continue;
            if (type > 0x03FF) continue;
            if (category == 0 || category > 10_000_000) continue;
            if (index > 100_000_000) continue;

            int textOffset = lengthOffset + 4;
            int byteLength;
            try { byteLength = checked(length * 2); }
            catch (OverflowException) { continue; }
            if (textOffset + byteLength > data.Length) continue;

            ReadOnlySpan<byte> textBytes = data.AsSpan(textOffset, byteLength);
            if (!HasUtf16LeShape(textBytes)) continue;
            string text = Encoding.Unicode.GetString(textBytes);
            if (!IsPlausibleName(text)) continue;

            bool terminator = textOffset + byteLength + 1 < data.Length &&
                              data[textOffset + byteLength] == 0 && data[textOffset + byteLength + 1] == 0;
            long nextOffset = textOffset + byteLength + (terminator ? 2 : 0);
            int recordSize = checked((int)(nextOffset - recordOffset));

            records.Add(new NameTableRecord(
                row++, recordOffset, type, category, index, reference,
                lengthOffset, length, text, terminator, nextOffset,
                recordSize, 0, false, "PENDING", ContextHex(data, recordOffset, ContextRadius)));

            // O próximo registro normalmente começa logo após o terminador.
            recordOffset = Math.Max(recordOffset, checked((int)nextOffset - 1));
        }

        return records;
    }

    private static void AssignSequencesAndConfidence(List<NameTableRecord> records)
    {
        int sequence = 0;
        NameTableRecord? previous = null;

        for (int i = 0; i < records.Count; i++)
        {
            NameTableRecord current = records[i];
            bool adjacent = previous is not null && current.RecordOffset - previous.NextOffset is >= 0 and <= 16;
            bool sameCategory = previous is not null && current.Category == previous.Category;
            if (previous is null || !adjacent || !sameCategory) sequence++;

            bool sequential = previous is not null && sameCategory && current.Index == previous.Index + 1;
            string confidence = current.HasNullTerminator && current.Category == 189 && sequential
                ? "HIGH"
                : current.HasNullTerminator && sameCategory && adjacent
                    ? "MEDIUM"
                    : "LOW";

            records[i] = current with
            {
                Sequence = sequence,
                IndexSequentialFromPrevious = sequential,
                Confidence = confidence
            };
            previous = records[i];
        }
    }

    private static IReadOnlyList<NameTypeStatistic> BuildStatistics(IReadOnlyList<NameTableRecord> records) => records
        .GroupBy(x => new { x.Type, x.Category })
        .Select(group =>
        {
            NameTableRecord[] ordered = group.OrderBy(x => x.RecordOffset).ToArray();
            int transitions = Math.Max(0, ordered.Length - 1);
            int sequential = ordered.Skip(1).Select((x, i) => x.Index == ordered[i].Index + 1 ? 1 : 0).Sum();
            return new NameTypeStatistic(
                group.Key.Type,
                group.Key.Category,
                ordered.Length,
                sequential,
                transitions == 0 ? 0 : (double)sequential / transitions,
                ordered.Min(x => x.Index),
                ordered.Max(x => x.Index),
                string.Join(" | ", ordered.Take(10).Select(x => x.Name)));
        })
        .OrderByDescending(x => x.Count)
        .ThenBy(x => x.Type)
        .ToArray();

    private static IReadOnlyList<TargetedNameRecord> BuildTargetedRecords(
        IReadOnlyList<NameTableRecord> records,
        IEnumerable<string> queries)
    {
        var result = new List<TargetedNameRecord>();
        foreach (string query in queries)
        {
            string normalized = Normalize(query);
            foreach (NameTableRecord item in records.Where(x => Normalize(x.Name).Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                NameTableRecord[] sameSequence = records.Where(x => x.Sequence == item.Sequence).OrderBy(x => x.RecordOffset).ToArray();
                int position = Array.FindIndex(sameSequence, x => x.RecordOffset == item.RecordOffset);
                string previous = string.Join(" | ", sameSequence.Skip(Math.Max(0, position - 3)).Take(Math.Min(3, position)).Select(x => x.Name));
                string next = string.Join(" | ", sameSequence.Skip(position + 1).Take(3).Select(x => x.Name));
                result.Add(new TargetedNameRecord(
                    query, item.RecordOffset, item.Type, item.Category, item.Index,
                    item.Reference, item.Name, item.Sequence, previous, next, item.ContextHex));
            }
        }
        return result.OrderBy(x => x.RecordOffset).ThenBy(x => x.Query).ToArray();
    }

    private static IReadOnlyList<ReferenceOccurrence> FindReferenceOccurrences(
        byte[] data,
        IReadOnlyList<TargetedNameRecord> targets,
        CancellationToken cancellationToken)
    {
        var output = new List<ReferenceOccurrence>();
        var values = targets
            .SelectMany(x => new[] { (x.Name, Field: "Index", Value: x.Index, Record: x.RecordOffset), (x.Name, Field: "Reference", Value: x.Reference, Record: x.RecordOffset) })
            .Where(x => x.Value != 0 && x.Value != uint.MaxValue)
            .DistinctBy(x => (x.Field, x.Value))
            .ToArray();

        foreach (var target in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] needle = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, target.Value);
            int hits = 0;
            for (int offset = 0; offset <= data.Length - 4 && hits < MaximumReferenceHitsPerValue; offset++)
            {
                if (data[offset] != needle[0] || data[offset + 1] != needle[1] || data[offset + 2] != needle[2] || data[offset + 3] != needle[3]) continue;
                string relation = offset >= target.Record - 32 && offset <= target.Record + 64 ? "WITHIN_SOURCE_RECORD" : "EXTERNAL";
                output.Add(new ReferenceOccurrence(target.Name, target.Field, target.Value, offset, relation, ContextHex(data, offset, 48)));
                hits++;
            }
        }
        return output;
    }

    private static IReadOnlyList<ClubStringPair> FindClubPairs(
        byte[] data,
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        List<SimpleString> strings = ScanLengthPrefixedStrings(data, cancellationToken)
            .Where(x => queries.Any(q => Normalize(x.Text).Contains(Normalize(q), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.LengthOffset)
            .ToList();

        var pairs = new List<ClubStringPair>();
        foreach (SimpleString full in strings)
        {
            SimpleString? next = strings.FirstOrDefault(x => x.LengthOffset > full.LengthOffset && x.LengthOffset - full.EndOffset <= 96);
            if (next is null) continue;
            string query = queries.First(q => Normalize(full.Text).Contains(Normalize(q), StringComparison.OrdinalIgnoreCase));
            pairs.Add(new ClubStringPair(query, full.Text, next.Text, full.LengthOffset, next.LengthOffset, 0, ContextHex(data, checked((int)full.LengthOffset), 160)));
        }
        return pairs.DistinctBy(x => (x.FullNameLengthOffset, x.ShortNameLengthOffset)).ToArray();
    }

    private static IEnumerable<SimpleString> ScanLengthPrefixedStrings(byte[] data, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset <= data.Length - 8; offset++)
        {
            if ((offset & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (length is < MinimumLength or > MaximumLength) continue;
            int byteLength = length * 2;
            int textOffset = offset + 4;
            if (textOffset + byteLength > data.Length) continue;
            ReadOnlySpan<byte> bytes = data.AsSpan(textOffset, byteLength);
            if (!HasUtf16LeShape(bytes)) continue;
            string text = Encoding.Unicode.GetString(bytes);
            if (!IsPlausibleName(text)) continue;
            bool terminator = textOffset + byteLength + 1 < data.Length && data[textOffset + byteLength] == 0 && data[textOffset + byteLength + 1] == 0;
            long end = textOffset + byteLength + (terminator ? 2 : 0);
            yield return new SimpleString(offset, textOffset, end, text);
            offset = checked((int)end - 1);
        }
    }

    private static bool HasUtf16LeShape(ReadOnlySpan<byte> bytes)
    {
        int pairs = bytes.Length / 2;
        int zeroHigh = 0;
        for (int i = 1; i < bytes.Length; i += 2) if (bytes[i] == 0) zeroHigh++;
        return pairs > 0 && zeroHigh >= Math.Max(1, (int)Math.Ceiling(pairs * 0.70));
    }

    private static bool IsPlausibleName(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLength) return false;
        if (text.Count(char.IsLetter) < 2) return false;
        if (text.Any(c => c is >= '\u2E80' and <= '\u9FFF')) return false;
        int good = text.Count(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || "-'’._/&()[]+,".Contains(c));
        return (double)good / text.Length >= 0.90;
    }

    private static string Normalize(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ContextHex(byte[] data, long center, int radius)
    {
        int safeCenter = checked((int)Math.Clamp(center, 0, data.LongLength));
        int start = Math.Max(0, safeCenter - radius);
        int length = Math.Min(data.Length - start, radius * 2);
        return Convert.ToHexString(data.AsSpan(start, length));
    }

    public static string FormatReport(NameTableRecordReport report)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var b = new StringBuilder();
        b.AppendLine("FM Genie Scout 2005 — NameTableRecordDiagnostic 0.0.6");
        b.AppendLine(new string('=', 78));
        b.AppendLine($"Arquivo: {report.SourceFile}");
        b.AppendLine($"Tamanho: {report.SourceSize.ToString("N0", culture)} bytes");
        b.AppendLine($"SHA-256: {report.Sha256}");
        b.AppendLine($"Analisado em UTC: {report.AnalyzedAtUtc:O}");
        b.AppendLine($"Registros estruturados aceitos: {report.Records.Count.ToString("N0", culture)}");
        b.AppendLine($"Combinações tipo/categoria: {report.TypeStatistics.Count.ToString("N0", culture)}");
        b.AppendLine($"Registros dirigidos: {report.TargetedRecords.Count.ToString("N0", culture)}");
        b.AppendLine($"Ocorrências de índices/referências: {report.ReferenceOccurrences.Count.ToString("N0", culture)}");
        b.AppendLine($"Pares candidatos de clubes: {report.ClubPairs.Count.ToString("N0", culture)}");
        b.AppendLine();
        b.AppendLine("Tipos/categorias mais frequentes:");
        foreach (NameTypeStatistic s in report.TypeStatistics.Take(30))
            b.AppendLine($"  type={s.Type,3} category={s.Category,8} registros={s.Count,7} sequencial={s.SequentialRate:P1} índices={s.MinimumIndex}-{s.MaximumIndex} | {s.Examples}");
        b.AppendLine();
        b.AppendLine("Jogadores e nomes dirigidos:");
        foreach (TargetedNameRecord x in report.TargetedRecords)
            b.AppendLine($"  0x{x.RecordOffset:X8} | {x.Query} -> {x.Name} | type={x.Type} cat={x.Category} index={x.Index} ref={x.Reference} seq={x.Sequence}");
        b.AppendLine();
        b.AppendLine("Arquivos gerados:");
        b.AppendLine("  name-table-records.csv");
        b.AppendLine("  name-type-statistics.csv");
        b.AppendLine("  targeted-name-records.csv");
        b.AppendLine("  reference-occurrences.csv");
        b.AppendLine("  club-string-pairs.csv");
        b.AppendLine("  name-table-report.txt");
        b.AppendLine("O arquivo de origem não foi modificado.");
        return b.ToString();
    }

    private static string FormatRecordsCsv(NameTableRecordReport report)
    {
        var b = new StringBuilder("row,record_offset,type,category,index,reference,length_offset,name_length,name,terminator,next_offset,record_size,sequence,sequential,confidence,context_hex\r\n");
        foreach (NameTableRecord x in report.Records)
            b.AppendLine(string.Join(',', x.RowNumber, Hex(x.RecordOffset), x.Type, x.Category, x.Index, x.Reference, Hex(x.LengthOffset), x.NameLength, Csv(x.Name), x.HasNullTerminator, Hex(x.NextOffset), x.RecordSize, x.Sequence, x.IndexSequentialFromPrevious, x.Confidence, x.ContextHex));
        return b.ToString();
    }

    private static string FormatStatisticsCsv(NameTableRecordReport report)
    {
        var b = new StringBuilder("type,category,count,sequential_transitions,sequential_rate,min_index,max_index,examples\r\n");
        foreach (NameTypeStatistic x in report.TypeStatistics)
            b.AppendLine(string.Join(',', x.Type, x.Category, x.Count, x.SequentialTransitions, x.SequentialRate.ToString("F6", CultureInfo.InvariantCulture), x.MinimumIndex, x.MaximumIndex, Csv(x.Examples)));
        return b.ToString();
    }

    private static string FormatTargetedCsv(NameTableRecordReport report)
    {
        var b = new StringBuilder("query,record_offset,type,category,index,reference,name,sequence,previous_names,next_names,context_hex\r\n");
        foreach (TargetedNameRecord x in report.TargetedRecords)
            b.AppendLine(string.Join(',', Csv(x.Query), Hex(x.RecordOffset), x.Type, x.Category, x.Index, x.Reference, Csv(x.Name), x.Sequence, Csv(x.PreviousNames), Csv(x.NextNames), x.ContextHex));
        return b.ToString();
    }

    private static string FormatReferencesCsv(NameTableRecordReport report)
    {
        var b = new StringBuilder("source_name,source_field,value,offset,relation,context_hex\r\n");
        foreach (ReferenceOccurrence x in report.ReferenceOccurrences)
            b.AppendLine(string.Join(',', Csv(x.SourceName), x.SourceField, x.Value, Hex(x.Offset), x.RelativeToRecord, x.ContextHex));
        return b.ToString();
    }

    private static string FormatClubPairsCsv(NameTableRecordReport report)
    {
        var b = new StringBuilder("query,full_name,short_name,full_name_length_offset,short_name_length_offset,sequence,context_hex\r\n");
        foreach (ClubStringPair x in report.ClubPairs)
            b.AppendLine(string.Join(',', Csv(x.Query), Csv(x.FullName), Csv(x.ShortName), Hex(x.FullNameLengthOffset), Hex(x.ShortNameLengthOffset), x.Sequence, x.ContextHex));
        return b.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Hex(long value) => $"0x{value:X8}";
    private sealed record SimpleString(long LengthOffset, long TextOffset, long EndOffset, string Text);
}
