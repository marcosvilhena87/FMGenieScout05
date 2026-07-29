using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class SaveGameDiagnostic
{
    private const int HeaderLength = 128;
    private const int EntropySampleLength = 1024 * 1024;
    private const int MaximumTextCandidates = 200;

    private static readonly (string Name, byte[] Bytes)[] KnownPatterns =
    [
        ("ZIP local header", [0x50, 0x4B, 0x03, 0x04]),
        ("GZip", [0x1F, 0x8B]),
        ("Zlib 78 01", [0x78, 0x01]),
        ("Zlib 78 5E", [0x78, 0x5E]),
        ("Zlib 78 9C", [0x78, 0x9C]),
        ("Zlib 78 DA", [0x78, 0xDA]),
        ("BZip2", Encoding.ASCII.GetBytes("BZh")),
        ("7-Zip", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
        ("RAR", Encoding.ASCII.GetBytes("Rar!")),
        ("SQLite", Encoding.ASCII.GetBytes("SQLite format 3"))
    ];

    public async Task<SaveDiagnosticReport> AnalyzeAsync(
        string filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("O save selecionado não existe.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        progress?.Report("Calculando SHA-256...");
        string sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Lendo cabeçalho...");
        byte[] header = await ReadPrefixAsync(fullPath, HeaderLength, cancellationToken).ConfigureAwait(false);

        progress?.Report("Calculando entropia da amostra...");
        byte[] sample = await ReadPrefixAsync(fullPath, EntropySampleLength, cancellationToken).ConfigureAwait(false);
        double entropy = CalculateEntropy(sample);

        progress?.Report("Procurando assinaturas conhecidas...");
        IReadOnlyList<BytePatternHit> hits = await FindPatternsAsync(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Extraindo candidatos de texto...");
        IReadOnlyList<TextCandidate> strings = await ExtractTextCandidatesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Diagnóstico concluído.");
        return new SaveDiagnosticReport(
            fullPath,
            fileInfo.Length,
            sha256,
            Convert.ToHexString(header),
            entropy,
            hits,
            strings,
            DateTimeOffset.UtcNow);
    }

    public static string FormatReport(SaveDiagnosticReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("FM Genie Scout 2005 — SaveGameDiagnostic 0.0.1");
        builder.AppendLine(new string('=', 62));
        builder.AppendLine($"Arquivo: {report.FilePath}");
        builder.AppendLine($"Tamanho: {report.FileSize:N0} bytes");
        builder.AppendLine($"SHA-256: {report.Sha256}");
        builder.AppendLine($"Analisado em UTC: {report.AnalyzedAtUtc:O}");
        builder.AppendLine($"Entropia (amostra): {report.SampleEntropy:F4} bits/byte");
        builder.AppendLine($"Cabeçalho ({report.HeaderHex.Length / 2} bytes): {report.HeaderHex}");
        builder.AppendLine();
        builder.AppendLine("Assinaturas encontradas:");
        if (report.PatternHits.Count == 0)
        {
            builder.AppendLine("  Nenhuma assinatura comum encontrada.");
        }
        else
        {
            foreach (BytePatternHit hit in report.PatternHits)
            {
                builder.AppendLine($"  0x{hit.Offset:X8} | {hit.Name} | {hit.SignatureHex}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Candidatos de texto:");
        if (report.TextCandidates.Count == 0)
        {
            builder.AppendLine("  Nenhum texto candidato encontrado.");
        }
        else
        {
            foreach (TextCandidate candidate in report.TextCandidates)
            {
                builder.AppendLine($"  0x{candidate.Offset:X8} | {candidate.Encoding,-8} | {candidate.Value}");
            }
        }

        return builder.ToString();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadPrefixAsync(string path, int maximumLength, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        int length = (int)Math.Min(stream.Length, maximumLength);
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (byte value in data) counts[value]++;

        double entropy = 0;
        foreach (int count in counts)
        {
            if (count == 0) continue;
            double probability = (double)count / data.Length;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }

    private static async Task<IReadOnlyList<BytePatternHit>> FindPatternsAsync(string path, CancellationToken cancellationToken)
    {
        var hits = new List<BytePatternHit>();
        const int bufferSize = 1024 * 1024;
        int overlap = KnownPatterns.Max(item => item.Bytes.Length) - 1;
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize + overlap);
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
            int carry = 0;
            long absoluteStart = 0;
            while (true)
            {
                int read = await stream.ReadAsync(rented.AsMemory(carry, bufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                int available = carry + read;
                ReadOnlySpan<byte> span = rented.AsSpan(0, available);

                foreach ((string name, byte[] pattern) in KnownPatterns)
                {
                    int searchFrom = 0;
                    while (searchFrom <= available - pattern.Length && hits.Count < 100)
                    {
                        int relative = span[searchFrom..].IndexOf(pattern);
                        if (relative < 0) break;
                        int index = searchFrom + relative;
                        long offset = absoluteStart - carry + index;
                        hits.Add(new BytePatternHit(name, offset, Convert.ToHexString(pattern)));
                        searchFrom = index + 1;
                    }
                }

                carry = Math.Min(overlap, available);
                span[(available - carry)..].CopyTo(rented);
                absoluteStart += read;
                if (hits.Count >= 100) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return hits.OrderBy(hit => hit.Offset).ToArray();
    }

    private static async Task<IReadOnlyList<TextCandidate>> ExtractTextCandidatesAsync(string path, CancellationToken cancellationToken)
    {
        byte[] data = await ReadPrefixAsync(path, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var values = new List<TextCandidate>();
        ExtractAscii(data, values);
        ExtractUtf16LittleEndian(data, values);
        return values.OrderBy(candidate => candidate.Offset).Take(MaximumTextCandidates).ToArray();
    }

    private static void ExtractAscii(ReadOnlySpan<byte> data, List<TextCandidate> output)
    {
        int start = -1;
        for (int index = 0; index <= data.Length; index++)
        {
            bool printable = index < data.Length && data[index] is >= 32 and <= 126;
            if (printable && start < 0) start = index;
            if ((!printable || index == data.Length) && start >= 0)
            {
                int length = index - start;
                if (length >= 6)
                {
                    string value = Encoding.ASCII.GetString(data.Slice(start, Math.Min(length, 120)));
                    output.Add(new TextCandidate(start, "ASCII", Sanitize(value)));
                }
                start = -1;
            }
        }
    }

    private static void ExtractUtf16LittleEndian(ReadOnlySpan<byte> data, List<TextCandidate> output)
    {
        for (int parity = 0; parity < 2; parity++)
        {
            int start = -1;
            int chars = 0;
            for (int index = parity; index + 1 <= data.Length; index += 2)
            {
                bool printable = index + 1 < data.Length && data[index] is >= 32 and <= 126 && data[index + 1] == 0;
                if (printable)
                {
                    if (start < 0) start = index;
                    chars++;
                    continue;
                }

                if (start >= 0 && chars >= 4)
                {
                    int byteLength = Math.Min(chars * 2, 240);
                    string value = Encoding.Unicode.GetString(data.Slice(start, byteLength));
                    output.Add(new TextCandidate(start, "UTF-16LE", Sanitize(value)));
                }
                start = -1;
                chars = 0;
            }
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Where(character => !char.IsControl(character))).Trim();
}
