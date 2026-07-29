using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubSequenceScannerDiagnostic
{
    private const int MinLength = 2;
    private const int MaxLength = 100;
    private const int MaxPairGap = 12;
    private const int FoundationSearchBack = 180;
    private const int FoundationSearchForward = 24;
    private const long DefaultRegionStart = 0x0008D000;
    private const long DefaultRegionEnd = 0x00100000;

    private static readonly Dictionary<string, ushort> KnownFoundationYears = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Banfield"] = 1896,
        ["Boca Juniors"] = 1905,
        ["Independiente"] = 1905,
        ["Newell's Old Boys"] = 1903,
        ["CR Flamengo"] = 1895,
        ["SC Corinthians Paulista"] = 1910,
        ["SE Palmeiras"] = 1914,
        ["São Paulo FC"] = 1930
    };

    public async Task<ClubSequenceReport> AnalyzeAsync(string inputFile, string outputDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(inputFile);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo payload do game_db...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        long regionStart = Math.Min(DefaultRegionStart, data.LongLength);
        long regionEnd = Math.Min(DefaultRegionEnd, data.LongLength);

        progress?.Report("Indexando strings prefixadas na região de clubes...");
        List<ClubStringEntry> strings = ScanStrings(data, checked((int)regionStart), checked((int)regionEnd), cancellationToken);

        progress?.Report("Detectando pares nome completo/nome curto...");
        List<(ClubStringEntry Full, ClubStringEntry Short, int Score, string Validation)> pairs = FindPairs(data, strings);

        progress?.Report("Estimando limites e anos de fundação...");
        var clubs = new List<ClubSequenceRecord>();
        var yearCandidates = new List<ClubYearCandidate>();
        for (int i = 0; i < pairs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = pairs[i];
            long start = FindEstimatedStart(data, pair.Full.LengthOffset, i == 0 ? regionStart : pairs[i - 1].Short.EndOffset);
            long next = i + 1 < pairs.Count ? pairs[i + 1].Full.LengthOffset : regionEnd;
            long size = Math.Max(0, next - start);
            List<(long Offset, ushort Value)> years = FindYears(data, pair.Full.LengthOffset);
            ushort? selectedYear = null;
            long? selectedOffset = null;
            string yearValidation = "SEM_ANO";

            if (KnownFoundationYears.TryGetValue(pair.Full.Text, out ushort known))
            {
                var exact = years.OrderBy(x => Math.Abs(x.Offset - pair.Full.LengthOffset)).FirstOrDefault(x => x.Value == known);
                if (exact != default)
                {
                    selectedYear = exact.Value;
                    selectedOffset = exact.Offset;
                    yearValidation = "ANO_CONFIRMADO";
                }
                else yearValidation = $"ANO_ESPERADO_{known}_NAO_ENCONTRADO";
            }
            else
            {
                var plausible = years.OrderBy(x => Math.Abs(x.Offset - pair.Full.LengthOffset)).FirstOrDefault();
                if (plausible != default)
                {
                    selectedYear = plausible.Value;
                    selectedOffset = plausible.Offset;
                    yearValidation = "ANO_CANDIDATO";
                }
            }

            foreach (var y in years)
                yearCandidates.Add(new ClubYearCandidate(i + 1, pair.Full.Text, y.Offset, checked((int)(y.Offset - pair.Full.LengthOffset)), y.Value,
                    selectedOffset == y.Offset ? yearValidation : "CANDIDATO"));

            string confidence = pair.Score >= 8 && yearValidation == "ANO_CONFIRMADO" ? "HIGH" : pair.Score >= 7 ? "MEDIUM" : "LOW";
            clubs.Add(new ClubSequenceRecord(i + 1, start, pair.Full.LengthOffset, pair.Full.TextOffset, pair.Full.Text,
                pair.Short.LengthOffset, pair.Short.TextOffset, pair.Short.Text, next, size, selectedYear, selectedOffset,
                selectedOffset.HasValue ? checked((int)(selectedOffset.Value - pair.Full.LengthOffset)) : null,
                checked((int)(pair.Short.LengthOffset - pair.Full.EndOffset)), confidence, pair.Validation + ";" + yearValidation));
        }

        progress?.Report("Calculando distribuição de tamanhos e campos comuns...");
        IReadOnlyList<ClubSizeBucket> buckets = BuildSizeBuckets(clubs);
        IReadOnlyList<ClubCommonField> commonFields = BuildCommonFields(data, clubs);
        var report = new ClubSequenceReport(source, data.LongLength, sha, regionStart, regionEnd, clubs, yearCandidates, buckets, commonFields, output, DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(Path.Combine(output, "all-club-records.csv"), FormatClubsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-foundation-years.csv"), FormatYearsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-sizes.csv"), FormatSizesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-common-fields.csv"), FormatFieldsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-sequence-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico concluído.");
        return report;
    }

    private static List<ClubStringEntry> ScanStrings(byte[] data, int start, int end, CancellationToken token)
    {
        var result = new List<ClubStringEntry>();
        for (int offset = start; offset <= end - 8; offset++)
        {
            if ((offset & 0x3FFFF) == 0) token.ThrowIfCancellationRequested();
            int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (len is < MinLength or > MaxLength) continue;
            int bytes = checked(len * 2);
            int textOffset = offset + 4;
            if (textOffset + bytes > end) continue;
            ReadOnlySpan<byte> span = data.AsSpan(textOffset, bytes);
            int plausible = 0;
            bool invalid = false;
            for (int i = 0; i < span.Length; i += 2)
            {
                char c = (char)(span[i] | (span[i + 1] << 8));
                if (c == '\0' || char.IsControl(c)) { invalid = true; break; }
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || "'-.&()/".Contains(c)) plausible++;
            }
            if (invalid || plausible < Math.Max(2, len * 9 / 10)) continue;
            string text = Encoding.Unicode.GetString(span).Trim();
            if (text.Length < MinLength || text.All(char.IsDigit)) continue;
            long endOffset = textOffset + bytes;
            if (endOffset + 1 < data.Length && data[endOffset] == 0 && data[endOffset + 1] == 0) endOffset += 2;
            result.Add(new ClubStringEntry(offset, textOffset, len, text, endOffset));
            offset = Math.Max(offset, checked((int)endOffset - 1));
        }
        return result;
    }

    private static List<(ClubStringEntry Full, ClubStringEntry Short, int Score, string Validation)> FindPairs(byte[] data, List<ClubStringEntry> strings)
    {
        var result = new List<(ClubStringEntry, ClubStringEntry, int, string)>();
        for (int i = 0; i + 1 < strings.Count; i++)
        {
            var full = strings[i];
            var shortName = strings[i + 1];
            long gap = shortName.LengthOffset - full.EndOffset;
            if (gap < 0 || gap > MaxPairGap) continue;
            int score = 0;
            var reasons = new List<string>();
            if (shortName.Length <= full.Length) { score += 2; reasons.Add("SHORT_LE_FULL"); }
            if (SharesToken(full.Text, shortName.Text)) { score += 2; reasons.Add("TOKEN_COMPARTILHADO"); }
            if (gap <= 4) { score += 2; reasons.Add("GAP_CURTO"); }
            if (HasClubLikeBinaryContext(data, full.LengthOffset, shortName.EndOffset)) { score += 3; reasons.Add("CONTEXTO_BINARIO"); }
            if (KnownFoundationYears.ContainsKey(full.Text)) { score += 4; reasons.Add("ALVO_CONHECIDO"); }
            if (LooksLikeNonClubPair(full.Text, shortName.Text)) score -= 4;
            if (score < 5) continue;
            result.Add((full, shortName, score, string.Join('|', reasons)));
        }
        return result.OrderBy(x => x.Item1.LengthOffset).DistinctBy(x => x.Item1.LengthOffset).ToList();
    }

    private static bool SharesToken(string a, string b)
    {
        string[] ta = Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] tb = Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ta.Intersect(tb).Any() || Normalize(a).Contains(Normalize(b), StringComparison.Ordinal) || Normalize(b).Contains(Normalize(a), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        string d = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool LooksLikeNonClubPair(string full, string shortName)
    {
        string combined = (full + " " + shortName).ToLowerInvariant();
        string[] words = ["constipação", "síndrome", "repetição", "primeira mão", "segunda mão"];
        return words.Any(combined.Contains);
    }

    private static bool HasClubLikeBinaryContext(byte[] data, long fullLengthOffset, long shortEndOffset)
    {
        int before = checked((int)Math.Max(0, fullLengthOffset - 96));
        int beforeCount = checked((int)(fullLengthOffset - before));
        ReadOnlySpan<byte> pre = data.AsSpan(before, beforeCount);
        byte[] oneTwoThree = [1,0,0,0,2,0,0,0,3,0,0,0];
        bool hasSequence = pre.IndexOf(oneTwoThree) >= 0;
        int after = checked((int)shortEndOffset);
        int count = Math.Min(96, data.Length - after);
        bool hasConstants = count >= 16 && data.AsSpan(after, count).IndexOf(new byte[] { 0xBD, 0, 0, 0 }) >= 0;
        return hasSequence || hasConstants;
    }

    private static long FindEstimatedStart(byte[] data, long fullOffset, long floor)
    {
        int start = checked((int)Math.Max(floor, fullOffset - 192));
        byte[] signature = [1,0,0,0,2,0,0,0,3,0,0,0];
        for (int p = checked((int)fullOffset - signature.Length); p >= start; p--)
            if (data.AsSpan(p, signature.Length).SequenceEqual(signature)) return p;
        return start;
    }

    private static List<(long Offset, ushort Value)> FindYears(byte[] data, long fullOffset)
    {
        int start = checked((int)Math.Max(0, fullOffset - FoundationSearchBack));
        int end = checked((int)Math.Min(data.LongLength - 2, fullOffset + FoundationSearchForward));
        var result = new List<(long, ushort)>();
        for (int p = start; p <= end; p++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2));
            if (value is >= 1800 and <= 2005) result.Add((p, value));
        }
        return result;
    }

    private static IReadOnlyList<ClubSizeBucket> BuildSizeBuckets(IReadOnlyList<ClubSequenceRecord> clubs)
    {
        (long Min, long Max)[] ranges = [(0,255),(256,511),(512,767),(768,1023),(1024,1535),(1536,2047),(2048,4095),(4096,8191),(8192,long.MaxValue)];
        return ranges.Select(r => new ClubSizeBucket(r.Min, r.Max, clubs.Count(c => c.EstimatedSize >= r.Min && c.EstimatedSize <= r.Max))).Where(x => x.Count > 0).ToArray();
    }

    private static IReadOnlyList<ClubCommonField> BuildCommonFields(byte[] data, IReadOnlyList<ClubSequenceRecord> clubs)
    {
        var rows = new List<ClubCommonField>();
        for (int rel = -128; rel <= 256; rel += 4)
        {
            var values = new List<uint>();
            foreach (var c in clubs)
            {
                long p = c.FullLengthOffset + rel;
                if (p < 0 || p + 4 > data.LongLength) continue;
                values.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p), 4)));
            }
            if (values.Count == 0) continue;
            var common = values.GroupBy(x => x).OrderByDescending(g => g.Count()).First();
            rows.Add(new ClubCommonField(rel, values.Count, common.Key.ToString("X8"), common.Count(), common.Count() * 100.0 / values.Count, values.Min(), values.Max()));
        }
        return rows.OrderByDescending(x => x.ConstantPercentage).ThenBy(x => x.RelativeOffset).ToArray();
    }

    public static string FormatReport(ClubSequenceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — ClubSequenceScannerDiagnostic 0.0.8");
        sb.AppendLine(new string('=', 82));
        sb.AppendLine($"Arquivo: {r.SourceFile}");
        sb.AppendLine($"Tamanho: {r.SourceSize:N0} bytes");
        sb.AppendLine($"SHA-256: {r.Sha256}");
        sb.AppendLine($"Região examinada: 0x{r.RegionStart:X8}-0x{r.RegionEnd:X8}");
        sb.AppendLine($"Pares de clubes candidatos: {r.Clubs.Count:N0}");
        sb.AppendLine($"Anos confirmados: {r.Clubs.Count(x => x.Validation.Contains("ANO_CONFIRMADO", StringComparison.Ordinal))}");
        sb.AppendLine();
        sb.AppendLine("Clubes conhecidos:");
        foreach (var c in r.Clubs.Where(x => KnownFoundationYears.ContainsKey(x.FullName)))
            sb.AppendLine($"  0x{c.FullLengthOffset:X8} | {c.FullName} -> {c.ShortName} | ano={(c.FoundationYear?.ToString() ?? "-")} rel={(c.FoundationYearRelativeOffset?.ToString() ?? "-")} | {c.Confidence} | {c.Validation}");
        sb.AppendLine();
        sb.AppendLine("Distribuição dos tamanhos estimados:");
        foreach (var b in r.SizeBuckets) sb.AppendLine($"  {b.Minimum,6}-{(b.Maximum == long.MaxValue ? "+" : b.Maximum.ToString()),-6}: {b.Count,5}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados: all-club-records.csv, club-foundation-years.csv, club-record-sizes.csv, club-common-fields.csv e club-sequence-report.txt.");
        sb.AppendLine("O arquivo de origem não foi modificado.");
        return sb.ToString();
    }

    private static string FormatClubsCsv(ClubSequenceReport r)
    {
        var sb = new StringBuilder("row,estimated_start,full_length_offset,full_text_offset,full_name,short_length_offset,short_text_offset,short_name,next_record_offset,estimated_size,foundation_year,foundation_year_offset,foundation_year_relative_offset,pair_gap,confidence,validation\r\n");
        foreach (var c in r.Clubs)
            sb.AppendLine(string.Join(',', c.Row, Hex(c.EstimatedStart), Hex(c.FullLengthOffset), Hex(c.FullTextOffset), Csv(c.FullName), Hex(c.ShortLengthOffset), Hex(c.ShortTextOffset), Csv(c.ShortName), Hex(c.NextRecordOffset), c.EstimatedSize, c.FoundationYear?.ToString() ?? "", c.FoundationYearOffset.HasValue ? Hex(c.FoundationYearOffset.Value) : "", c.FoundationYearRelativeOffset?.ToString() ?? "", c.PairGap, c.Confidence, Csv(c.Validation)));
        return sb.ToString();
    }

    private static string FormatYearsCsv(ClubSequenceReport r)
    {
        var sb = new StringBuilder("club_row,club_name,absolute_offset,relative_offset,value,status\r\n");
        foreach (var y in r.YearCandidates) sb.AppendLine($"{y.ClubRow},{Csv(y.ClubName)},{Hex(y.AbsoluteOffset)},{y.RelativeOffset},{y.Value},{y.Status}");
        return sb.ToString();
    }

    private static string FormatSizesCsv(ClubSequenceReport r)
    {
        var sb = new StringBuilder("minimum,maximum,count\r\n");
        foreach (var b in r.SizeBuckets) sb.AppendLine($"{b.Minimum},{(b.Maximum == long.MaxValue ? "" : b.Maximum)},{b.Count}");
        return sb.ToString();
    }

    private static string FormatFieldsCsv(ClubSequenceReport r)
    {
        var sb = new StringBuilder("relative_offset,samples,most_common_hex,most_common_count,constant_percentage,minimum_uint32,maximum_uint32\r\n");
        foreach (var f in r.CommonFields) sb.AppendLine($"{f.RelativeOffset},{f.Samples},{f.MostCommonHex},{f.MostCommonCount},{f.ConstantPercentage.ToString("F2", CultureInfo.InvariantCulture)},{f.MinimumUInt32},{f.MaximumUInt32}");
        return sb.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hex(long value) => $"0x{value:X8}";
}
