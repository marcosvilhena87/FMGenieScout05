using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubCompetitionFieldDiagnostic
{
    private const int WindowBefore = 256;
    private const int WindowAfter = 512;

    private static readonly string[] SerieA =
    [
        "Atlético Paranaense", "Atlético Mineiro", "Botafogo", "Corinthians", "Coritiba", "Criciúma",
        "Cruzeiro", "Figueirense", "Flamengo", "Fluminense", "Goiás", "Grêmio", "Guarani",
        "Internacional", "Juventude", "Palmeiras", "Paraná", "Paysandu", "Ponte Preta", "Santos",
        "São Caetano", "São Paulo", "Vasco", "Vitória"
    ];

    private static readonly string[] SerieB =
    [
        "América (MG)", "América (RN)", "Anapolina", "Avaí", "Bahia", "Brasiliense", "CRB", "Caxias",
        "Ceará", "Fortaleza", "Ituano", "Joinville", "Londrina", "Marília", "Mogi Mirim", "Náutico",
        "Paulista", "Portuguesa", "Sport Recife", "Remo", "Santa Cruz", "Santo André",
        "São Raimundo (AM)", "Vila Nova (GO)"
    ];

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Atlético Paranaense"] = ["Clube Atlético Paranaense", "C Atlético Paranaense", "Atlético Paranaense"],
        ["Atlético Mineiro"] = ["Clube Atlético Mineiro", "C Atlético Mineiro", "Atlético Mineiro"],
        ["Botafogo"] = ["Botafogo FR", "Botafogo"],
        ["Corinthians"] = ["SC Corinthians Paulista", "Corinthians"],
        ["Flamengo"] = ["CR Flamengo", "Flamengo"],
        ["Fluminense"] = ["Fluminense FC", "Fluminense"],
        ["Grêmio"] = ["Grêmio FBPA", "Grêmio"],
        ["Internacional"] = ["SC Internacional", "Internacional"],
        ["Palmeiras"] = ["SE Palmeiras", "Palmeiras"],
        ["São Paulo"] = ["São Paulo FC", "São Paulo"],
        ["Vasco"] = ["CR Vasco da Gama", "Vasco"],
        ["Vitória"] = ["EC Vitória", "Vitória"],
        ["Bahia"] = ["EC Bahia", "Bahia"],
        ["Náutico"] = ["C Náutico Capibaribe", "Náutico"],
        ["Portuguesa"] = ["A Portuguesa D", "Portuguesa"],
        ["Sport Recife"] = ["SC Recife", "Sport Recife", "Sport"],
        ["Remo"] = ["C Remo", "Remo"],
        ["América (MG)"] = ["América FC (MG)", "América Mineiro", "América (MG)"],
        ["América (RN)"] = ["América FC (RN)", "América (RN)"],
        ["São Raimundo (AM)"] = ["São Raimundo EC (AM)", "São Raimundo (AM)"],
        ["Vila Nova (GO)"] = ["Vila Nova FC (GO)", "Vila Nova (GO)"]
    };

    public async Task<ClubCompetitionFieldReport> AnalyzeAsync(
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

        progress?.Report("Reutilizando o parser de ClubId 0.0.11...");
        var idDiagnostic = new ClubIdStructureDiagnostic();
        ClubIdStructureReport idReport = await idDiagnostic.AnalyzeAsync(source, Path.Combine(output, "club-id-source"), progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("Associando os 48 clubes conhecidos...");
        var matches = new List<DivisionClubMatch>();
        AddMatches(matches, SerieA, 1, idReport.Clubs);
        AddMatches(matches, SerieB, 2, idReport.Clubs);

        ClubIdRecord[] a = ResolveRecords(matches, 1, idReport.Clubs);
        ClubIdRecord[] b = ResolveRecords(matches, 2, idReport.Clubs);

        progress?.Report("Comparando bytes, UInt16 e UInt32 alinhados pelo bloco de ClubId...");
        var candidates = new List<CompetitionFieldCandidate>();
        for (int rel = -WindowBefore; rel <= WindowAfter; rel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCandidate<byte>(candidates, "BYTE", rel, a, b, data, 1, ReadByte, v => v.ToString(CultureInfo.InvariantCulture));
            if ((rel & 1) == 0)
                AddCandidate<ushort>(candidates, "UINT16", rel, a, b, data, 2, ReadUInt16, v => v.ToString(CultureInfo.InvariantCulture));
            if ((rel & 3) == 0)
                AddCandidate<uint>(candidates, "UINT32", rel, a, b, data, 4, ReadUInt32, v => v.ToString(CultureInfo.InvariantCulture));
        }

        CompetitionFieldCandidate[] ordered = candidates
            .Where(x => x.SerieASamples >= 4 && x.SerieBSamples >= 4)
            .OrderByDescending(x => x.SeparationScore)
            .ThenByDescending(x => Math.Min(x.SerieAModeRate, x.SerieBModeRate))
            .ThenBy(x => Math.Abs(x.RelativeOffset))
            .Take(1000)
            .ToArray();

        var report = new ClubCompetitionFieldReport(source, data.LongLength, sha, matches, ordered, output, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(output, "club-competition-field-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "division-club-matches.csv"), FormatMatchesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-field-candidates.csv"), FormatCandidatesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "competition-field-top-contexts.txt"), FormatTopContexts(report, data, idReport.Clubs), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico de campo de competição concluído.");
        return report;
    }

    private static void AddMatches(List<DivisionClubMatch> output, IEnumerable<string> names, int division, IReadOnlyList<ClubIdRecord> records)
    {
        foreach (string expected in names)
        {
            (ClubIdRecord? record, string method) = MatchClub(expected, records);
            output.Add(record is null
                ? new DivisionClubMatch(expected, division, null, null, null, null, "NAO_ENCONTRADO", method)
                : new DivisionClubMatch(expected, division, record.FullName, record.ShortName, record.ClubId, record.IdBlockOffset, "ENCONTRADO", method));
        }
    }

    private static (ClubIdRecord? Record, string Method) MatchClub(string expected, IReadOnlyList<ClubIdRecord> records)
    {
        IEnumerable<string> aliases = Aliases.TryGetValue(expected, out string[]? values) ? values.Prepend(expected) : [expected];
        foreach (string alias in aliases)
        {
            string n = Normalize(alias);
            ClubIdRecord? exact = records.FirstOrDefault(r => Normalize(r.FullName) == n || Normalize(r.ShortName) == n);
            if (exact is not null) return (exact, $"EXATO:{alias}");
        }

        string target = Normalize(expected);
        string[] tokens = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3).ToArray();
        ClubIdRecord? fuzzy = records
            .Select(r => new { Record = r, Score = tokens.Count(t => Normalize(r.FullName + " " + r.ShortName).Contains(t, StringComparison.Ordinal)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Record.FullName.Length - expected.Length))
            .Select(x => x.Record)
            .FirstOrDefault();
        return (fuzzy, fuzzy is null ? "SEM_CORRESPONDENCIA" : "TOKENS");
    }

    private static ClubIdRecord[] ResolveRecords(IReadOnlyList<DivisionClubMatch> matches, int division, IReadOnlyList<ClubIdRecord> records) =>
        matches.Where(x => x.Division == division && x.ClubId.HasValue)
            .Select(x => records.First(r => r.ClubId == x.ClubId && r.IdBlockOffset == x.IdBlockOffset))
            .DistinctBy(x => x.IdBlockOffset)
            .ToArray();

    private delegate bool TryRead<T>(byte[] data, long offset, out T value) where T : struct;

    private static void AddCandidate<T>(
        List<CompetitionFieldCandidate> output,
        string type,
        int relativeOffset,
        IReadOnlyList<ClubIdRecord> serieA,
        IReadOnlyList<ClubIdRecord> serieB,
        byte[] data,
        int width,
        TryRead<T> reader,
        Func<T, string> formatter) where T : struct, IEquatable<T>
    {
        List<T> a = ReadValues(serieA, data, relativeOffset, width, reader);
        List<T> b = ReadValues(serieB, data, relativeOffset, width, reader);
        if (a.Count == 0 || b.Count == 0) return;
        var am = Mode(a); var bm = Mode(b);
        double ar = am.Count / (double)a.Count;
        double br = bm.Count / (double)b.Count;
        bool distinct = !am.Value.Equals(bm.Value);
        double balance = Math.Min(a.Count / 24.0, b.Count / 24.0);
        double score = distinct ? ar * br * Math.Min(1.0, balance) : 0.0;
        string assessment = !distinct ? "MESMO_MODO" : score >= 0.75 ? "SEPARACAO_FORTE" : score >= 0.45 ? "SEPARACAO_MEDIA" : "SEPARACAO_FRACA";
        output.Add(new CompetitionFieldCandidate(type, relativeOffset, a.Count, formatter(am.Value), am.Count, ar,
            b.Count, formatter(bm.Value), bm.Count, br, score, assessment));
    }

    private static List<T> ReadValues<T>(IReadOnlyList<ClubIdRecord> clubs, byte[] data, int rel, int width, TryRead<T> reader) where T : struct
    {
        var values = new List<T>(clubs.Count);
        foreach (ClubIdRecord club in clubs)
        {
            long offset = club.IdBlockOffset + rel;
            if (offset < 0 || offset + width > data.LongLength) continue;
            if (reader(data, offset, out T value)) values.Add(value);
        }
        return values;
    }

    private static (T Value, int Count) Mode<T>(IEnumerable<T> values) where T : IEquatable<T> =>
        values.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.GetHashCode())
            .Select(g => (g.Key, g.Count())).First();

    private static bool ReadByte(byte[] data, long offset, out byte value) { value = data[checked((int)offset)]; return true; }
    private static bool ReadUInt16(byte[] data, long offset, out ushort value) { value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked((int)offset), 2)); return true; }
    private static bool ReadUInt32(byte[] data, long offset, out uint value) { value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)offset), 4)); return true; }

    private static string Normalize(string value)
    {
        string d = value.Normalize(NormalizationForm.FormD); var sb = new StringBuilder();
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        string normalized = sb.ToString().Normalize(NormalizationForm.FormC);
        string[] ignored = ["fc", "ec", "sc", "cr", "se", "clube", "esporte", "sport", "associacao", "atletica", "futebol"];
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => !ignored.Contains(x, StringComparer.Ordinal)));
    }

    public static string FormatReport(ClubCompetitionFieldReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — ClubCompetitionFieldDiagnostic 0.0.13");
        sb.AppendLine(new string('=', 88));
        sb.AppendLine($"Arquivo: {r.SourceFile}");
        sb.AppendLine($"Tamanho: {r.SourceSize:N0} bytes");
        sb.AppendLine($"SHA-256: {r.Sha256}");
        sb.AppendLine($"Clubes da Série A encontrados: {r.ClubMatches.Count(x => x.Division == 1 && x.Status == "ENCONTRADO")}/24");
        sb.AppendLine($"Clubes da Série B encontrados: {r.ClubMatches.Count(x => x.Division == 2 && x.Status == "ENCONTRADO")}/24");
        sb.AppendLine($"Campos candidatos avaliados e mantidos: {r.Candidates.Count:N0}");
        sb.AppendLine();
        sb.AppendLine("Melhores campos de separação Série A × Série B:");
        foreach (CompetitionFieldCandidate c in r.Candidates.Take(30))
            sb.AppendLine($"  {c.DataType,-6} rel={c.RelativeOffset,5} | A={c.SerieAMode} ({c.SerieAModeRate:P1}, n={c.SerieASamples}) | B={c.SerieBMode} ({c.SerieBModeRate:P1}, n={c.SerieBSamples}) | score={c.SeparationScore:F3} | {c.Assessment}");
        sb.AppendLine();
        sb.AppendLine("Clubes não encontrados:");
        foreach (DivisionClubMatch m in r.ClubMatches.Where(x => x.Status != "ENCONTRADO"))
            sb.AppendLine($"  Série {m.Division}: {m.ExpectedDisplayName} | {m.MatchMethod}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados:");
        sb.AppendLine("  club-competition-field-report.txt");
        sb.AppendLine("  division-club-matches.csv");
        sb.AppendLine("  competition-field-candidates.csv");
        sb.AppendLine("  competition-field-top-contexts.txt");
        sb.AppendLine("  club-id-source/ (diagnóstico 0.0.11 reutilizado)");
        sb.AppendLine("O arquivo de origem não foi modificado.");
        return sb.ToString();
    }

    private static string FormatMatchesCsv(ClubCompetitionFieldReport r)
    {
        var sb = new StringBuilder("expected_display_name,division,full_name,short_name,club_id,id_block_offset,status,match_method\r\n");
        foreach (DivisionClubMatch m in r.ClubMatches)
            sb.AppendLine($"{Csv(m.ExpectedDisplayName)},{m.Division},{Csv(m.FullName ?? "")},{Csv(m.ShortName ?? "")},{m.ClubId?.ToString() ?? ""},{(m.IdBlockOffset.HasValue ? Hex(m.IdBlockOffset.Value) : "")},{m.Status},{Csv(m.MatchMethod)}");
        return sb.ToString();
    }

    private static string FormatCandidatesCsv(ClubCompetitionFieldReport r)
    {
        var sb = new StringBuilder("data_type,relative_offset,serie_a_samples,serie_a_mode,serie_a_mode_count,serie_a_mode_rate,serie_b_samples,serie_b_mode,serie_b_mode_count,serie_b_mode_rate,separation_score,assessment\r\n");
        foreach (CompetitionFieldCandidate c in r.Candidates)
            sb.AppendLine($"{c.DataType},{c.RelativeOffset},{c.SerieASamples},{c.SerieAMode},{c.SerieAModeCount},{c.SerieAModeRate.ToString("F6", CultureInfo.InvariantCulture)},{c.SerieBSamples},{c.SerieBMode},{c.SerieBModeCount},{c.SerieBModeRate.ToString("F6", CultureInfo.InvariantCulture)},{c.SeparationScore.ToString("F6", CultureInfo.InvariantCulture)},{c.Assessment}");
        return sb.ToString();
    }

    private static string FormatTopContexts(ClubCompetitionFieldReport report, byte[] data, IReadOnlyList<ClubIdRecord> records)
    {
        var sb = new StringBuilder();
        foreach (CompetitionFieldCandidate candidate in report.Candidates.Where(x => x.Assessment != "MESMO_MODO").Take(20))
        {
            sb.AppendLine($"[{candidate.DataType} rel={candidate.RelativeOffset}] A={candidate.SerieAMode} B={candidate.SerieBMode} score={candidate.SeparationScore:F3}");
            foreach (DivisionClubMatch match in report.ClubMatches.Where(x => x.Status == "ENCONTRADO").Take(48))
            {
                ClubIdRecord? record = records.FirstOrDefault(x => x.ClubId == match.ClubId && x.IdBlockOffset == match.IdBlockOffset);
                if (record is null) continue;
                long p = record.IdBlockOffset + candidate.RelativeOffset;
                if (p < 0 || p + 8 > data.LongLength) continue;
                sb.AppendLine($"  S{match.Division} {match.ExpectedDisplayName,-24} id={match.ClubId,6} offset={Hex(p)} bytes={Convert.ToHexString(data.AsSpan(checked((int)p), 8))}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hex(long value) => $"0x{value:X8}";
}
