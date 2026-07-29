namespace FMGenieScout2005.Core.Models;

public sealed record CompetitionComponentInfo(
    uint CompetitionId,
    string CompetitionName,
    string FilePath,
    long FileSize,
    string Sha256,
    int CompetitionIdOccurrencesUtf16Name,
    int CompetitionIdOccurrencesUInt32,
    int CompetitionIdOccurrencesUInt16);

public sealed record CompetitionMembershipOccurrence(
    uint CompetitionId,
    string CompetitionName,
    string ClubName,
    uint ClubId,
    string Encoding,
    long Offset,
    string ContextHex,
    string Assessment);

public sealed record CompetitionKnownClubValidation(
    string ClubName,
    uint ClubId,
    bool ExpectedInFirstDivision,
    int FirstDivisionOccurrences,
    int SecondDivisionOccurrences,
    string Status);

public sealed record CompetitionCandidateMembership(
    string ClubName,
    string ShortName,
    uint ClubId,
    int FirstDivisionOccurrences,
    int SecondDivisionOccurrences,
    string MembershipStatus,
    string Confidence);

public sealed record CompetitionMembershipReport(
    string GameDbFile,
    string FirstDivisionFile,
    string SecondDivisionFile,
    long GameDbSize,
    string GameDbSha256,
    IReadOnlyList<CompetitionComponentInfo> Components,
    IReadOnlyList<CompetitionMembershipOccurrence> Occurrences,
    IReadOnlyList<CompetitionKnownClubValidation> KnownValidations,
    IReadOnlyList<CompetitionCandidateMembership> CandidateMemberships,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
