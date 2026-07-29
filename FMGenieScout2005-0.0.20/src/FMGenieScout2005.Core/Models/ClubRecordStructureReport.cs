namespace FMGenieScout2005.Core.Models;

public sealed record ClubStructureRecord(
    string DisplayName,
    int Division,
    uint ExpectedClubId,
    uint? FoundClubId,
    long? ClubIdOffset,
    long? ClubRecordIndexOffset,
    uint? ClubRecordIndex,
    string? FullName,
    string? ShortName,
    long? NameOffset,
    int? DistanceNameToId,
    string Variant,
    byte? Separator,
    uint? UnknownValue1,
    uint? UnknownValue2,
    string Status,
    string Confidence,
    string ContextHex);

public sealed record StructureSignatureSummary(
    string Variant,
    int Total,
    int SerieA,
    int SerieB,
    string Examples);

public sealed record ClubRecordStructureReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<ClubStructureRecord> Clubs,
    IReadOnlyList<StructureSignatureSummary> Signatures,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
