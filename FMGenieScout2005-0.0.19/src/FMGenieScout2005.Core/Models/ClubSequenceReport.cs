namespace FMGenieScout2005.Core.Models;

public sealed record ClubSequenceRecord(
    int Row,
    long EstimatedStart,
    long FullLengthOffset,
    long FullTextOffset,
    string FullName,
    long ShortLengthOffset,
    long ShortTextOffset,
    string ShortName,
    long NextRecordOffset,
    long EstimatedSize,
    ushort? FoundationYear,
    long? FoundationYearOffset,
    int? FoundationYearRelativeOffset,
    int PairGap,
    string Confidence,
    string Validation);

public sealed record ClubYearCandidate(int ClubRow, string ClubName, long AbsoluteOffset, int RelativeOffset, ushort Value, string Status);
public sealed record ClubSizeBucket(long Minimum, long Maximum, int Count);
public sealed record ClubCommonField(int RelativeOffset, int Samples, string MostCommonHex, int MostCommonCount, double ConstantPercentage, uint MinimumUInt32, uint MaximumUInt32);

public sealed record ClubSequenceReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    long RegionStart,
    long RegionEnd,
    IReadOnlyList<ClubSequenceRecord> Clubs,
    IReadOnlyList<ClubYearCandidate> YearCandidates,
    IReadOnlyList<ClubSizeBucket> SizeBuckets,
    IReadOnlyList<ClubCommonField> CommonFields,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
