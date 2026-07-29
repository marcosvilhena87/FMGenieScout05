using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Domain;
using FMGenieScout2005.Core.Models;
using FMGenieScout2005.Core.Parsing;

namespace FMGenieScout2005.Core.Diagnostics;

/// <summary>
/// Procura referências ao clube próximas aos nomes de jogadores conhecidos.
/// O diagnóstico não assume se o vínculo usa ClubDatabaseId ou SaveIndex:
/// ambos são pesquisados e comparados estatisticamente.
/// </summary>
public sealed class PlayerClubReferenceDiagnostic
{
    private const int SearchWindow = 2048;
    private const int ContextRadius = 32;
    private const uint FlamengoDatabaseId = 322;

    private static readonly string[] FlamengoPlayers =
    [
        "Jean",
        "Felipe",
        "Athirson",
        "Andrezinho",
        "Dimba",
        "Júlio César",
        "André Bahia",
        "Bruno Santos",
        "Ibson",
        "Reginaldo Araújo",
        "Anderson",
        "Fabinho",
        "Jonatas",
        "Juliano",
        "Fabiano Eller",
        "Douglas Silva",
        "Da Silva",
        "Saraiva",
        "Nélio",
        "Valentim",
        "Diego",
        "Júlio César Moraes",
        "Dill",
        "Júnior Baiano",
        "Roger",
        "Zinho"
    ];

    public async Task<PlayerClubReferenceReport> AnalyzeAsync(
        string inputFile,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string source = Path.GetFullPath(inputFile);
        if (!File.Exists(source))
            throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);

