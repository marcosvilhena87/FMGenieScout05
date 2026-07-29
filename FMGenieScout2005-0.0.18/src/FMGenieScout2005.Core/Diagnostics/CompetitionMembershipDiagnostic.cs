using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class CompetitionMembershipDiagnostic
{
    public const uint FirstDivisionId = 102423;
    public const uint SecondDivisionId = 107191;

    private static readonly Dictionary<string, uint> KnownFirstDivisionClubIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SC Corinthians Paulista"] = 319,
        ["CR Flamengo"] = 322,
        ["SE Palmeiras"] = 329,
        ["São Paulo FC"] = 337
    };

    public async Task<CompetitionMembershipReport> AnalyzeAsync(
        string gameDbFile,
        string firstDivisionFile,
        string secondDivisionFile,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string gameDbPath = ValidateFile(gameDbFile, "game_db.payload.bin");
        string firstPath = ValidateFile(firstDivisionFile, "comp_102423.dat.raw.bin");
        string secondPath = ValidateFile(secondDivisionFile, "comp_107191.dat.raw.bin");
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo os três arquivos...");
        byte[] gameDb = await File.ReadAllBytesAsync(gameDbPath, cancellationToken).ConfigureAwait(false);
        byte[] first = await File.ReadAllBytesAsync(firstPath, cancellationToken).ConfigureAwait(false);
        byte[] second = await File.ReadAllBytesAsync(secondPath, cancellationToken).ConfigureAwait(false);

        string gameDbSha = Sha256(gameDb);
        CompetitionComponentInfo firstInfo = BuildComponentInfo(FirstDivisionId, "Brasileirão - Primeira Divisão", firstPath, first);
        CompetitionComponentInfo secondInfo = BuildComponentInfo(SecondDivisionId, "Brasileirão - Segunda Divisão", secondPath, second);

        progress?.Report("Decodificando ClubIds confirmados no game_db...");
        string intermediate = Path.Combine(output, "club-id-source");
        var clubDiagnostic = new ClubIdStructureDiagnostic();
        ClubIdStructureReport clubReport = await clubDiagnostic.AnalyzeAsync(gameDbPath, intermediate, progress, cancellationToken).ConfigureAwait(false);

        List<ClubIdRecord> uniqueClubs = clubReport.Clubs
            .GroupBy(x => x.ClubId)
            .Select(g => g.OrderByDescending(x => ConfidenceRank(x.Confidence)).ThenBy(x => x.FullLengthOffset).First())
            .OrderBy(x => x.ClubId)
            .ToList();

        progress?.Report("Procurando ClubIds nos componentes das competições...");
        var occurrences = new List<CompetitionMembershipOccurrence>();
        foreach (ClubIdRecord club in uniqueClubs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddOccurrences(occurrences, FirstDivisionId, firstInfo.CompetitionName, club.FullName, club.ClubId, first);
            AddOccurrences(occurrences, SecondDivisionId, secondInfo.CompetitionName, club.FullName, club.ClubId, second);
        }

        var known = new List<CompetitionKnownClubValidation>();
        foreach ((string clubName, uint clubId) in KnownFirstDivisionClubIds)
        {
            int firstCount = CountOccurrences(occurrences, FirstDivisionId, clubId);
            int secondCount = CountOccurrences(occurrences, SecondDivisionId, clubId);
            string status = firstCount > 0 && secondCount == 0 ? "CONFIRMADO"
                : firstCount == 0 && secondCount == 0 ? "NAO_LOCALIZADO_NOS_COMPONENTES"
                : firstCount == 0 && secondCount > 0 ? "DIVERGENTE_SEGUNDA_DIVISAO"
                : "PRESENTE_EM_AMBAS";
            known.Add(new CompetitionKnownClubValidation(clubName, clubId, true, firstCount, secondCount, status));
        }

        var memberships = new List<CompetitionCandidateMembership>();
        foreach (ClubIdRecord club in uniqueClubs)
        {
            int firstCount = CountOccurrences(occurrences, FirstDivisionId, club.ClubId);
            int secondCount = CountOccurrences(occurrences, SecondDivisionId, club.ClubId);
            string status = firstCount > 0 && secondCount == 0 ? "PRIMEIRA_DIVISAO"
                : firstCount == 0 && secondCount > 0 ? "SEGUNDA_DIVISAO"
                : firstCount > 0 && secondCount > 0 ? "AMBAS"
                : "NAO_LOCALIZADO";
            string confidence = status == "NAO_LOCALIZADO" ? "NONE"
                : (firstCount + secondCount >= 2 ? "MEDIUM" : "LOW");
            memberships.Add(new CompetitionCandidateMembership(club.FullName, club.ShortName, club.ClubId, firstCount, secondCount, status, confidence));
        }

        var report = new CompetitionMembershipReport(
            gameDbPath,
            firstPath,
            secondPath,
            gameDb.LongLength,
            gameDbSha,
            new[] { firstInfo, secondInfo },
            occurrences.OrderBy(x => x.CompetitionId).ThenBy(x => x.ClubId).ThenBy(x => x.Offset).ToArray(),
            known,
            memberships.OrderBy(x => x.ClubId).ToArray(),
            output,
            DateTimeOffset.UtcNow);

        progress?.Report("Salvando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "competition-membership-report.txt"), FormatReport(report), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-components.csv"), FormatComponentsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-membership-occurrences.csv"), FormatOccurrencesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-known-validation.csv"), FormatKnownCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-candidate-memberships.csv"), FormatMembershipsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static string ValidateFile(string path, string expectedDescription)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Selecione {expectedDescription}.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"O arquivo {expectedDescription} não existe.", full);
        return full;
    }

    private static CompetitionComponentInfo BuildComponentInfo(uint id, string name, string path, byte[] data)
    {
        string utf16Token = $"comp_{id}";
        return new CompetitionComponentInfo(
            id,
            name,
            path,
            data.LongLength,
            Sha256(data),
            FindAll(data, Encoding.Unicode.GetBytes(utf16Token)).Count,
            FindAll(data, UInt32Bytes(id)).Count,
            id <= ushort.MaxValue ? FindAll(data, UInt16Bytes((ushort)id)).Count : 0);
    }

    private static void AddOccurrences(List<CompetitionMembershipOccurrence> result, uint competitionId, string competitionName, string clubName, uint clubId, byte[] data)
    {
        AddEncodingOccurrences(result, competitionId, competitionName, clubName, clubId, "UInt32_LE", UInt32Bytes(clubId), data, "CANDIDATO_FORTE_SE_ISOLADO");
        if (clubId <= ushort.MaxValue)
            AddEncodingOccurrences(result, competitionId, competitionName, clubName, clubId, "UInt16_LE", UInt16Bytes((ushort)clubId), data, "CANDIDATO_FRACO");
        AddEncodingOccurrences(result, competitionId, competitionName, clubName, clubId, "ASCII_DECIMAL", Encoding.ASCII.GetBytes(clubId.ToString(CultureInfo.InvariantCulture)), data, "TEXTO_DECIMAL");
        AddEncodingOccurrences(result, competitionId, competitionName, clubName, clubId, "UTF16_DECIMAL", Encoding.Unicode.GetBytes(clubId.ToString(CultureInfo.InvariantCulture)), data, "TEXTO_DECIMAL");
    }

    private static void AddEncodingOccurrences(List<CompetitionMembershipOccurrence> result, uint competitionId, string competitionName, string clubName, uint clubId, string encoding, byte[] pattern, byte[] data, string assessment)
    {
        foreach (long offset in FindAll(data, pattern))
        {
            result.Add(new CompetitionMembershipOccurrence(
                competitionId,
                competitionName,
                clubName,
                clubId,
                encoding,
                offset,
                HexContext(data, offset, pattern.Length, 16),
                assessment));
        }
    }

    private static IReadOnlyList<long> FindAll(byte[] data, byte[] pattern)
    {
        var offsets = new List<long>();
        if (pattern.Length == 0 || pattern.Length > data.Length) return offsets;
        ReadOnlySpan<byte> source = data;
        ReadOnlySpan<byte> needle = pattern;
        int start = 0;
        while (start <= source.Length - needle.Length)
        {
            int relative = source[start..].IndexOf(needle);
            if (relative < 0) break;
            int found = start + relative;
            offsets.Add(found);
            start = found + 1;
        }
        return offsets;
    }

    private static int CountOccurrences(IEnumerable<CompetitionMembershipOccurrence> occurrences, uint competitionId, uint clubId) =>
        occurrences.Count(x => x.CompetitionId == competitionId && x.ClubId == clubId && x.Encoding == "UInt32_LE");

    private static string HexContext(byte[] data, long offset, int patternLength, int radius)
    {
        int start = Math.Max(0, checked((int)offset) - radius);
        int end = Math.Min(data.Length, checked((int)offset) + patternLength + radius);
        return Convert.ToHexString(data.AsSpan(start, end - start));
    }

    private static byte[] UInt32Bytes(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt16Bytes(ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static int ConfidenceRank(string confidence) => confidence switch { "HIGH" => 3, "MEDIUM" => 2, "LOW" => 1, _ => 0 };
    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string FormatReport(CompetitionMembershipReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — CompetitionMembershipDiagnostic 0.0.12");
        sb.AppendLine(new string('=', 88));
        sb.AppendLine($"game_db: {r.GameDbFile}");
        sb.AppendLine($"Tamanho game_db: {r.GameDbSize:N0} bytes");
        sb.AppendLine($"SHA-256 game_db: {r.GameDbSha256}");
        sb.AppendLine();
        sb.AppendLine("Componentes:");
        foreach (CompetitionComponentInfo c in r.Components)
        {
            sb.AppendLine($"  {c.CompetitionId} | {c.CompetitionName}");
            sb.AppendLine($"    Arquivo: {c.FilePath}");
            sb.AppendLine($"    Tamanho: {c.FileSize:N0} bytes | SHA-256: {c.Sha256}");
            sb.AppendLine($"    Nome comp_<id> UTF-16LE: {c.CompetitionIdOccurrencesUtf16Name} | ID UInt32: {c.CompetitionIdOccurrencesUInt32} | ID UInt16: {c.CompetitionIdOccurrencesUInt16}");
        }
        sb.AppendLine();
        sb.AppendLine($"Ocorrências de ClubId UInt32 LE: {r.Occurrences.Count(x => x.Encoding == "UInt32_LE"):N0}");
        sb.AppendLine();
        sb.AppendLine("Validação dos quatro clubes conhecidos da Primeira Divisão:");
        foreach (CompetitionKnownClubValidation v in r.KnownValidations)
            sb.AppendLine($"  {v.ClubName,-26} id={v.ClubId,5} primeira={v.FirstDivisionOccurrences,3} segunda={v.SecondDivisionOccurrences,3} | {v.Status}");
        sb.AppendLine();
        sb.AppendLine("Conclusão automática:");
        int confirmed = r.KnownValidations.Count(x => x.Status == "CONFIRMADO");
        int absent = r.KnownValidations.Count(x => x.Status == "NAO_LOCALIZADO_NOS_COMPONENTES");
        if (confirmed == r.KnownValidations.Count)
            sb.AppendLine("  Os componentes contêm os ClubIds conhecidos com a distribuição esperada.");
        else if (absent == r.KnownValidations.Count)
            sb.AppendLine("  Nenhum dos quatro ClubIds foi encontrado como UInt32 literal. Os componentes podem guardar regras/configuração da competição, enquanto a filiação atual fica em outra estrutura ou usa referências/codificação diferente.");
        else
            sb.AppendLine("  A evidência é parcial ou divergente; consulte os CSVs de ocorrências antes de atribuir filiação.");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados:");
        sb.AppendLine("  competition-components.csv");
        sb.AppendLine("  competition-membership-occurrences.csv");
        sb.AppendLine("  competition-known-validation.csv");
        sb.AppendLine("  competition-candidate-memberships.csv");
        sb.AppendLine("  competition-membership-report.txt");
        sb.AppendLine("  club-id-source/ (diagnóstico 0.0.11 reutilizado)");
        sb.AppendLine("Os arquivos de origem não foram modificados.");
        return sb.ToString();
    }

    private static string FormatComponentsCsv(CompetitionMembershipReport r)
    {
        var sb = new StringBuilder("competition_id,competition_name,file_path,file_size,sha256,utf16_component_name_occurrences,uint32_id_occurrences,uint16_id_occurrences\r\n");
        foreach (CompetitionComponentInfo c in r.Components)
            sb.AppendLine($"{c.CompetitionId},{Csv(c.CompetitionName)},{Csv(c.FilePath)},{c.FileSize},{c.Sha256},{c.CompetitionIdOccurrencesUtf16Name},{c.CompetitionIdOccurrencesUInt32},{c.CompetitionIdOccurrencesUInt16}");
        return sb.ToString();
    }

    private static string FormatOccurrencesCsv(CompetitionMembershipReport r)
    {
        var sb = new StringBuilder("competition_id,competition_name,club_name,club_id,encoding,offset,context_hex,assessment\r\n");
        foreach (CompetitionMembershipOccurrence o in r.Occurrences)
            sb.AppendLine($"{o.CompetitionId},{Csv(o.CompetitionName)},{Csv(o.ClubName)},{o.ClubId},{o.Encoding},{Hex(o.Offset)},{o.ContextHex},{o.Assessment}");
        return sb.ToString();
    }

    private static string FormatKnownCsv(CompetitionMembershipReport r)
    {
        var sb = new StringBuilder("club_name,club_id,expected_first_division,first_division_uint32_occurrences,second_division_uint32_occurrences,status\r\n");
        foreach (CompetitionKnownClubValidation v in r.KnownValidations)
            sb.AppendLine($"{Csv(v.ClubName)},{v.ClubId},{v.ExpectedInFirstDivision},{v.FirstDivisionOccurrences},{v.SecondDivisionOccurrences},{v.Status}");
        return sb.ToString();
    }

    private static string FormatMembershipsCsv(CompetitionMembershipReport r)
    {
        var sb = new StringBuilder("club_name,short_name,club_id,first_division_uint32_occurrences,second_division_uint32_occurrences,membership_status,confidence\r\n");
        foreach (CompetitionCandidateMembership m in r.CandidateMemberships)
            sb.AppendLine($"{Csv(m.ClubName)},{Csv(m.ShortName)},{m.ClubId},{m.FirstDivisionOccurrences},{m.SecondDivisionOccurrences},{m.MembershipStatus},{m.Confidence}");
        return sb.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hex(long value) => $"0x{value:X8}";
}
