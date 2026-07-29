using FMGenieScout2005.Core.Domain;

namespace FMGenieScout2005.Core.Parsing;

public sealed record Fm2005ClubParseResult(
    IReadOnlyList<Club> Clubs,
    int CandidatesExamined,
    int CandidatesAccepted,
    int DuplicateDatabaseIdsRemoved)
{
    public Club? FindByDatabaseId(uint databaseId) =>
        Clubs.FirstOrDefault(x => x.DatabaseId == databaseId);

    public Club? FindBySaveIndex(uint saveIndex) =>
        Clubs.FirstOrDefault(x => x.SaveIndex == saveIndex);

    public IReadOnlyList<Club> SearchByName(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Clubs
            .Where(x => x.FullName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (x.ShortName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
