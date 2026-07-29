using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubRecordBoundaryDiagnostic
{
    private const int MinLength = 2;
    private const int MaxLength = 100;
    private const int MaxPairGap = 24;
    private const int BackwardWindow = 512;
    private const int ForwardWindow = 512;

    private static readonly string[] Targets = [
        "CR Flamengo", "SC Corinthians Paulista", "SE Palmeiras", "São Paulo FC",
        "Boca Juniors", "Banfield", "Independiente", "Newell's Old Boys"
    ];

    public async Task<ClubRecordBoundaryReport> AnalyzeAsync(string inputFile, string outputDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(inputFile);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo payload do game_db...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        progress?.Report("Indexando strings prefixadas...");
        List<ClubStringEntry> strings = ScanStrings(data, cancellationToken);

        progress?.Report("Localizando pares de clubes...");
        List<(ClubStringEntry Full, ClubStringEntry Short)> pairs = FindTargetPairs(strings);
        var clubs = new List<ClubRecordCandidate>();
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            long previousBoundary = i == 0 ? Math.Max(0, pair.Full.LengthOffset - BackwardWindow) : pairs[i - 1].Short.EndOffset;
            long nextBoundary = i + 1 < pairs.Count ? pairs[i + 1].Full.LengthOffset : Math.Min(data.LongLength, pair.Short.EndOffset + ForwardWindow);
            long? signature = FindSignatureBackward(data, checked((int)pair.Full.LengthOffset));
            long estimatedStart = signature ?? FindLikelyStart(data, checked((int)pair.Full.LengthOffset), checked((int)previousBoundary));
            long estimatedEnd = nextBoundary;
            string confidence = signature.HasValue ? "HIGH" : (pair.Short.LengthOffset - pair.Full.EndOffset <= 8 ? "MEDIUM" : "LOW");
            clubs.Add(new ClubRecordCandidate(i + 1, pair.Full.Text, pair.Short.Text, pair.Full.LengthOffset, pair.Short.LengthOffset,
                estimatedStart, estimatedEnd, estimatedEnd - estimatedStart, signature, signature.HasValue ? checked((int)(pair.Full.LengthOffset - signature.Value)) : -1,
                confidence, Hex(data, checked((int)Math.Max(0, pair.Full.LengthOffset - 160)), 160), Hex(data, checked((int)pair.Short.EndOffset), 256)));
        }

        progress?.Report("Comparando assinaturas e campos relativos...");
        IReadOnlyList<ClubSignatureCandidate> signatures = BuildSignatures(data, clubs);
        IReadOnlyList<ClubFieldValue> fields = BuildFieldMatrix(data, clubs);
        var report = new ClubRecordBoundaryReport(source, data.LongLength, sha, clubs, signatures, fields, output, DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(Path.Combine(output, "club-records.csv"), FormatClubsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-field-matrix.csv"), FormatFieldsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-signature-candidates.csv"), FormatSignaturesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-contexts.txt"), FormatContexts(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-boundary-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico concluído.");
        return report;
    }

    private static List<ClubStringEntry> ScanStrings(byte[] data, CancellationToken token)
    {
        var result = new List<ClubStringEntry>();
        for (int offset = 0; offset <= data.Length - 8; offset++)
        {
            if ((offset & 0xFFFFF) == 0) token.ThrowIfCancellationRequested();
            int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            if (len is < MinLength or > MaxLength) continue;
            int bytes = len * 2;
            int textOffset = offset + 4;
            if (textOffset + bytes > data.Length) continue;
            ReadOnlySpan<byte> span = data.AsSpan(textOffset, bytes);
            int printable = 0;
            bool bad = false;
            for (int i = 0; i < span.Length; i += 2)
            {
                char c = (char)(span[i] | (span[i + 1] << 8));
                if (char.IsControl(c) && c is not '\'' and not '-') { bad = true; break; }
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || "'-.&()/".Contains(c)) printable++;
            }
            if (bad || printable < Math.Max(2, len * 8 / 10)) continue;
            string text = Encoding.Unicode.GetString(span).Trim();
            if (text.Length < MinLength) continue;
            long end = textOffset + bytes;
            if (end + 1 < data.Length && data[end] == 0 && data[end + 1] == 0) end += 2;
            result.Add(new ClubStringEntry(offset, textOffset, len, text, end));
            offset = Math.Max(offset, checked((int)end - 1));
        }
        return result;
    }

    private static List<(ClubStringEntry Full, ClubStringEntry Short)> FindTargetPairs(List<ClubStringEntry> strings)
    {
        var pairs = new List<(ClubStringEntry, ClubStringEntry)>();
        foreach (string target in Targets)
        {
            foreach (ClubStringEntry full in strings.Where(x => string.Equals(x.Text, target, StringComparison.OrdinalIgnoreCase)))
            {
                ClubStringEntry? next = strings.FirstOrDefault(x => x.LengthOffset > full.LengthOffset && x.LengthOffset - full.EndOffset <= MaxPairGap);
                if (next is null) continue;
                pairs.Add((full, next));
            }
        }
        return pairs.DistinctBy(x => x.Item1.LengthOffset).OrderBy(x => x.Item1.LengthOffset).ToList();
    }

    private static long? FindSignatureBackward(byte[] data, int nameOffset)
    {
        byte[] signature = [1,0,0,0,2,0,0,0,3,0,0,0];
        int start = Math.Max(0, nameOffset - BackwardWindow);
        for (int p = nameOffset - signature.Length; p >= start; p--)
            if (data.AsSpan(p, signature.Length).SequenceEqual(signature)) return p;
        return null;
    }

    private static long FindLikelyStart(byte[] data, int nameOffset, int floor)
    {
        for (int p = nameOffset - 4; p >= floor; p -= 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
            if (v == 0 && p + 8 < nameOffset)
            {
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4, 4));
                if (next is > 0 and < 1000000) return p;
            }
        }
        return floor;
    }

    private static IReadOnlyList<ClubSignatureCandidate> BuildSignatures(byte[] data, IReadOnlyList<ClubRecordCandidate> clubs)
    {
        var rows = new List<(string Hex, string Club)>();
        foreach (var club in clubs)
        {
            int start = checked((int)Math.Max(0, club.FullLengthOffset - 64));
            for (int len = 4; len <= 16; len += 4)
            {
                int p = checked((int)club.FullLengthOffset - len);
                if (p < 0) continue;
                rows.Add((Convert.ToHexString(data.AsSpan(p, len)), club.FullName));
            }
        }
        return rows.GroupBy(x => x.Hex)
            .Where(g => g.Select(x => x.Club).Distinct().Count() >= 2)
            .Select(g => new ClubSignatureCandidate(g.Key, g.Key.Length / 2, g.Count(), string.Join(" | ", g.Select(x => x.Club).Distinct())))
            .OrderByDescending(x => x.Occurrences).ThenByDescending(x => x.Length).ToArray();
    }

    private static IReadOnlyList<ClubFieldValue> BuildFieldMatrix(byte[] data, IReadOnlyList<ClubRecordCandidate> clubs)
    {
        var result = new List<ClubFieldValue>();
        foreach (var club in clubs)
        {
            for (int rel = -128; rel <= 256; rel += 4)
            {
                long absolute = club.FullLengthOffset + rel;
                if (absolute < 0 || absolute + 4 > data.LongLength) continue;
                ReadOnlySpan<byte> s = data.AsSpan(checked((int)absolute), 4);
                result.Add(new ClubFieldValue(club.Row, club.FullName, rel, BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[..2]), BinaryPrimitives.ReadUInt16LittleEndian(s[2..])));
            }
        }
        return result;
    }

    public static string FormatReport(ClubRecordBoundaryReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — ClubRecordBoundaryDiagnostic 0.0.7");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine($"Arquivo: {r.SourceFile}"); sb.AppendLine($"Tamanho: {r.SourceSize:N0} bytes"); sb.AppendLine($"SHA-256: {r.Sha256}");
        sb.AppendLine($"Clubes dirigidos encontrados: {r.Clubs.Count}"); sb.AppendLine($"Assinaturas repetidas: {r.Signatures.Count}");
        sb.AppendLine(); sb.AppendLine("Clubes:");
        foreach (var c in r.Clubs) sb.AppendLine($"  {c.Row,2} | 0x{c.FullLengthOffset:X8} | {c.FullName} -> {c.ShortName} | início=0x{c.EstimatedStart:X8} tamanho={c.EstimatedSize:N0} | {c.Confidence}");
        sb.AppendLine(); sb.AppendLine("Arquivos gerados: club-records.csv, club-field-matrix.csv, club-signature-candidates.csv, club-contexts.txt e club-boundary-report.txt.");
        sb.AppendLine("O arquivo de origem não foi modificado."); return sb.ToString();
    }

    private static string FormatClubsCsv(ClubRecordBoundaryReport r) { var sb = new StringBuilder("row,full_name,short_name,full_length_offset,short_length_offset,estimated_start,estimated_end,estimated_size,signature_offset,signature_distance,confidence\r\n"); foreach (var c in r.Clubs) sb.AppendLine(string.Join(',', c.Row, Csv(c.FullName), Csv(c.ShortName), Hex(c.FullLengthOffset), Hex(c.ShortLengthOffset), Hex(c.EstimatedStart), Hex(c.EstimatedEnd), c.EstimatedSize, c.SignatureOffset.HasValue ? Hex(c.SignatureOffset.Value) : "", c.SignatureDistance, c.Confidence)); return sb.ToString(); }
    private static string FormatFieldsCsv(ClubRecordBoundaryReport r) { var sb = new StringBuilder("club_row,club_name,relative_offset,uint32,int32,uint16_a,uint16_b\r\n"); foreach (var x in r.Fields) sb.AppendLine($"{x.ClubRow},{Csv(x.ClubName)},{x.RelativeOffset},{x.UInt32},{x.Int32},{x.UInt16A},{x.UInt16B}"); return sb.ToString(); }
    private static string FormatSignaturesCsv(ClubRecordBoundaryReport r) { var sb = new StringBuilder("signature_hex,length,occurrences,clubs\r\n"); foreach (var x in r.Signatures) sb.AppendLine($"{x.SignatureHex},{x.Length},{x.Occurrences},{Csv(x.ClubExamples)}"); return sb.ToString(); }
    private static string FormatContexts(ClubRecordBoundaryReport r) { var sb = new StringBuilder(); foreach (var c in r.Clubs) { sb.AppendLine($"[{c.Row:00}] {c.FullName} -> {c.ShortName}"); sb.AppendLine($"Antes: {c.BeforeHex}"); sb.AppendLine($"Depois: {c.AfterHex}"); sb.AppendLine(); } return sb.ToString(); }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hex(long value) => $"0x{value:X8}";
    private static string Hex(byte[] data, int offset, int count) { if (offset < 0 || offset >= data.Length) return ""; count = Math.Min(count, data.Length - offset); return Convert.ToHexString(data.AsSpan(offset, count)); }
}