        progress?.Report("Lendo game_db.payload.bin...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        progress?.Report("Resolvendo o Flamengo pelo parser de clubes 0.0.19...");
        var clubParser = new Fm2005ClubParser();
        Fm2005ClubParseResult clubResult = clubParser.Parse(data, cancellationToken);
        Club flamengo = clubResult.FindByDatabaseId(FlamengoDatabaseId)
            ?? throw new InvalidOperationException(
                $"O Flamengo (ClubDatabaseId {FlamengoDatabaseId}) não foi encontrado pelo parser de clubes.");

        progress?.Report($"Flamengo resolvido: DatabaseId={flamengo.DatabaseId}, SaveIndex={flamengo.SaveIndex}.");
        progress?.Report("Localizando nomes conhecidos de jogadores...");

        List<PlayerNameOccurrence> occurrences = [];
        foreach (string player in FlamengoPlayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddOccurrences(occurrences, data, player, Encoding.Unicode, "UTF16LE");
            AddOccurrences(occurrences, data, player, Encoding.UTF8, "UTF8");
        }

        occurrences = occurrences
            .OrderBy(x => x.ExpectedName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.NameOffset)
            .ThenBy(x => x.EncodingKind, StringComparer.Ordinal)
            .ToList();

        progress?.Report($"Ocorrências de nomes encontradas: {occurrences.Count:N0}.");
        progress?.Report("Procurando ClubDatabaseId e SaveIndex nas janelas dos jogadores...");

        List<PlayerClubReferenceHit> hits = [];
        foreach (PlayerNameOccurrence occurrence in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchReference(data, occurrence, "CLUB_DATABASE_ID", flamengo.DatabaseId, hits);
            if (flamengo.SaveIndex != flamengo.DatabaseId)
                SearchReference(data, occurrence, "CLUB_SAVE_INDEX", flamengo.SaveIndex, hits);
        }

        PlayerReferenceOffsetSummary[] summaries = BuildSummaries(hits, occurrences, FlamengoPlayers.Length);

        var report = new PlayerClubReferenceReport(
            source,
            data.LongLength,
            sha256,
            flamengo.DatabaseId,
            flamengo.SaveIndex,
            flamengo.FullName,
            FlamengoPlayers,
            occurrences,
            hits.OrderBy(x => x.PlayerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.NameOffset)
                .ThenBy(x => x.ReferenceKind, StringComparer.Ordinal)
                .ThenBy(x => x.ReferenceOffset)
                .ToArray(),
            summaries,
            output,
            DateTimeOffset.UtcNow);

        progress?.Report("Gravando relatórios da investigação jogador → clube...");
        await File.WriteAllTextAsync(
            Path.Combine(output, "player-club-reference-report.txt"),
            FormatReport(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "player-name-occurrences.csv"),
            FormatNameOccurrencesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "player-club-reference-hits.csv"),
            FormatReferenceHitsCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "player-club-relative-offsets.csv"),
            FormatOffsetSummariesCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "player-club-top-contexts.txt"),
            FormatTopContexts(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "players-not-found.txt"),
            FormatPlayersNotFound(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

        progress?.Report("PlayerClubReferenceDiagnostic concluído.");
        return report;
    }

    private static void AddOccurrences(
        ICollection<PlayerNameOccurrence> output,
        byte[] data,
        string player,
        Encoding encoding,
        string encodingKind)
    {
        byte[] pattern = encoding.GetBytes(player);
        if (pattern.Length == 0 || pattern.Length > data.Length) return;

        int occurrenceNumber = 0;
        int offset = 0;
        while (offset <= data.Length - pattern.Length)
        {
            int found = data.AsSpan(offset).IndexOf(pattern);
            if (found < 0) break;

            int absolute = offset + found;
            occurrenceNumber++;
            output.Add(new PlayerNameOccurrence(
                player, absolute, pattern.Length, encodingKind, occurrenceNumber));
            offset = absolute + Math.Max(1, pattern.Length);
        }
    }

    private static void SearchReference(
        byte[] data,
        PlayerNameOccurrence occurrence,
        string referenceKind,
        uint value,
        ICollection<PlayerClubReferenceHit> output)
    {
        int start = checked((int)Math.Max(0, occurrence.NameOffset - SearchWindow));
        long nameEndLong = occurrence.NameOffset + occurrence.EncodedByteLength;
        int endExclusive = checked((int)Math.Min(data.LongLength, nameEndLong + SearchWindow));

        byte[] value32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value32, value);
        AddPatternHits(data, occurrence, referenceKind, value, value32, start, endExclusive, output);

        if (value <= ushort.MaxValue)
        {
            byte[] value16 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(value16, (ushort)value);
            AddPatternHits(data, occurrence, referenceKind, value, value16, start, endExclusive, output);
        }
    }

    private static void AddPatternHits(
        byte[] data,
        PlayerNameOccurrence occurrence,
        string referenceKind,
        uint referenceValue,
        byte[] pattern,
        int start,
        int endExclusive,
        ICollection<PlayerClubReferenceHit> output)
    {
        if (endExclusive - start < pattern.Length) return;

        ReadOnlySpan<byte> window = data.AsSpan(start, endExclusive - start);
        int localOffset = 0;
        while (localOffset <= window.Length - pattern.Length)
        {
            int found = window[localOffset..].IndexOf(pattern);
            if (found < 0) break;

            int referenceOffset = start + localOffset + found;
            long relativeStart = referenceOffset - occurrence.NameOffset;
            long nameEnd = occurrence.NameOffset + occurrence.EncodedByteLength;
            long relativeEnd = referenceOffset - nameEnd;
            string direction = relativeStart < 0 ? "ANTES" : "DEPOIS";

            output.Add(new PlayerClubReferenceHit(
                occurrence.ExpectedName,
                occurrence.NameOffset,
                referenceKind,
                referenceValue,
                referenceOffset,
                relativeStart,
                relativeEnd,
                pattern.Length,
                direction,
                HexContext(data, referenceOffset, ContextRadius)));

            localOffset += found + Math.Max(1, pattern.Length);
        }
    }

    private static PlayerReferenceOffsetSummary[] BuildSummaries(
        IReadOnlyCollection<PlayerClubReferenceHit> hits,
        IReadOnlyCollection<PlayerNameOccurrence> occurrences,
        int expectedPlayerCount)
    {
        int denominator = Math.Max(1, expectedPlayerCount);

        return hits
            .GroupBy(x => new { x.ReferenceKind, x.RelativeToNameStart })
            .Select(group =>
            {
                string[] players = group.Select(x => x.PlayerName)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                int occurrenceCount = group.Select(x => (x.PlayerName, x.NameOffset)).Distinct().Count();
                double coverage = players.Length / (double)denominator;
                string assessment = players.Length >= 8 && coverage >= 0.30 ? "CANDIDATO_FORTE"
                    : players.Length >= 4 ? "CANDIDATO_MEDIO"
                    : players.Length >= 2 ? "CANDIDATO_FRACO"
                    : "ISOLADO";

                return new PlayerReferenceOffsetSummary(
                    group.Key.ReferenceKind,
                    group.Key.RelativeToNameStart,
                    players.Length,
                    group.Count(),
                    occurrenceCount,
                    coverage,
                    string.Join(" | ", players),
                    assessment);
            })
            .OrderByDescending(x => x.PlayerCount)
            .ThenByDescending(x => x.DistinctNameOccurrenceCount)
            .ThenBy(x => Math.Abs(x.RelativeToNameStart))
            .ThenBy(x => x.ReferenceKind, StringComparer.Ordinal)
            .ToArray();
    }

    public static string FormatReport(PlayerClubReferenceReport report)
    {
        int foundPlayers = report.NameOccurrences.Select(x => x.ExpectedName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        int databaseHits = report.ReferenceHits.Count(x => x.ReferenceKind == "CLUB_DATABASE_ID");
        int saveIndexHits = report.ReferenceHits.Count(x => x.ReferenceKind == "CLUB_SAVE_INDEX");

        var builder = new StringBuilder();
        builder.AppendLine("FM Genie Scout 2005 — PlayerClubReferenceDiagnostic 0.0.20");
        builder.AppendLine(new string('=', 96));
        builder.AppendLine($"Arquivo: {report.SourceFile}");
        builder.AppendLine($"Tamanho: {report.SourceSize:N0} bytes");
        builder.AppendLine($"SHA-256: {report.Sha256}");
        builder.AppendLine();
        builder.AppendLine($"Clube: {report.ClubName}");
        builder.AppendLine($"ClubDatabaseId: {report.ClubDatabaseId}");
        builder.AppendLine($"SaveClubIndex: {report.ClubSaveIndex}");
        builder.AppendLine();
        builder.AppendLine($"Jogadores esperados: {report.ExpectedPlayers.Count:N0}");
        builder.AppendLine($"Jogadores com ao menos uma ocorrência: {foundPlayers:N0}");
        builder.AppendLine($"Ocorrências totais de nomes: {report.NameOccurrences.Count:N0}");
        builder.AppendLine($"Hits de ClubDatabaseId: {databaseHits:N0}");
        builder.AppendLine($"Hits de SaveClubIndex: {saveIndexHits:N0}");
        builder.AppendLine();
        builder.AppendLine("Melhores offsets repetidos:");

        foreach (PlayerReferenceOffsetSummary summary in report.OffsetSummaries.Take(40))
        {
            builder.AppendLine(
                $"  {summary.ReferenceKind,-18} rel={summary.RelativeToNameStart,7:+0;-0;0} " +
                $"jogadores={summary.PlayerCount,2} ocorrencias={summary.DistinctNameOccurrenceCount,3} " +
                $"cobertura={summary.PlayerCoverage:P1} | {summary.Assessment}");
        }

        builder.AppendLine();
        builder.AppendLine("Interpretação:");
        PlayerReferenceOffsetSummary? best = report.OffsetSummaries.FirstOrDefault();
        if (best is null)
        {
            builder.AppendLine("  INCONCLUSIVO: nenhuma referência ao clube foi encontrada na janela pesquisada.");
        }
        else if (best.Assessment == "CANDIDATO_FORTE")
        {
            builder.AppendLine(
                $"  EVIDÊNCIA_FORTE: {best.ReferenceKind} aparece no offset relativo " +
                $"{best.RelativeToNameStart:+0;-0;0} para {best.PlayerCount} jogadores.");
        }
        else
        {
            builder.AppendLine(
                $"  AINDA_INCONCLUSIVO: o melhor agrupamento foi {best.ReferenceKind} " +
                $"no offset {best.RelativeToNameStart:+0;-0;0}, cobrindo {best.PlayerCount} jogadores.");
        }

        builder.AppendLine();
        builder.AppendLine("Arquivos gerados:");
        builder.AppendLine("  player-club-reference-report.txt");
        builder.AppendLine("  player-name-occurrences.csv");
        builder.AppendLine("  player-club-reference-hits.csv");
        builder.AppendLine("  player-club-relative-offsets.csv");
        builder.AppendLine("  player-club-top-contexts.txt");
        builder.AppendLine("  players-not-found.txt");
        builder.AppendLine("O arquivo de origem não foi modificado.");
        return builder.ToString();
    }

    private static string FormatNameOccurrencesCsv(PlayerClubReferenceReport report)
    {
        var builder = new StringBuilder("player_name,name_offset,encoded_byte_length,encoding,occurrence_number\r\n");
        foreach (PlayerNameOccurrence occurrence in report.NameOccurrences)
        {
            builder.AppendLine(string.Join(',',
                Csv(occurrence.ExpectedName),
                $"0x{occurrence.NameOffset:X8}",
                occurrence.EncodedByteLength,
                occurrence.EncodingKind,
                occurrence.OccurrenceNumber));
        }
        return builder.ToString();
    }

    private static string FormatReferenceHitsCsv(PlayerClubReferenceReport report)
    {
        var builder = new StringBuilder("player_name,name_offset,reference_kind,reference_value,reference_offset,relative_to_name_start,relative_to_name_end,width,direction,context_hex\r\n");
        foreach (PlayerClubReferenceHit hit in report.ReferenceHits)
        {
            builder.AppendLine(string.Join(',',
                Csv(hit.PlayerName),
                $"0x{hit.NameOffset:X8}",
                hit.ReferenceKind,
                hit.ReferenceValue.ToString(CultureInfo.InvariantCulture),
                $"0x{hit.ReferenceOffset:X8}",
                hit.RelativeToNameStart.ToString(CultureInfo.InvariantCulture),
                hit.RelativeToNameEnd.ToString(CultureInfo.InvariantCulture),
                hit.Width,
                hit.Direction,
                hit.ContextHex));
        }
        return builder.ToString();
    }

    private static string FormatOffsetSummariesCsv(PlayerClubReferenceReport report)
    {
        var builder = new StringBuilder("reference_kind,relative_to_name_start,player_count,hit_count,distinct_name_occurrences,player_coverage,assessment,players\r\n");
        foreach (PlayerReferenceOffsetSummary summary in report.OffsetSummaries)
        {
            builder.AppendLine(string.Join(',',
                summary.ReferenceKind,
                summary.RelativeToNameStart.ToString(CultureInfo.InvariantCulture),
                summary.PlayerCount,
                summary.HitCount,
                summary.DistinctNameOccurrenceCount,
                summary.PlayerCoverage.ToString("F6", CultureInfo.InvariantCulture),
                summary.Assessment,
                Csv(summary.Players)));
        }
        return builder.ToString();
    }

    private static string FormatTopContexts(PlayerClubReferenceReport report)
    {
        var builder = new StringBuilder();
        foreach (PlayerReferenceOffsetSummary summary in report.OffsetSummaries.Take(30))
        {
            builder.AppendLine(
                $"[{summary.ReferenceKind} rel={summary.RelativeToNameStart:+0;-0;0}] " +
                $"jogadores={summary.PlayerCount} | {summary.Assessment}");
            foreach (PlayerClubReferenceHit hit in report.ReferenceHits
                .Where(x => x.ReferenceKind == summary.ReferenceKind
                    && x.RelativeToNameStart == summary.RelativeToNameStart)
                .Take(20))
            {
                builder.AppendLine(
                    $"  {hit.PlayerName,-24} nome=0x{hit.NameOffset:X8} " +
                    $"ref=0x{hit.ReferenceOffset:X8} width={hit.Width} | {hit.ContextHex}");
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatPlayersNotFound(PlayerClubReferenceReport report)
    {
        HashSet<string> found = report.NameOccurrences.Select(x => x.ExpectedName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var builder = new StringBuilder();
        foreach (string player in report.ExpectedPlayers.Where(x => !found.Contains(x)))
            builder.AppendLine(player);
        return builder.ToString();
    }

    private static string HexContext(byte[] data, int center, int radius)
    {
        int start = Math.Max(0, center - radius);
        int length = Math.Min(radius * 2, data.Length - start);
        return Convert.ToHexString(data.AsSpan(start, length));
    }

    private static string Csv(string value) =>
        '"' + value.Replace("\"", "\"\"") + '"';
}
