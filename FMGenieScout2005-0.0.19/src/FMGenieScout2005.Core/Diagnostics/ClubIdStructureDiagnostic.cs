using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubIdStructureDiagnostic
{
    private const int MinLength = 2;
    private const int MaxLength = 100;
    private const int MaxPairGap = 12;
    private const int MaxSearchAfterShortName = 256;
    private const long RegionStartDefault = 0x0008D000;
    private const long RegionEndDefault = 0x00100000;

    private static readonly Dictionary<string, uint> KnownIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SC Corinthians Paulista"] = 319,
        ["CR Flamengo"] = 322,
        ["SE Palmeiras"] = 329,
        ["São Paulo FC"] = 337
    };

    public async Task<ClubIdStructureReport> AnalyzeAsync(string inputFile, string outputDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(inputFile);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo payload do game_db...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        int regionStart = checked((int)Math.Min(RegionStartDefault, data.LongLength));
        int regionEnd = checked((int)Math.Min(RegionEndDefault, data.LongLength));

        progress?.Report("Indexando pares de nomes de clubes...");
        List<ClubStringEntry> strings = ScanStrings(data, regionStart, regionEnd, cancellationToken);
        var raw = new List<ClubIdRecord>();

        progress?.Report("Procurando padrão LocalIndex/ClubId/00/189/255...");
        for (int i = 0; i + 1 < strings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClubStringEntry full = strings[i];
            ClubStringEntry shortName = strings[i + 1];
            long pairGap = shortName.LengthOffset - full.EndOffset;
            if (pairGap < 0 || pairGap > MaxPairGap) continue;
            if (shortName.Length > full.Length || !SharesToken(full.Text, shortName.Text)) continue;
            if (LooksLikeNonClubPair(full.Text, shortName.Text)) continue;

            long rawTextEnd = shortName.TextOffset + shortName.Length * 2L;
            bool hasTerminator = rawTextEnd + 1 < data.LongLength
                && data[checked((int)rawTextEnd)] == 0
                && data[checked((int)rawTextEnd + 1)] == 0;
            if (!hasTerminator) continue;
            long shortEnd = rawTextEnd + 2;

            IdCandidate? candidate = FindIdCandidate(data, shortEnd, full.Text);
            if (candidate is null) continue;

            int score = 0;
            var reasons = new List<string>();
            if (pairGap <= 4) { score += 2; reasons.Add("GAP_CURTO"); }
            if (candidate.Separator == 0) { score += 4; reasons.Add("SEPARADOR_00"); }
            if (candidate.FollowingValue == 189) { score += 6; reasons.Add("CONSTANTE_189"); }
            if (candidate.Constant255 == 255) { score += 5; reasons.Add("CONSTANTE_255"); }
            if (candidate.Delta == 271) { score += 7; reasons.Add("DELTA_271"); }
            else if (candidate.Delta is >= 1 and <= 4096) { score += 1; reasons.Add("DELTA_PLAUSIVEL"); }
            if (candidate.RelativeOffset is >= 64 and <= 160) { score += 2; reasons.Add("DISTANCIA_COMPATIVEL"); }

            if (KnownIds.TryGetValue(full.Text, out uint expected))
            {
                if (candidate.ClubId == expected) { score += 20; reasons.Add("ID_CONHECIDO_CONFIRMADO"); }
                else { score -= 20; reasons.Add($"ID_ESPERADO_{expected}_DIVERGENTE"); }
            }

            if (score < 15) continue;
            string confidence = score >= 35 ? "HIGH" : score >= 24 ? "MEDIUM" : "LOW";
            raw.Add(new ClubIdRecord(0, full.LengthOffset, full.Text, shortName.LengthOffset, shortName.Text,
                candidate.Offset, candidate.RelativeOffset, candidate.LocalIndex, candidate.ClubId, candidate.Delta,
                candidate.Separator, candidate.FollowingValue, candidate.Constant255, 0, 0, confidence, string.Join('|', reasons)));
        }

        List<ClubIdRecord> ordered = raw.OrderBy(x => x.FullLengthOffset).DistinctBy(x => x.FullLengthOffset).ToList();
        var clubs = new List<ClubIdRecord>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            long next = i + 1 < ordered.Count ? ordered[i + 1].FullLengthOffset : regionEnd;
            clubs.Add(ordered[i] with { Row = i + 1, NextRecordOffset = next, EstimatedSize = Math.Max(0, next - ordered[i].FullLengthOffset) });
        }

        var validations = KnownIds.Select(kv =>
        {
            ClubIdRecord? found = clubs.FirstOrDefault(x => x.FullName.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            string status = found is null ? "NAO_ENCONTRADO" : found.ClubId == kv.Value ? "CONFIRMADO" : "DIVERGENTE";
            return new ClubIdKnownValidation(kv.Key, kv.Value, found?.ClubId, found?.IdBlockOffset, status);
        }).ToArray();

        var deltas = clubs.GroupBy(x => x.Delta).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new ClubIdDeltaStat(g.Key, g.Count(), string.Join(" | ", g.Take(8).Select(x => x.FullName)))).ToArray();
        var commonFields = BuildCommonFields(data, clubs);
        var report = new ClubIdStructureReport(source, data.LongLength, sha, regionStart, regionEnd, clubs, validations, deltas, commonFields, output, DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(Path.Combine(output, "club-id-pattern-records.csv"), FormatRecordsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-id-known-validation.csv"), FormatKnownCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-id-deltas.csv"), FormatDeltasCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-id-common-fields.csv"), FormatFieldsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-id-pattern-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico do padrão de ClubId concluído.");
        return report;
    }

    private sealed record IdCandidate(long Offset, int RelativeOffset, uint LocalIndex, uint ClubId, int Delta, byte Separator, uint FollowingValue, uint Constant255);

    private static IdCandidate? FindIdCandidate(byte[] data, long shortEndOffset, string fullName)
    {
        uint? expected = KnownIds.TryGetValue(fullName, out uint known) ? known : null;
        IdCandidate? best = null;
        int bestScore = int.MinValue;

        for (int rel = 0; rel <= MaxSearchAfterShortName; rel++)
        {
            long p = shortEndOffset + rel;
            // 4 local + 4 id + 1 separador + 4 constante 189 + 4 constante 255
            if (p < 0 || p + 17 > data.LongLength) break;

            uint local = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p), 4));
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p + 4), 4));
            byte separator = data[checked((int)p + 8)];
            uint following = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p + 9), 4));
            uint constant255 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p + 13), 4));

            if (separator != 0 || following != 189 || constant255 != 255) continue;
            if (id is 0 or > 1_000_000 || local > 1_000_000 || id <= local) continue;
            long deltaLong = (long)id - local;
            if (deltaLong > int.MaxValue) continue;
            int delta = (int)deltaLong;

            int score = 20;
            if (delta == 271) score += 12;
            else if (delta is >= 1 and <= 4096) score += 2;
            if (rel is >= 64 and <= 160) score += 3;
            if (expected.HasValue) score += id == expected.Value ? 40 : -40;
            if (score > bestScore)
            {
                bestScore = score;
                best = new IdCandidate(p, rel, local, id, delta, separator, following, constant255);
            }
        }
        return bestScore >= 20 ? best : null;
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
            if (endOffset + 1 < data.LongLength && data[endOffset] == 0 && data[endOffset + 1] == 0) endOffset += 2;
            result.Add(new ClubStringEntry(offset, textOffset, len, text, endOffset));
            offset = Math.Max(offset, checked((int)endOffset - 1));
        }
        return result;
    }

    private static bool SharesToken(string a, string b)
    {
        string na = Normalize(a); string nb = Normalize(b);
        string[] ta = na.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] tb = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ta.Intersect(tb).Any() || na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        string d = value.Normalize(NormalizationForm.FormD); var sb = new StringBuilder();
        foreach (char c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool LooksLikeNonClubPair(string full, string shortName)
    {
        string combined = (full + " " + shortName).ToLowerInvariant();
        string[] words = ["constipação", "síndrome", "repetição", "primeira mão", "segunda mão"];
        return words.Any(combined.Contains);
    }

    private static IReadOnlyList<ClubIdCommonField> BuildCommonFields(byte[] data, IReadOnlyList<ClubIdRecord> clubs)
    {
        var rows = new List<ClubIdCommonField>();
        for (int rel = -64; rel <= 128; rel += 4)
        {
            var values = new List<uint>();
            foreach (ClubIdRecord c in clubs)
            {
                long p = c.IdBlockOffset + rel;
                if (p < 0 || p + 4 > data.LongLength) continue;
                values.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)p), 4)));
            }
            if (values.Count == 0) continue;
            IGrouping<uint, uint> common = values.GroupBy(x => x).OrderByDescending(g => g.Count()).First();
            rows.Add(new ClubIdCommonField(rel, values.Count, common.Key.ToString("X8"), common.Count(), common.Count() * 100.0 / values.Count, values.Min(), values.Max()));
        }
        return rows.OrderByDescending(x => x.ConstantPercentage).ThenBy(x => x.RelativeToIdBlock).ToArray();
    }

    public static string FormatReport(ClubIdStructureReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — ClubIdPatternDiagnostic 0.0.11");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine($"Arquivo: {r.SourceFile}");
        sb.AppendLine($"Tamanho: {r.SourceSize:N0} bytes");
        sb.AppendLine($"SHA-256: {r.Sha256}");
        sb.AppendLine($"Região examinada: 0x{r.RegionStart:X8}-0x{r.RegionEnd:X8}");
        sb.AppendLine($"Clubes aceitos pelo padrão completo: {r.Clubs.Count:N0}");
        sb.AppendLine($"IDs conhecidos confirmados: {r.KnownValidations.Count(x => x.Status == "CONFIRMADO")}/{r.KnownValidations.Count}");
        sb.AppendLine();
        sb.AppendLine("Padrão exigido:");
        sb.AppendLine("  [UInt32 LocalIndex] [UInt32 ClubId] [byte 00] [UInt32 189] [UInt32 255]");
        sb.AppendLine($"  Janela pesquisada após o nome curto: 0-{MaxSearchAfterShortName} bytes");
        sb.AppendLine();
        sb.AppendLine("Validação dos IDs conhecidos:");
        foreach (ClubIdKnownValidation v in r.KnownValidations)
            sb.AppendLine($"  {v.ClubName,-26} esperado={v.ExpectedId,5} encontrado={(v.FoundId?.ToString() ?? "-"),5} offset={(v.Offset.HasValue ? $"0x{v.Offset:X8}" : "-")} | {v.Status}");
        sb.AppendLine();
        sb.AppendLine("Clubes conhecidos decodificados:");
        foreach (ClubIdRecord c in r.Clubs.Where(x => KnownIds.ContainsKey(x.FullName)))
            sb.AppendLine($"  {c.FullName} -> {c.ShortName} | local={c.LocalIndex} id={c.ClubId} delta={c.Delta} sep={c.Separator} c189={c.FollowingValue} c255={c.Constant255} rel={c.IdRelativeToShortEnd} | {c.Confidence}");
        sb.AppendLine();
        sb.AppendLine("Deltas mais frequentes (ClubId - LocalIndex):");
        foreach (ClubIdDeltaStat d in r.DeltaStatistics.Take(20)) sb.AppendLine($"  delta={d.Delta,6} ocorrências={d.Count,5} | {d.Examples}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados: club-id-pattern-records.csv, club-id-known-validation.csv, club-id-deltas.csv, club-id-common-fields.csv e club-id-pattern-report.txt.");
        sb.AppendLine("O arquivo de origem não foi modificado.");
        return sb.ToString();
    }

    private static string FormatRecordsCsv(ClubIdStructureReport r)
    {
        var sb = new StringBuilder("row,full_length_offset,full_name,short_length_offset,short_name,id_block_offset,id_relative_to_short_end,local_index,club_id,delta,separator,constant_189,constant_255,next_record_offset,estimated_size,confidence,validation\r\n");
        foreach (ClubIdRecord c in r.Clubs)
            sb.AppendLine(string.Join(',', c.Row, Hex(c.FullLengthOffset), Csv(c.FullName), Hex(c.ShortLengthOffset), Csv(c.ShortName), Hex(c.IdBlockOffset), c.IdRelativeToShortEnd, c.LocalIndex, c.ClubId, c.Delta, c.Separator, c.FollowingValue, c.Constant255, Hex(c.NextRecordOffset), c.EstimatedSize, c.Confidence, Csv(c.Validation)));
        return sb.ToString();
    }

    private static string FormatKnownCsv(ClubIdStructureReport r)
    {
        var sb = new StringBuilder("club_name,expected_id,found_id,offset,status\r\n");
        foreach (ClubIdKnownValidation v in r.KnownValidations) sb.AppendLine($"{Csv(v.ClubName)},{v.ExpectedId},{v.FoundId?.ToString() ?? ""},{(v.Offset.HasValue ? Hex(v.Offset.Value) : "")},{v.Status}");
        return sb.ToString();
    }

    private static string FormatDeltasCsv(ClubIdStructureReport r)
    {
        var sb = new StringBuilder("delta,count,examples\r\n");
        foreach (ClubIdDeltaStat d in r.DeltaStatistics) sb.AppendLine($"{d.Delta},{d.Count},{Csv(d.Examples)}");
        return sb.ToString();
    }

    private static string FormatFieldsCsv(ClubIdStructureReport r)
    {
        var sb = new StringBuilder("relative_to_id_block,samples,most_common_hex,most_common_count,constant_percentage,minimum,maximum\r\n");
        foreach (ClubIdCommonField f in r.CommonFields) sb.AppendLine($"{f.RelativeToIdBlock},{f.Samples},{f.MostCommonHex},{f.MostCommonCount},{f.ConstantPercentage.ToString("F2", CultureInfo.InvariantCulture)},{f.Minimum},{f.Maximum}");
        return sb.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hex(long value) => $"0x{value:X8}";
}
