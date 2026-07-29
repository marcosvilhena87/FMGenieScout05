namespace FMGenieScout2005.Core.Models;

public sealed record ClubStringEntry(long LengthOffset, long TextOffset, int Length, string Text, long EndOffset);
public sealed record ClubRecordCandidate(int Row, string FullName, string ShortName, long FullLengthOffset, long ShortLengthOffset, long EstimatedStart, long EstimatedEnd, long EstimatedSize, long? SignatureOffset, int SignatureDistance, string Confidence, string BeforeHex, string AfterHex);
public sealed record ClubSignatureCandidate(string SignatureHex, int Length, int Occurrences, string ClubExamples);
public sealed record ClubFieldValue(int ClubRow, string ClubName, int RelativeOffset, uint UInt32, int Int32, ushort UInt16A, ushort UInt16B);
public sealed record ClubRecordBoundaryReport(string SourceFile, long SourceSize, string Sha256, IReadOnlyList<ClubRecordCandidate> Clubs, IReadOnlyList<ClubSignatureCandidate> Signatures, IReadOnlyList<ClubFieldValue> Fields, string OutputDirectory, DateTimeOffset AnalyzedAtUtc);
