namespace FMGenieScout2005.Core.Models;

public sealed record NameTableRecord(
    int RowNumber,
    long RecordOffset,
    ushort Type,
    uint Category,
    uint Index,
    uint Reference,
    long LengthOffset,
    int NameLength,
    string Name,
    bool HasNullTerminator,
    long NextOffset,
    int RecordSize,
    int Sequence,
    bool IndexSequentialFromPrevious,
    string Confidence,
    string ContextHex);

public sealed record NameTypeStatistic(
    ushort Type,
    uint Category,
    int Count,
    int SequentialTransitions,
    double SequentialRate,
    uint MinimumIndex,
    uint MaximumIndex,
    string Examples);

public sealed record TargetedNameRecord(
    string Query,
    long RecordOffset,
    ushort Type,
    uint Category,
    uint Index,
    uint Reference,
    string Name,
    int Sequence,
    string PreviousNames,
    string NextNames,
    string ContextHex);

public sealed record ReferenceOccurrence(
    string SourceName,
    string SourceField,
    uint Value,
    long Offset,
    string RelativeToRecord,
    string ContextHex);

public sealed record ClubStringPair(
    string Query,
    string FullName,
    string ShortName,
    long FullNameLengthOffset,
    long ShortNameLengthOffset,
    int Sequence,
    string ContextHex);

public sealed record NameTableRecordReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<NameTableRecord> Records,
    IReadOnlyList<NameTypeStatistic> TypeStatistics,
    IReadOnlyList<TargetedNameRecord> TargetedRecords,
    IReadOnlyList<ReferenceOccurrence> ReferenceOccurrences,
    IReadOnlyList<ClubStringPair> ClubPairs,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
