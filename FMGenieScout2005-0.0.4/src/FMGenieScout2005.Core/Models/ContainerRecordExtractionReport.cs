namespace FMGenieScout2005.Core.Models;

public sealed record ContainerRecordExtractionReport(
    string FilePath,
    long FileSize,
    string Sha256,
    string OutputDirectory,
    IReadOnlyList<ContainerRecord> Records,
    IReadOnlyList<RejectedMarker> RejectedMarkers,
    DateTimeOffset AnalyzedAtUtc);

public sealed record ContainerRecord(
    int Index,
    long MarkerOffset,
    long NameOffset,
    long ExtensionOffset,
    long? NextMarkerOffset,
    long RawSize,
    string BaseName,
    string Extension,
    string FullName,
    string OutputFileName,
    string HeaderPrefixHex,
    bool Extracted,
    string? ExtractionError);

public sealed record RejectedMarker(
    long MarkerOffset,
    string Reason,
    string ContextHex);
