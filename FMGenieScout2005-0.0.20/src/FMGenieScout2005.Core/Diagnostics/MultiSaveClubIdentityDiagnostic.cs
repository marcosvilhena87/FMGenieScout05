using System.Globalization;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class MultiSaveClubIdentityDiagnostic
{
    private readonly GlobalClubHeaderDiagnostic _parser = new();

    public async Task<MultiSaveClubIdentityReport> AnalyzeAsync(
        string save1File,
        string save2File,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string source1 = Path.GetFullPath(save1File);
        string source2 = Path.GetFullPath(save2File);
        if (!File.Exists(source1)) throw new FileNotFoundException("O primeiro game_db.payload.bin não existe.", source1);
        if (!File.Exists(source2)) throw new FileNotFoundException("O segundo game_db.payload.bin não existe.", source2);
        if (string.Equals(source1, source2, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selecione dois arquivos diferentes para o teste multi-save.");

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string parser1Output = Path.Combine(output, "save1-parser");
        string parser2Output = Path.Combine(output, "save2-parser");

        progress?.Report("Analisando o Save 1...");
        GlobalClubHeaderReport save1 = await _parser.AnalyzeAsync(source1, parser1Output, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Analisando o Save 2...");
        GlobalClubHeaderReport save2 = await _parser.AnalyzeAsync(source2, parser2Output, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("Cruzando clubes por ClubDatabaseId...");
        Dictionary<uint, GlobalClubHeaderRecord> map1 = save1.Clubs.ToDictionary(x => x.ClubId);
        Dictionary<uint, GlobalClubHeaderRecord> map2 = save2.Clubs.ToDictionary(x => x.ClubId);
        uint[] allIds = map1.Keys.Union(map2.Keys).OrderBy(x => x).ToArray();
        List<MultiSaveClubIdentityRecord> records = new(allIds.Length);

        foreach (uint clubId in allIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            map1.TryGetValue(clubId, out GlobalClubHeaderRecord? a);
            map2.TryGetValue(clubId, out GlobalClubHeaderRecord? b);
            bool both = a is not null && b is not null;
            bool indexStable = both && a!.ClubRecordIndex == b!.ClubRecordIndex;
            bool fullEquivalent = both && Equivalent(a!.FullName, b!.FullName);
            bool shortEquivalent = both && Equivalent(a!.ShortName, b!.ShortName);
            long? delta = both ? (long)b!.ClubRecordIndex - a!.ClubRecordIndex : null;
            string status = a is null ? "APENAS_SAVE_2"
                : b is null ? "APENAS_SAVE_1"
                : !fullEquivalent ? "NOME_DIVERGENTE"
                : indexStable ? "ID_E_INDICE_ESTAVEIS"
                : "ID_ESTAVEL_INDICE_ALTERADO";

            records.Add(new MultiSaveClubIdentityRecord(
                clubId, a?.FullName, b?.FullName, a?.ShortName, b?.ShortName,
                a?.ClubRecordIndex, b?.ClubRecordIndex, a?.HeaderOffset, b?.HeaderOffset,
                a is not null, b is not null, both, indexStable, fullEquivalent, shortEquivalent,
                delta, status));
        }

        int shared = records.Count(x => x.PresentInSave1 && x.PresentInSave2);
        var report = new MultiSaveClubIdentityReport(
            source1, source2, save1.Sha256, save2.Sha256, records,
            save1.Clubs.Count, save2.Clubs.Count, shared,
            records.Count(x => x.PresentInSave1 && !x.PresentInSave2),
            records.Count(x => !x.PresentInSave1 && x.PresentInSave2),
            records.Count(x => x.PresentInSave1 && x.PresentInSave2 && !x.SaveIndexStable),
            records.Count(x => x.PresentInSave1 && x.PresentInSave2 && x.SaveIndexStable),
            records.Count(x => x.PresentInSave1 && x.PresentInSave2 && !x.FullNameEquivalent),
            output, DateTimeOffset.UtcNow);

        progress?.Report("Gravando relatórios multi-save...");
        await File.WriteAllTextAsync(Path.Combine(output, "multi-save-club-identity-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "multi-save-club-identity.csv"), FormatIdentityCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "changed-save-indices.csv"), FormatFilteredCsv(report, x => x.Status == "ID_ESTAVEL_INDICE_ALTERADO"), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "save-membership-differences.csv"), FormatFilteredCsv(report, x => x.Status is "APENAS_SAVE_1" or "APENAS_SAVE_2"), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "club-name-mismatches.csv"), FormatFilteredCsv(report, x => x.Status == "NOME_DIVERGENTE"), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        progress?.Report("Concluído.");
        return report;
    }

    private static bool Equivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)) return true;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string FormatReport(MultiSaveClubIdentityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FM Genie Scout 2005 — MultiSaveClubIdentityDiagnostic 0.0.18");
        sb.AppendLine(new string('=', 92));
        sb.AppendLine($"Save 1: {report.Save1File}");
        sb.AppendLine($"SHA-256 Save 1: {report.Save1Sha256}");
        sb.AppendLine($"Clubes Save 1: {report.Save1ClubCount:N0}");
        sb.AppendLine();
        sb.AppendLine($"Save 2: {report.Save2File}");
        sb.AppendLine($"SHA-256 Save 2: {report.Save2Sha256}");
        sb.AppendLine($"Clubes Save 2: {report.Save2ClubCount:N0}");
        sb.AppendLine();
        sb.AppendLine("Resultado do cruzamento por ClubDatabaseId:");
        sb.AppendLine($"  Clubes compartilhados: {report.SharedClubCount:N0}");
        sb.AppendLine($"  Apenas no Save 1:      {report.OnlySave1Count:N0}");
        sb.AppendLine($"  Apenas no Save 2:      {report.OnlySave2Count:N0}");
        sb.AppendLine($"  SaveClubIndex estável: {report.StableIndexCount:N0}");
        sb.AppendLine($"  SaveClubIndex alterado:{report.ChangedIndexCount,6:N0}");
        sb.AppendLine($"  Nomes divergentes:     {report.NameMismatchCount:N0}");
        sb.AppendLine();
        sb.AppendLine("Interpretação automática:");
        if (report.SharedClubCount == 0)
            sb.AppendLine("  Não há clubes compartilhados suficientes para testar a hipótese.");
        else if (report.ChangedIndexCount > 0 && report.NameMismatchCount == 0)
            sb.AppendLine("  EVIDÊNCIA_FORTE: ClubDatabaseId permaneceu como chave de identidade enquanto SaveClubIndex mudou entre os saves.");
        else if (report.ChangedIndexCount == 0)
            sb.AppendLine("  INCONCLUSIVO: os ClubDatabaseIds coincidem, mas os SaveClubIndexes também ficaram iguais nestes dois saves.");
        else
            sb.AppendLine("  REVISAR: existem mudanças de índice e divergências de nome que precisam de inspeção.");
        sb.AppendLine();
        sb.AppendLine("Maiores mudanças de SaveClubIndex:");
        foreach (MultiSaveClubIdentityRecord x in report.Clubs.Where(x => x.IndexDelta.HasValue).OrderByDescending(x => Math.Abs(x.IndexDelta.GetValueOrDefault())).Take(30))
            sb.AppendLine($"  id={x.ClubId,8} {DisplayName(x),-38} save1={x.Save1Index,6} save2={x.Save2Index,6} delta={x.IndexDelta.GetValueOrDefault(),7:+0;-0;0}");
        sb.AppendLine();
        sb.AppendLine("Arquivos gerados: multi-save-club-identity-report.txt, multi-save-club-identity.csv, changed-save-indices.csv, save-membership-differences.csv, club-name-mismatches.csv e pastas save1-parser/save2-parser.");
        sb.AppendLine("Os arquivos de origem não foram modificados.");
        return sb.ToString();
    }

    private static string DisplayName(MultiSaveClubIdentityRecord x) => x.Save1FullName ?? x.Save2FullName ?? $"ClubId {x.ClubId}";

    private static string FormatIdentityCsv(MultiSaveClubIdentityReport report) => FormatRows(report.Clubs);
    private static string FormatFilteredCsv(MultiSaveClubIdentityReport report, Func<MultiSaveClubIdentityRecord, bool> predicate) => FormatRows(report.Clubs.Where(predicate));

    private static string FormatRows(IEnumerable<MultiSaveClubIdentityRecord> rows)
    {
        var sb = new StringBuilder("club_database_id,save1_full_name,save2_full_name,save1_short_name,save2_short_name,save1_index,save2_index,index_delta,save1_offset,save2_offset,present_save1,present_save2,database_id_stable,save_index_stable,full_name_equivalent,short_name_equivalent,status\r\n");
        foreach (MultiSaveClubIdentityRecord x in rows)
            sb.AppendLine(string.Join(',', x.ClubId, Csv(x.Save1FullName), Csv(x.Save2FullName), Csv(x.Save1ShortName), Csv(x.Save2ShortName),
                x.Save1Index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                x.Save2Index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                x.IndexDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                x.Save1Offset.HasValue ? $"0x{x.Save1Offset.Value:X8}" : string.Empty,
                x.Save2Offset.HasValue ? $"0x{x.Save2Offset.Value:X8}" : string.Empty,
                x.PresentInSave1, x.PresentInSave2, x.DatabaseIdStable, x.SaveIndexStable,
                x.FullNameEquivalent, x.ShortNameEquivalent, x.Status));
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        string text = value ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
