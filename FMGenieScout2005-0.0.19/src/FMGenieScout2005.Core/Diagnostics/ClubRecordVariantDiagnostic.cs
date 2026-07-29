using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubRecordVariantDiagnostic
{
    private const int NameSearchBefore = 768;
    private const int NameSearchAfter = 96;

    private static readonly (string Name, int Division, uint Id, string[] Terms)[] Clubs =
    [
        ("Atlético Paranaense",1,107206,["C Atlético Paranaense","Atlético Paranaense","Atlético-PR"]),
        ("Atlético Mineiro",1,314,["C Atlético Mineiro","Atlético Mineiro"]),
        ("Botafogo",1,316,["Botafogo FR","Botafogo"]),
        ("Corinthians",1,319,["SC Corinthians Paulista","Corinthians"]),
        ("Coritiba",1,104776,["Coritiba FC","Coritiba"]),
        ("Criciúma",1,320,["Criciúma EC","Criciúma"]),
        ("Cruzeiro",1,321,["Cruzeiro EC","Cruzeiro"]),
        ("Figueirense",1,301306,["Figueirense FC","Figueirense"]),
        ("Flamengo",1,322,["CR Flamengo","Flamengo"]),
        ("Fluminense",1,323,["Fluminense FC","Fluminense"]),
        ("Goiás",1,102555,["Goiás EC","Goiás","Goias"]),
        ("Grêmio",1,324,["Grêmio FBPA","Grêmio"]),
        ("Guarani",1,325,["Guarani FC","Guarani"]),
        ("Internacional",1,326,["SC Internacional","Internacional"]),
        ("Juventude",1,327,["EC Juventude","Juventude"]),
        ("Palmeiras",1,329,["SE Palmeiras","Palmeiras"]),
        ("Paraná",1,330,["Paraná C","Paraná"]),
        ("Paysandu",1,331,["Paysandu SC","Paysandu"]),
        ("Ponte Preta",1,332,["AA Ponte Preta","Ponte Preta"]),
        ("Santos",1,335,["Santos FC","Santos"]),
        ("São Caetano",1,301354,["AD São Caetano","São Caetano"]),
        ("São Paulo",1,337,["São Paulo FC","São Paulo"]),
        ("Vasco",1,339,["CR Vasco da Gama","Vasco"]),
        ("Vitória",1,313273,["EC Vitória","Vitória"]),
        ("América (MG)",2,107201,["América FC (MG)","América (MG)","América MG"]),
        ("América (RN)",2,107203,["América FC (RN)","América (RN)","América RN"]),
        ("Anapolina",2,301146,["AA Anapolina","Anapolina"]),
        ("Avaí",2,107208,["Avaí FC","Avaí"]),
        ("Bahia",2,315,["EC Bahia","Bahia"]),
        ("Brasiliense",2,309670,["Brasiliense FC","Brasiliense"]),
        ("CRB",2,301102,["CRB","Clube de Regatas Brasil","Regatas Brasil"]),
        ("Caxias",2,301266,["SER Caxias do Sul","Caxias"]),
        ("Ceará",2,104749,["Ceará SC","Ceará"]),
        ("Fortaleza",2,104750,["Fortaleza EC","Fortaleza"]),
        ("Ituano",2,107216,["Ituano FC","Ituano"]),
        ("Joinville",2,301310,["Joinville EC","Joinville"]),
        ("Londrina",2,900678,["Londrina EC","Londrina"]),
        ("Marília",2,311026,["Marília AC","Marília"]),
        ("Mogi Mirim",2,107222,["Mogi Mirim EC","Mogi Mirim"]),
        ("Náutico",2,328,["C Náutico Capibaribe","Náutico"]),
        ("Paulista",2,301338,["Paulista FC","Paulista"]),
        ("Portuguesa",2,333,["A Portuguesa D","Portuguesa"]),
        ("Sport Recife",2,338,["SC Recife","Sport Recife","Sport"]),
        ("Remo",2,334,["C Remo","Remo"]),
        ("Santa Cruz",2,107232,["Santa Cruz FC","Santa Cruz"]),
        ("Santo André",2,301352,["EC Santo André","Santo André"]),
        ("São Raimundo (AM)",2,301111,["São Raimundo EC (AM)","São Raimundo (AM)","São Raimundo"]),
        ("Vila Nova (GO)",2,311107,["Vila Nova FC (GO)","Vila Nova (GO)","Vila Nova"])
    ];

    public async Task<ClubRecordVariantReport> AnalyzeAsync(
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

        progress?.Report("Indexando os 48 ClubIds conhecidos em uma única passagem...");
        Dictionary<uint, List<int>> occurrenceMap = IndexKnownClubIds(data);
        List<ClubVariantRecord> records = [];
        foreach ((string name, int division, uint id, string[] terms) in Clubs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            occurrenceMap.TryGetValue(id, out List<int>? occurrences);
            records.Add(FindClub(data, name, division, id, terms, occurrences ?? []));
        }

        List<VariantSignatureSummary> signatures = records
            .Where(x => x.Status == "CONFIRMADO")
            .GroupBy(x => x.Variant)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key)
            .Select(g => new VariantSignatureSummary(
                g.Key,
                g.Count(),
                g.Count(x => x.Division == 1),
                g.Count(x => x.Division == 2),
                string.Join(" | ", g.Take(10).Select(x => x.DisplayName))))
            .ToList();

        var report = new ClubRecordVariantReport(
            source, data.LongLength, sha, records, signatures, output, DateTimeOffset.UtcNow);

        progress?.Report("Gravando relatórios...");
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-variant-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-variants.csv"), FormatVariantsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "known-club-validation.csv"), FormatValidationCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "variant-signatures.csv"), FormatSignaturesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-contexts.txt"), FormatContexts(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Diagnóstico de variantes concluído.");
        return report;
    }

    private static Dictionary<uint, List<int>> IndexKnownClubIds(byte[] data)
    {
        HashSet<uint> targets = Clubs.Select(x => x.Id).ToHashSet();
        var result = targets.ToDictionary(x => x, _ => new List<int>());
        for (int offset = 0; offset <= data.Length - 4; offset++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (targets.Contains(value)) result[value].Add(offset);
        }
        return result;
    }

    private static ClubVariantRecord FindClub(byte[] data, string displayName, int division, uint expectedId, string[] terms, IReadOnlyList<int> occurrences)
    {
        if (occurrences.Count == 0)
            return Missing(displayName, division, expectedId, "ID_NAO_LOCALIZADO");

        Candidate? best = null;
        foreach (int idOffset in occurrences)
        {
            Candidate candidate = EvaluateCandidate(data, idOffset, expectedId, terms);
            if (best is null || candidate.Score > best.Score ||
                (candidate.Score == best.Score && candidate.NameDistance < best.NameDistance))
                best = candidate;
        }

        if (best is null)
            return Missing(displayName, division, expectedId, "SEM_CANDIDATO");

        uint? local = best.IdOffset >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(best.IdOffset - 4, 4))
            : null;
        string confidence = best.Score >= 90 ? "HIGH" : best.Score >= 55 ? "MEDIUM" : "LOW";
        string status = "CONFIRMADO";
        return new ClubVariantRecord(
            displayName,
            division,
            expectedId,
            expectedId,
            best.IdOffset,
            best.IdOffset - 4,
            local,
            best.FullName,
            best.ShortName,
            best.NameOffset,
            best.NameDistance == int.MaxValue ? null : best.NameDistance,
            best.Variant,
            best.Separator,
            best.Constant189,
            best.Constant255,
            status,
            confidence,
            HexContext(data, best.IdOffset, 96));
    }

    private static Candidate EvaluateCandidate(byte[] data, int idOffset, uint expectedId, string[] terms)
    {
        int score = 20;
        string variant = "VARIANT_C_VARIABLE";
        byte? separator = null;
        uint? c189 = null;
        uint? c255 = null;

        if (TryReadUInt32(data, idOffset + 4, out uint direct189) && direct189 == 189 &&
            TryReadUInt32(data, idOffset + 8, out uint direct255) && direct255 == 255)
        {
            variant = "VARIANT_B_DIRECT_189_255";
            c189 = direct189;
            c255 = direct255;
            score += 45;
        }
        else if (idOffset + 13 <= data.Length && data[idOffset + 4] == 0 &&
            TryReadUInt32(data, idOffset + 5, out uint shifted189) && shifted189 == 189 &&
            TryReadUInt32(data, idOffset + 9, out uint shifted255) && shifted255 == 255)
        {
            variant = "VARIANT_A_SEPARATOR_00";
            separator = 0;
            c189 = shifted189;
            c255 = shifted255;
            score += 50;
        }
        else
        {
            for (int rel = 4; rel <= 48; rel++)
            {
                if (!TryReadUInt32(data, idOffset + rel, out uint value) || value != 189) continue;
                c189 = 189;
                if (TryReadUInt32(data, idOffset + rel + 4, out uint value255) && value255 == 255)
                    c255 = 255;
                variant = c255 == 255 ? $"VARIANT_C_189_AT_PLUS_{rel}" : "VARIANT_C_VARIABLE";
                score += c255 == 255 ? 30 : 15;
                break;
            }
        }

        NameMatch? name = FindBestName(data, idOffset, terms);
        if (name is not null)
        {
            score += name.ExactTerm ? 50 : 35;
            if (name.Distance <= 512) score += 15;
            if (name.Distance <= 256) score += 10;
        }

        if (idOffset >= 4)
        {
            uint local = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idOffset - 4, 4));
            if (local < expectedId && local < 100000) score += 8;
        }

        return new Candidate(
            idOffset,
            score,
            variant,
            separator,
            c189,
            c255,
            name?.FullName,
            name?.ShortName,
            name?.Offset,
            name?.Distance ?? int.MaxValue);
    }

    private static NameMatch? FindBestName(byte[] data, int idOffset, string[] terms)
    {
        int start = Math.Max(0, idOffset - NameSearchBefore);
        int end = Math.Min(data.Length, idOffset + NameSearchAfter);
        NameMatch? best = null;
        foreach (string term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[] pattern = Encoding.Unicode.GetBytes(term);
            foreach (int p in FindAll(data, pattern, start, end))
            {
                int distance = Math.Abs(idOffset - p);
                string? full = TryReadLengthPrefixedString(data, p, out string decoded) ? decoded : term;
                string? shortName = FindFollowingString(data, p + pattern.Length, Math.Min(idOffset, p + 256));
                var match = new NameMatch(full, shortName, p, distance, true);
                if (best is null || match.Distance < best.Distance) best = match;
            }
        }
        return best;
    }

    private static bool TryReadLengthPrefixedString(byte[] data, int textOffset, out string value)
    {
        value = string.Empty;
        if (textOffset < 4) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(textOffset - 4, 4));
        if (length <= 0 || length > 160 || textOffset + length * 2 > data.Length) return false;
        string decoded = Encoding.Unicode.GetString(data, textOffset, length * 2);
        if (!IsPlausibleName(decoded)) return false;
        value = decoded;
        return true;
    }

    private static string? FindFollowingString(byte[] data, int start, int end)
    {
        int limit = Math.Min(end, data.Length - 8);
        for (int p = Math.Max(4, start); p <= limit; p++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p - 4, 4));
            if (length is < 2 or > 60 || p + length * 2 + 2 > data.Length) continue;
            string text = Encoding.Unicode.GetString(data, p, length * 2);
            if (!IsPlausibleName(text)) continue;
            if (data[p + length * 2] != 0 || data[p + length * 2 + 1] != 0) continue;
            return text;
        }
        return null;
    }

    private static bool IsPlausibleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        int printable = value.Count(ch => !char.IsControl(ch));
        return printable == value.Length && value.Any(char.IsLetter);
    }

    private static ClubVariantRecord Missing(string displayName, int division, uint id, string status) =>
        new(displayName, division, id, null, null, null, null, null, null, null, null,
            "NOT_FOUND", null, null, null, status, "NONE", string.Empty);

    private static bool TryReadUInt32(byte[] data, int offset, out uint value)
    {
        if (offset < 0 || offset + 4 > data.Length) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return true;
    }

    private static IEnumerable<int> FindAll(byte[] data, byte[] pattern) => FindAll(data, pattern, 0, data.Length);

    private static IEnumerable<int> FindAll(byte[] data, byte[] pattern, int start, int end)
    {
        if (pattern.Length == 0) yield break;
        int last = Math.Min(data.Length - pattern.Length, end - pattern.Length);
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            int j = 0;
            for (; j < pattern.Length; j++) if (data[i + j] != pattern[j]) break;
            if (j == pattern.Length) yield return i;
        }
    }

    private static string HexContext(byte[] data, int center, int total)
    {
        int start = Math.Max(0, center - total / 2);
        int count = Math.Min(total, data.Length - start);
        return Convert.ToHexString(data.AsSpan(start, count));
    }

    public static string FormatReport(ClubRecordVariantReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — ClubRecordVariantDiagnostic 0.0.15");
        sb.AppendLine(new string('=', 92));
        sb.AppendLine($"Arquivo: {report.SourceFile}");
        sb.AppendLine($"Tamanho: {report.SourceSize:N0} bytes");
        sb.AppendLine($"SHA-256: {report.Sha256}");
        sb.AppendLine($"Série A confirmados: {report.Clubs.Count(x => x.Division == 1 && x.Status == "CONFIRMADO")}/24");
        sb.AppendLine($"Série B confirmados: {report.Clubs.Count(x => x.Division == 2 && x.Status == "CONFIRMADO")}/24");
        sb.AppendLine();
        sb.AppendLine("Variantes estruturais:");
        foreach (VariantSignatureSummary s in report.Signatures)
            sb.AppendLine($"  {s.Variant,-30} total={s.Total,2} A={s.SerieA,2} B={s.SerieB,2} | {s.Examples}");
        sb.AppendLine();
        sb.AppendLine("Validação dos 48 clubes:");
        foreach (ClubVariantRecord c in report.Clubs)
            sb.AppendLine($"  S{c.Division} {c.DisplayName,-24} esperado={c.ExpectedClubId,6} encontrado={(c.FoundClubId?.ToString(CultureInfo.InvariantCulture) ?? "-"),6} offset={(c.ClubIdOffset.HasValue ? $"0x{c.ClubIdOffset:X8}" : "-")} variante={c.Variant,-28} {c.Status} {c.Confidence}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados: club-record-variant-report.txt, club-record-variants.csv, known-club-validation.csv, variant-signatures.csv e club-record-contexts.txt.");
        sb.AppendLine("O arquivo de origem não foi modificado.");
        return sb.ToString();
    }

    private static string FormatVariantsCsv(ClubRecordVariantReport report)
    {
        var sb = new StringBuilder("display_name,division,expected_club_id,found_club_id,club_id_offset,local_value_offset,local_value,full_name,short_name,name_offset,distance_name_to_id,variant,separator,constant_189,constant_255,status,confidence,context_hex\r\n");
        foreach (ClubVariantRecord c in report.Clubs)
            sb.AppendLine($"{Csv(c.DisplayName)},{c.Division},{c.ExpectedClubId},{Value(c.FoundClubId)},{Offset(c.ClubIdOffset)},{Offset(c.LocalValueOffset)},{Value(c.LocalValue)},{Csv(c.FullName ?? string.Empty)},{Csv(c.ShortName ?? string.Empty)},{Offset(c.NameOffset)},{Value(c.DistanceNameToId)},{c.Variant},{Value(c.Separator)},{Value(c.Constant189)},{Value(c.Constant255)},{c.Status},{c.Confidence},{c.ContextHex}");
        return sb.ToString();
    }

    private static string FormatValidationCsv(ClubRecordVariantReport report)
    {
        var sb = new StringBuilder("display_name,division,expected_club_id,found_club_id,status,confidence,club_id_offset,variant\r\n");
        foreach (ClubVariantRecord c in report.Clubs)
            sb.AppendLine($"{Csv(c.DisplayName)},{c.Division},{c.ExpectedClubId},{Value(c.FoundClubId)},{c.Status},{c.Confidence},{Offset(c.ClubIdOffset)},{c.Variant}");
        return sb.ToString();
    }

    private static string FormatSignaturesCsv(ClubRecordVariantReport report)
    {
        var sb = new StringBuilder("variant,total,serie_a,serie_b,examples\r\n");
        foreach (VariantSignatureSummary s in report.Signatures)
            sb.AppendLine($"{s.Variant},{s.Total},{s.SerieA},{s.SerieB},{Csv(s.Examples)}");
        return sb.ToString();
    }

    private static string FormatContexts(ClubRecordVariantReport report)
    {
        var sb = new StringBuilder();
        foreach (ClubVariantRecord c in report.Clubs)
        {
            sb.AppendLine($"[{c.DisplayName}] division={c.Division} id={c.ExpectedClubId} status={c.Status} confidence={c.Confidence}");
            sb.AppendLine($"  full={c.FullName ?? "-"} | short={c.ShortName ?? "-"}");
            sb.AppendLine($"  id_offset={Offset(c.ClubIdOffset)} local={Value(c.LocalValue)} variant={c.Variant} separator={Value(c.Separator)} c189={Value(c.Constant189)} c255={Value(c.Constant255)}");
            sb.AppendLine($"  context={c.ContextHex}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Offset(long? value) => value.HasValue ? $"0x{value.Value:X8}" : string.Empty;
    private static string Value<T>(T? value) where T : struct => value?.ToString() ?? string.Empty;

    private sealed record Candidate(
        int IdOffset,
        int Score,
        string Variant,
        byte? Separator,
        uint? Constant189,
        uint? Constant255,
        string? FullName,
        string? ShortName,
        long? NameOffset,
        int NameDistance);

    private sealed record NameMatch(
        string? FullName,
        string? ShortName,
        int Offset,
        int Distance,
        bool ExactTerm);
}
