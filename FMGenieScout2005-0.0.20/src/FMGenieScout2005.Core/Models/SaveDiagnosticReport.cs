namespace FMGenieScout2005.Core.Models;

public sealed record SaveDiagnosticReport(
    string FilePath,
    long FileSize,
    string Sha256,
    string HeaderHex,
    double SampleEntropy,
    IReadOnlyList<BytePatternHit> PatternHits,
    IReadOnlyList<TextCandidate> TextCandidates,
    DateTimeOffset AnalyzedAtUtc);

public sealed record BytePatternHit(string Name, long Offset, string SignatureHex);
public sealed record TextCandidate(long Offset, string Encoding, string Value);
