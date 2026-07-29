namespace FMGenieScout2005.Core.Models;

public sealed record ClubFamilyMatch(
    string ExpectedDisplayName, int Division, string? FullName, string? ShortName,
    uint ExpectedClubId, uint? FoundClubId, long? IdBlockOffset, uint? LocalIndex,
    int? Delta, int? IdRelativeToShortEnd, long? EstimatedSize,
    string Family, string Status, string MatchMethod);

public sealed record ClubFamilySummary(
    string Family, int Total, int SerieA, int SerieB, uint MinimumClubId, uint MaximumClubId,
    int DistinctDeltas, string CommonDeltas, string CommonDistances, string Examples);

public sealed record FamilyFieldCandidate(
    string Family, string DataType, int RelativeOffset,
    int SerieASamples, string SerieAMode, int SerieAModeCount, double SerieAModeRate,
    int SerieBSamples, string SerieBMode, int SerieBModeCount, double SerieBModeRate,
    double SeparationScore, string Assessment);

public sealed record MissingClubSearchHit(
    string Club, uint ExpectedClubId, string SearchKind, long Offset, string Value, string ContextHex);

public sealed record ClubRecordFamilyReport(
    string SourceFile, long SourceSize, string Sha256,
    IReadOnlyList<ClubFamilyMatch> ClubMatches,
    IReadOnlyList<ClubFamilySummary> Families,
    IReadOnlyList<FamilyFieldCandidate> Candidates,
    IReadOnlyList<MissingClubSearchHit> MissingSearchHits,
    string OutputDirectory, DateTimeOffset AnalyzedAtUtc);
