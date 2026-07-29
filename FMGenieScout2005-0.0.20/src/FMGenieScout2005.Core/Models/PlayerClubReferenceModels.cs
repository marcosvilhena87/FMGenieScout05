namespace FMGenieScout2005.Core.Models;

public sealed record PlayerNameOccurrence(
    string ExpectedName,
    long NameOffset,
    int EncodedByteLength,
    string EncodingKind,
    int OccurrenceNumber);

public sealed record PlayerClubReferenceHit(
    string PlayerName,
    long NameOffset,
    string ReferenceKind,
    uint ReferenceValue,
    long ReferenceOffset,
    long RelativeToNameStart,
    long RelativeToNameEnd,
    int Width,
    string Direction,
    string ContextHex);

public sealed record PlayerReferenceOffsetSummary(
    string ReferenceKind,
    long RelativeToNameStart,
    int PlayerCount,
    int HitCount,
    int DistinctNameOccurrenceCount,
    double PlayerCoverage,
    string Players,
    string Assessment);

public sealed record PlayerClubReferenceReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    uint ClubDatabaseId,
    uint ClubSaveIndex,
    string ClubName,
    IReadOnlyList<string> ExpectedPlayers,
    IReadOnlyList<PlayerNameOccurrence> NameOccurrences,
    IReadOnlyList<PlayerClubReferenceHit> ReferenceHits,
    IReadOnlyList<PlayerReferenceOffsetSummary> OffsetSummaries,
    string OutputDirectory,
    DateTimeOffset GeneratedAtUtc);
