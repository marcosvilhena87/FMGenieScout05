namespace FMGenieScout2005.Core.Models;

public sealed record GameDbStringEntry(int Index, long Offset, int Length, string Text, long? PreviousOffset, long? DistanceFromPrevious, int Group);
public sealed record GameDbSearchHit(string Query, long Offset, string Text, string ContextHex);
public sealed record GameDbGroup(int Group, long StartOffset, long EndOffset, int StringCount, string Preview);
public sealed record GameDbStructureReport(
    string SourceFile,
    long SourceSize,
    string Sha256,
    int PayloadOffset,
    long PayloadSize,
    string OutputDirectory,
    IReadOnlyList<GameDbStringEntry> Strings,
    IReadOnlyList<GameDbGroup> Groups,
    IReadOnlyList<GameDbSearchHit> SearchHits,
    DateTimeOffset AnalyzedAtUtc);
