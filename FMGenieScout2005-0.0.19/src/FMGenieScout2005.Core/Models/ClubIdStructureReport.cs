namespace FMGenieScout2005.Core.Models;

public sealed record ClubIdRecord(
    int Row,
    long FullLengthOffset,
    string FullName,
    long ShortLengthOffset,
    string ShortName,
    long IdBlockOffset,
    int IdRelativeToShortEnd,
    uint LocalIndex,
    uint ClubId,
    int Delta,
    byte Separator,
    uint FollowingValue,
    uint Constant255,
    long NextRecordOffset,
    long EstimatedSize,
    string Confidence,
    string Validation);

public sealed record ClubIdKnownValidation(string ClubName, uint ExpectedId, uint? FoundId, long? Offset, string Status);
public sealed record ClubIdDeltaStat(int Delta, int Count, string Examples);
public sealed record ClubIdCommonField(int RelativeToIdBlock, int Samples, string MostCommonHex, int MostCommonCount, double ConstantPercentage, uint Minimum, uint Maximum);

public sealed record ClubIdStructureReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    long RegionStart,
    long RegionEnd,
    IReadOnlyList<ClubIdRecord> Clubs,
    IReadOnlyList<ClubIdKnownValidation> KnownValidations,
    IReadOnlyList<ClubIdDeltaStat> DeltaStatistics,
    IReadOnlyList<ClubIdCommonField> CommonFields,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
