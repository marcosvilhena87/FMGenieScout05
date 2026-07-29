using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FMGenieScout2005.Core.Domain;

namespace FMGenieScout2005.Core.Parsing;

/// <summary>
/// Parser de produção para os cabeçalhos de clubes do game_db.payload.bin do FM 2005.
/// Baseado na estrutura validada pelo GlobalClubHeaderDiagnostic 0.0.17.
/// </summary>
public sealed class Fm2005ClubParser
{
    private const int HeaderSize = 22;
    private const int NameSearchBefore = 1024;
    private const int MaxAcceptedNameDistance = 768;

    public Fm2005ClubParseResult Parse(ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length < HeaderSize)
            throw new ArgumentException("O payload é pequeno demais para conter cabeçalhos de clubes.", nameof(payload));

        // A busca de nomes usa Encoding.Unicode; manter uma cópia simplifica o parser e evita
        // referências a memória temporária quando ele for chamado pela interface gráfica.
        byte[] data = payload.ToArray();
        List<ClubCandidate> accepted = [];
        int examined = 0;

        for (int indexOffset = 0; indexOffset <= data.Length - HeaderSize; indexOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint saveIndex = ReadUInt32(data, indexOffset);
            uint clubId = ReadUInt32(data, indexOffset + 4);
            byte separator1 = data[indexOffset + 8];
            uint field1 = ReadUInt32(data, indexOffset + 9);
            uint field2 = ReadUInt32(data, indexOffset + 13);
            byte separator2 = data[indexOffset + 17];
            uint field3 = ReadUInt32(data, indexOffset + 18);

            if (separator1 != 0 || field2 != 255 || separator2 != 0 || field3 != 255)
                continue;

            examined++;
            if (saveIndex == 0 || saveIndex > 100_000 || clubId == 0 || clubId > 10_000_000)
                continue;

            NamePair? names = FindNearestNamePair(data, indexOffset);
            if (names is null)
                continue;

            int score = 45;
            if (clubId > saveIndex) score += 10;
            score += 30;
            if (names.Distance <= 256) score += 15;
            else if (names.Distance <= MaxAcceptedNameDistance) score += 8;
            if (names.FullName.Length >= 3) score += 5;

            if (score < 75)
                continue;

            accepted.Add(new ClubCandidate(
                new Club(clubId, saveIndex, names.FullName, names.ShortName, indexOffset,
                    field1, score, score >= 105 ? "HIGH" : "MEDIUM"),
                names.Distance));
        }

        Club[] unique = accepted
            .GroupBy(x => x.Club.DatabaseId)
            .Select(group => group
                .OrderByDescending(x => x.Club.Score)
                .ThenBy(x => x.NameDistance)
                .ThenBy(x => x.Club.HeaderOffset)
                .First()
                .Club)
            .OrderBy(x => x.SaveIndex)
            .ThenBy(x => x.DatabaseId)
            .ToArray();

        return new Fm2005ClubParseResult(
            unique,
            examined,
            accepted.Count,
            accepted.Count - unique.Length);
    }

    public async Task<Fm2005ClubParseResult> ParseFileAsync(
        string payloadFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadFile);
        string source = Path.GetFullPath(payloadFile);
        if (!File.Exists(source))
            throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);

        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        return Parse(data, cancellationToken);
    }

    private static NamePair? FindNearestNamePair(byte[] data, int headerOffset)
    {
        int start = Math.Max(0, headerOffset - NameSearchBefore);
        NamePair? best = null;

        for (int offset = start; offset <= headerOffset - 6; offset++)
        {
            if (!TryReadLengthPrefixedUtf16(data, offset, out string? first, out int next)) continue;
            if (!IsPlausibleName(first)) continue;

            string? second = null;
            int end = next;
            if (TryReadLengthPrefixedUtf16(data, next, out string? possibleSecond, out int next2)
                && IsPlausibleName(possibleSecond))
            {
                second = possibleSecond;
                end = next2;
            }

            int distance = headerOffset - end;
            if (distance < 0 || distance > MaxAcceptedNameDistance) continue;

            NamePair candidate = new(first!, second, distance);
            if (best is null
                || candidate.Distance < best.Distance
                || (candidate.Distance == best.Distance
                    && candidate.FullName.Length > best.FullName.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool TryReadLengthPrefixedUtf16(
        byte[] data,
        int offset,
        out string? value,
        out int nextOffset)
    {
        value = null;
        nextOffset = offset;
        if (offset < 0 || offset > data.Length - 8) return false;

        int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        if (length < 2 || length > 80) return false;

        int byteLength;
        try
        {
            byteLength = checked(length * 2);
        }
        catch (OverflowException)
        {
            return false;
        }

        int textOffset = offset + 4;
        int terminatorOffset = textOffset + byteLength;
        if (terminatorOffset + 2 > data.Length) return false;
        if (data[terminatorOffset] != 0 || data[terminatorOffset + 1] != 0) return false;

        string text = Encoding.Unicode.GetString(data, textOffset, byteLength);
        if (text.IndexOf('\0') >= 0) return false;

        value = text;
        nextOffset = terminatorOffset + 2;
        return true;
    }

    private static bool IsPlausibleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 80) return false;

        int letters = 0;
        foreach (char c in value)
        {
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (char.IsLetter(c)) letters++;
            else if (!(char.IsDigit(c) || char.IsWhiteSpace(c)
                || c is '.' or '-' or '\'' or '(' or ')' or '&')) return false;

            if (category is UnicodeCategory.Control
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse) return false;
        }

        return letters >= 2 && letters * 2 >= value.Length;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private sealed record NamePair(string FullName, string? ShortName, int Distance);
    private sealed record ClubCandidate(Club Club, int NameDistance);
}
