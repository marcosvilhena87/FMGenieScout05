using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class GlobalClubHeaderDiagnostic
{
    private const int NameSearchBefore = 1024;
    private const int MaxAcceptedNameDistance = 768;

    private static readonly (string Name, int Division, uint Id)[] KnownClubs =
    [
        ("Atlético Paranaense",1,107206),("Atlético Mineiro",1,314),("Botafogo",1,316),("Corinthians",1,319),
        ("Coritiba",1,104776),("Criciúma",1,320),("Cruzeiro",1,321),("Figueirense",1,301306),
        ("Flamengo",1,322),("Fluminense",1,323),("Goiás",1,102555),("Grêmio",1,324),
        ("Guarani",1,325),("Internacional",1,326),("Juventude",1,327),("Palmeiras",1,329),
        ("Paraná",1,330),("Paysandu",1,331),("Ponte Preta",1,332),("Santos",1,335),
        ("São Caetano",1,301354),("São Paulo",1,337),("Vasco",1,339),("Vitória (BA)",1,340),
        ("América (MG)",2,107201),("América (RN)",2,107203),("Anapolina",2,301146),("Avaí",2,107208),
        ("Bahia",2,315),("Brasiliense",2,309670),("CRB",2,301102),("Caxias",2,301266),
        ("Ceará",2,104749),("Fortaleza",2,104750),("Ituano",2,107216),("Joinville",2,301310),
        ("Londrina",2,900678),("Marília",2,311026),("Mogi Mirim",2,107222),("Náutico",2,328),
        ("Paulista",2,301338),("Portuguesa",2,333),("Sport Recife",2,338),("Remo",2,334),
        ("Santa Cruz",2,107232),("Santo André",2,301352),("São Raimundo (AM)",2,301111),("Vila Nova (GO)",2,311107)
    ];

    public async Task<GlobalClubHeaderReport> AnalyzeAsync(
        string inputFile,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(inputFile);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo game_db.payload.bin...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        progress?.Report("Procurando cabeçalhos globais de clubes...");
        List<GlobalClubHeaderRecord> accepted = [];
        List<RejectedClubHeaderRecord> rejected = [];

        for (int indexOffset = 0; indexOffset <= data.Length - 22; indexOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint recordIndex = ReadUInt32(data, indexOffset);
            uint clubId = ReadUInt32(data, indexOffset + 4);
            byte separator1 = data[indexOffset + 8];
            uint field1 = ReadUInt32(data, indexOffset + 9);
            uint field2 = ReadUInt32(data, indexOffset + 13);
            byte separator2 = data[indexOffset + 17];
            uint field3 = ReadUInt32(data, indexOffset + 18);

            if (separator1 != 0 || field2 != 255 || separator2 != 0 || field3 != 255) continue;
            if (recordIndex == 0 || recordIndex > 100_000 || clubId == 0 || clubId > 10_000_000) continue;

            NamePair? names = FindNearestNamePair(data, indexOffset);
            int score = 45;
            List<string> reasons = [];
            if (clubId > recordIndex) score += 10; else reasons.Add("CLUBID_NAO_MAIOR_QUE_INDEX");
            if (names is not null)
            {
                score += 30;
                if (names.Distance <= 256) score += 15;
                else if (names.Distance <= MaxAcceptedNameDistance) score += 8;
                if (names.FullName.Length >= 3) score += 5;
            }
            else reasons.Add("SEM_NOME_PROXIMO");

            bool known = KnownClubs.Any(x => x.Id == clubId);
            if (known) score += 30;
            string context = HexContext(data, indexOffset + 4, 112);

            if (score >= 75 && names is not null)
            {
                accepted.Add(new GlobalClubHeaderRecord(
                    recordIndex, clubId, indexOffset, separator1, field1, field2, separator2, field3,
                    names.FullName, names.ShortName, names.Offset, names.Distance, score,
                    score >= 105 ? "HIGH" : "MEDIUM", context));
            }
            else if (known || score >= 60)
            {
                rejected.Add(new RejectedClubHeaderRecord(indexOffset, recordIndex, clubId,
                    reasons.Count == 0 ? "PONTUACAO_INSUFICIENTE" : string.Join("|", reasons), score, context));
            }
        }

        progress?.Report("Removendo duplicatas e escolhendo o melhor registro por ClubId...");
        accepted = accepted
            .GroupBy(x => x.ClubId)
            .Select(g => g.OrderByDescending(x => x.Score).ThenBy(x => x.NameDistance ?? int.MaxValue).ThenBy(x => x.HeaderOffset).First())
            .OrderBy(x => x.ClubRecordIndex)
            .ThenBy(x => x.ClubId)
            .ToList();

        List<KnownClubCoverageRecord> coverage = KnownClubs.Select(k =>
        {
            GlobalClubHeaderRecord? found = accepted.FirstOrDefault(x => x.ClubId == k.Id);
            return new KnownClubCoverageRecord(k.Name, k.Division, k.Id, found is not null,
                found?.ClubRecordIndex, found?.HeaderOffset, found?.FullName,
                found is null ? "NAO_ENCONTRADO" : "CONFIRMADO");
        }).ToList();

        var report = new GlobalClubHeaderReport(source, data.LongLength, sha, accepted, rejected, coverage, output, DateTimeOffset.UtcNow);
        progress?.Report("Gravando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "global-club-header-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "all-clubs.csv"), FormatAllClubsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-header-field-distributions.csv"), FormatDistributionsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "duplicate-club-ids.csv"), FormatDuplicatesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "rejected-club-candidates.csv"), FormatRejectedCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "known-club-coverage.csv"), FormatCoverageCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico global concluído.");
        return report;
    }

    public static string FormatReport(GlobalClubHeaderReport report)
    {
        int a = report.KnownCoverage.Count(x => x.Division == 1 && x.Found);
        int b = report.KnownCoverage.Count(x => x.Division == 2 && x.Found);
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — GlobalClubHeaderDiagnostic 0.0.17");
        sb.AppendLine(new string('=', 92));
        sb.AppendLine($"Arquivo: {report.SourceFile}");
        sb.AppendLine($"Tamanho: {report.SourceSize:N0} bytes");
        sb.AppendLine($"SHA-256: {report.Sha256}");
        sb.AppendLine($"Clubes globais aceitos e únicos: {report.Clubs.Count:N0}");
        sb.AppendLine($"Candidatos rejeitados registrados: {report.Rejected.Count:N0}");
        sb.AppendLine($"Cobertura conhecida: Série A {a}/24 | Série B {b}/24");
        sb.AppendLine();
        sb.AppendLine("Estrutura exigida:");
        sb.AppendLine("  UInt32 ClubRecordIndex | UInt32 ClubId | byte 00 | UInt32 Field1 | UInt32 255 | byte 00 | UInt32 255");
        sb.AppendLine();
        sb.AppendLine("Distribuição de Field1:");
        foreach (var g in report.Clubs.GroupBy(x => x.Field1).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Take(20))
            sb.AppendLine($"  {g.Key,8} ocorrências={g.Count(),5} | {string.Join(" | ", g.Take(8).Select(x => x.FullName ?? x.ClubId.ToString(CultureInfo.InvariantCulture)))}");
        sb.AppendLine();
        sb.AppendLine("Primeiros clubes por ClubRecordIndex:");
        foreach (GlobalClubHeaderRecord club in report.Clubs.Take(60))
            sb.AppendLine($"  index={club.ClubRecordIndex,6} id={club.ClubId,8} offset=0x{club.HeaderOffset:X8} field1={club.Field1,5} | {club.FullName ?? "-"} -> {club.ShortName ?? "-"} | {club.Confidence}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados: global-club-header-report.txt, all-clubs.csv, club-header-field-distributions.csv, duplicate-club-ids.csv, rejected-club-candidates.csv e known-club-coverage.csv.");
        sb.AppendLine("O arquivo de origem não foi modificado.");
        return sb.ToString();
    }

    private static NamePair? FindNearestNamePair(byte[] data, int headerOffset)
    {
        int start = Math.Max(0, headerOffset - NameSearchBefore);
        NamePair? best = null;
        for (int offset = start; offset <= headerOffset - 6; offset++)
        {
            if (!TryReadLengthPrefixedUtf16(data, offset, out string? first, out int next)) continue;
            if (!IsPlausibleName(first)) continue;
            string? second = null;
            int end = next;
            if (TryReadLengthPrefixedUtf16(data, next, out string? possibleSecond, out int next2) && IsPlausibleName(possibleSecond))
            {
                second = possibleSecond;
                end = next2;
            }
            int distance = headerOffset - end;
            if (distance < 0 || distance > MaxAcceptedNameDistance) continue;
            var candidate = new NamePair(first!, second, offset, distance);
            if (best is null || candidate.Distance < best.Distance ||
                (candidate.Distance == best.Distance && candidate.FullName.Length > best.FullName.Length)) best = candidate;
        }
        return best;
    }

    private static bool TryReadLengthPrefixedUtf16(byte[] data, int offset, out string? value, out int nextOffset)
    {
        value = null;
        nextOffset = offset;
        if (offset < 0 || offset > data.Length - 8) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        if (length < 2 || length > 80) return false;
        int byteLength = checked(length * 2);
        int textOffset = offset + 4;
        int terminatorOffset = textOffset + byteLength;
        if (terminatorOffset + 2 > data.Length) return false;
        if (data[terminatorOffset] != 0 || data[terminatorOffset + 1] != 0) return false;
        string text = Encoding.Unicode.GetString(data, textOffset, byteLength);
        if (text.IndexOf('\0') >= 0) return false;
        value = text;
        nextOffset = terminatorOffset + 2;
        return true;
    }

    private static bool IsPlausibleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value.Length > 80) return false;
        int letters = 0;
        foreach (char c in value)
        {
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (char.IsLetter(c)) letters++;
            else if (!(char.IsDigit(c) || char.IsWhiteSpace(c) || c is '.' or '-' or '\'' or '(' or ')' or '&')) return false;
            if (category is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse) return false;
        }
        return letters >= 2 && letters * 2 >= value.Length;
    }

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static string HexContext(byte[] data, int center, int total)
    {
        int start = Math.Max(0, center - total / 2);
        int length = Math.Min(total, data.Length - start);
        return Convert.ToHexString(data.AsSpan(start, length));
    }

    private static string Csv(string? value)
    {
        string text = value ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static string FormatAllClubsCsv(GlobalClubHeaderReport report)
    {
        var sb = new StringBuilder("club_record_index,club_id,header_offset,field1,field2,field3,full_name,short_name,name_offset,name_distance,score,confidence\r\n");
        foreach (GlobalClubHeaderRecord x in report.Clubs)
            sb.AppendLine(string.Join(',', x.ClubRecordIndex, x.ClubId, $"0x{x.HeaderOffset:X8}", x.Field1, x.Field2, x.Field3, Csv(x.FullName), Csv(x.ShortName), x.NameOffset?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, x.NameDistance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, x.Score, x.Confidence));
        return sb.ToString();
    }

    private static string FormatCoverageCsv(GlobalClubHeaderReport report)
    {
        var sb = new StringBuilder("division,display_name,club_id,found,club_record_index,header_offset,parsed_name,status\r\n");
        foreach (KnownClubCoverageRecord x in report.KnownCoverage)
            sb.AppendLine(string.Join(',', x.Division, Csv(x.DisplayName), x.ClubId, x.Found, x.ClubRecordIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, x.HeaderOffset.HasValue ? $"0x{x.HeaderOffset.Value:X8}" : string.Empty, Csv(x.ParsedName), x.Status));
        return sb.ToString();
    }

    private static string FormatDistributionsCsv(GlobalClubHeaderReport report)
    {
        var sb = new StringBuilder("field,value,count,examples\r\n");
        foreach (var g in report.Clubs.GroupBy(x => x.Field1).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"Field1,{g.Key},{g.Count()},{Csv(string.Join(" | ", g.Take(12).Select(x => x.FullName ?? x.ClubId.ToString(CultureInfo.InvariantCulture))))}");
        foreach (var g in report.Clubs.GroupBy(x => x.Field2).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"Field2,{g.Key},{g.Count()},{Csv(string.Join(" | ", g.Take(12).Select(x => x.FullName ?? x.ClubId.ToString(CultureInfo.InvariantCulture))))}");
        foreach (var g in report.Clubs.GroupBy(x => x.Field3).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
            sb.AppendLine($"Field3,{g.Key},{g.Count()},{Csv(string.Join(" | ", g.Take(12).Select(x => x.FullName ?? x.ClubId.ToString(CultureInfo.InvariantCulture))))}");
        return sb.ToString();
    }

    private static string FormatRejectedCsv(GlobalClubHeaderReport report)
    {
        var sb = new StringBuilder("header_offset,club_record_index,club_id,reason,score,context_hex\r\n");
        foreach (RejectedClubHeaderRecord x in report.Rejected)
            sb.AppendLine(string.Join(',', $"0x{x.HeaderOffset:X8}", x.ClubRecordIndex, x.ClubId, x.Reason, x.Score, x.ContextHex));
        return sb.ToString();
    }

    private static string FormatDuplicatesCsv(GlobalClubHeaderReport report)
    {
        return "club_id,note\r\n" + string.Join("\r\n", report.Clubs.GroupBy(x => x.ClubId).Where(g => g.Count() > 1).Select(g => $"{g.Key},duplicado_apos_deduplicacao"));
    }

    private sealed record NamePair(string FullName, string? ShortName, int Offset, int Distance);
}
