namespace FMGenieScout2005.Core.Models;

public sealed record GlobalClubHeaderRecord(
    uint ClubRecordIndex,
    uint ClubId,
    long HeaderOffset,
    byte Separator1,
    uint Field1,
    uint Field2,
    byte Separator2,
    uint Field3,
    string? FullName,
    string? ShortName,
    long? NameOffset,
    int? NameDistance,
    int Score,
    string Confidence,
    string ContextHex);

public sealed record RejectedClubHeaderRecord(
    long HeaderOffset,
    uint ClubRecordIndex,
    uint ClubId,
    string Reason,
    int Score,
    string ContextHex);

public sealed record KnownClubCoverageRecord(
    string DisplayName,
    int Division,
    uint ClubId,
    bool Found,
    uint? ClubRecordIndex,
    long? HeaderOffset,
    string? ParsedName,
    string Status);

public sealed record GlobalClubHeaderReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<GlobalClubHeaderRecord> Clubs,
    IReadOnlyList<RejectedClubHeaderRecord> Rejected,
    IReadOnlyList<KnownClubCoverageRecord> KnownCoverage,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
