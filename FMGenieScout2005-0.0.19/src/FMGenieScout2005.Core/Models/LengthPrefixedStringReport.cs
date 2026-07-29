namespace FMGenieScout2005.Core.Models;

public sealed record LengthPrefixedStringEntry(
    int Index,
    long LengthOffset,
    long TextOffset,
    int Length,
    string Text,
    bool HasNullTerminator,
    double Plausibility,
    int Sequence,
    long? PreviousLengthOffset,
    long? DistanceFromPrevious,
    int BeforeInt32Minus16,
    int BeforeInt32Minus12,
    int BeforeInt32Minus8,
    int BeforeInt32Minus4,
    string ContextHex);

public sealed record LengthPrefixedSequence(
    int Sequence,
    long StartOffset,
    long EndOffset,
    int EntryCount,
    double AverageGap,
    string Preview);

public sealed record LengthPrefixedSearchHit(
    string Query,
    long LengthOffset,
    long TextOffset,
    string Text,
    int Sequence,
    string ContextHex);

public sealed record PossibleNameRecord(
    int Index,
    long RecordOffset,
    long LengthOffset,
    int FieldMinus16,
    int FieldMinus12,
    int FieldMinus8,
    int FieldMinus4,
    int NameLength,
    string Name,
    int Sequence,
    string Confidence);

public sealed record LengthPrefixedStringReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    IReadOnlyList<LengthPrefixedStringEntry> Entries,
    IReadOnlyList<LengthPrefixedSequence> Sequences,
    IReadOnlyList<LengthPrefixedSearchHit> SearchHits,
    IReadOnlyList<PossibleNameRecord> PossibleNameRecords,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
