namespace FMGenieScout2005.Core.Models;

public sealed record DivisionClubMatch(
    string ExpectedDisplayName,
    int Division,
    string? FullName,
    string? ShortName,
    uint? ClubId,
    long? IdBlockOffset,
    string Status,
    string MatchMethod);

public sealed record CompetitionFieldCandidate(
    string DataType,
    int RelativeOffset,
    int SerieASamples,
    string SerieAMode,
    int SerieAModeCount,
    double SerieAModeRate,
    int SerieBSamples,
    string SerieBMode,
    int SerieBModeCount,
    double SerieBModeRate,
    double SeparationScore,
    string Assessment);

public sealed record ClubCompetitionFieldReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<DivisionClubMatch> ClubMatches,
    IReadOnlyList<CompetitionFieldCandidate> Candidates,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
