namespace FMGenieScout2005.Core.Domain;

public sealed record Club(
    uint DatabaseId,
    uint SaveIndex,
    string FullName,
    string? ShortName,
    long HeaderOffset,
    uint Field1,
    int Score,
    string Confidence);
