namespace FMGenieScout2005.Core.Models;

public sealed record ContainerDiagnosticReport(
    string FilePath,
    long FileSize,
    string Sha256,
    string HeaderHex,
    double SampleEntropy,
    IReadOnlyList<ComponentCandidate> Components,
    IReadOnlyList<DistanceGroup> DistanceGroups,
    IReadOnlyList<CompressionValidationResult> CompressionResults,
    DateTimeOffset AnalyzedAtUtc);

public sealed record ComponentCandidate(
    int Index,
    string BaseName,
    string Extension,
    string FullName,
    long NameOffset,
    long? ExtensionOffset,
    long? NextNameOffset,
    long? DistanceToNext,
    string ContextBeforeHex,
    string ContextAfterHex,
    IReadOnlyList<IntegerInterpretation> IntegersBeforeName);

public sealed record IntegerInterpretation(
    int RelativeOffset,
    ushort UInt16,
    uint UInt32,
    int Int32,
    ulong UInt64);

public sealed record DistanceGroup(long Distance, int Count);

public sealed record CompressionValidationResult(
    long Offset,
    string Algorithm,
    string SignatureHex,
    bool IsValid,
    long CompressedBytesRead,
    long DecompressedBytes,
    string OutputPrefixHex,
    string? Error);
