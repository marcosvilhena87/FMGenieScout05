namespace FMGenieScout2005.Core.Models;

public sealed record MultiSaveClubIdentityRecord(
    uint ClubId,
    string? Save1FullName,
    string? Save2FullName,
    string? Save1ShortName,
    string? Save2ShortName,
    uint? Save1Index,
    uint? Save2Index,
    long? Save1Offset,
    long? Save2Offset,
    bool PresentInSave1,
    bool PresentInSave2,
    bool DatabaseIdStable,
    bool SaveIndexStable,
    bool FullNameEquivalent,
    bool ShortNameEquivalent,
    long? IndexDelta,
    string Status);

public sealed record MultiSaveClubIdentityReport(
    string Save1File,
    string Save2File,
    string Save1Sha256,
    string Save2Sha256,
    IReadOnlyList<MultiSaveClubIdentityRecord> Clubs,
    int Save1ClubCount,
    int Save2ClubCount,
    int SharedClubCount,
    int OnlySave1Count,
    int OnlySave2Count,
    int ChangedIndexCount,
    int StableIndexCount,
    int NameMismatchCount,
    string OutputDirectory,
    DateTimeOffset AnalyzedAtUtc);
