namespace FMGenieScout2005.Core.Models;

public sealed record ClubVariantRecord(
    string DisplayName,
    int Division,
    uint ExpectedClubId,
    uint? FoundClubId,
    long? ClubIdOffset,
    long? LocalValueOffset,
    uint? LocalValue,
    string? FullName,
    string? ShortName,
    long? NameOffset,
    int? DistanceNameToId,
    string Variant,
    byte? Separator,
    uint? Constant189,
    uint? Constant255,
    string Status,
    string Confidence,
    string ContextHex);

public sealed record VariantSignatureSummary(
    string Variant,
    int Total,
    int SerieA,
    int SerieB,
    string Examples);

public sealed record ClubRecordVariantReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<ClubVariantRecord> Clubs,
    IReadOnlyList<VariantSignatureSummary> Signatures,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
