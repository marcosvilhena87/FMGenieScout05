using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed partial class ContainerStructureDiagnostic
{
    private const int HeaderLength = 128;
    private const int EntropySampleLength = 1024 * 1024;
    private const int ContextBeforeLength = 64;
    private const int ContextAfterLength = 96;
    private const int MaximumCompressionCandidates = 400;
    private const long MaximumCompressedProbeLength = 32L * 1024 * 1024;
    private const long MaximumDecompressedProbeLength = 64L * 1024 * 1024;

    public async Task<ContainerDiagnosticReport> AnalyzeAsync(
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

        var info = new FileInfo(fullPath);
        progress?.Report("Calculando SHA-256...");
        string sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Lendo o save...");
        byte[] data = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        byte[] header = data[..Math.Min(HeaderLength, data.Length)];
        double entropy = CalculateEntropy(data.AsSpan(0, Math.Min(EntropySampleLength, data.Length)));

        progress?.Report("Localizando nomes de componentes...");
        IReadOnlyList<ComponentCandidate> components = FindComponents(data);

        progress?.Report("Agrupando distâncias entre componentes...");
        IReadOnlyList<DistanceGroup> distances = components
            .Where(item => item.DistanceToNext.HasValue)
            .GroupBy(item => item.DistanceToNext!.Value)
            .Select(group => new DistanceGroup(group.Key, group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Distance)
            .ToArray();

        progress?.Report("Validando candidatos Zlib/GZip...");
        IReadOnlyList<CompressionValidationResult> compression = ValidateCompressionCandidates(data, cancellationToken);

        progress?.Report("Diagnóstico concluído.");
        return new ContainerDiagnosticReport(
            fullPath,
            info.Length,
            sha256,
            Convert.ToHexString(header),
            entropy,
            components,
            distances,
            compression,
            DateTimeOffset.UtcNow);
    }

    public static string FormatReport(ContainerDiagnosticReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("FM Genie Scout 2005 — ContainerStructureDiagnostic 0.0.2");
        builder.AppendLine(new string('=', 74));
        builder.AppendLine($"Arquivo: {report.FilePath}");
        builder.AppendLine($"Tamanho: {report.FileSize.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} bytes");
        builder.AppendLine($"SHA-256: {report.Sha256}");
        builder.AppendLine($"Analisado em UTC: {report.AnalyzedAtUtc:O}");
        builder.AppendLine($"Entropia (amostra): {report.SampleEntropy:F4} bits/byte");
        builder.AppendLine($"Cabeçalho ({report.HeaderHex.Length / 2} bytes): {report.HeaderHex}");
        builder.AppendLine();

        builder.AppendLine($"Componentes candidatos: {report.Components.Count}");
        builder.AppendLine("Idx | Offset nome | Offset ext. | Distância | Nome");
        builder.AppendLine(new string('-', 74));
        foreach (ComponentCandidate item in report.Components)
        {
            string extOffset = item.ExtensionOffset.HasValue ? $"0x{item.ExtensionOffset.Value:X8}" : "----------";
            string distance = item.DistanceToNext.HasValue ? $"0x{item.DistanceToNext.Value:X8}" : "----------";
            builder.AppendLine($"{item.Index,3} | 0x{item.NameOffset:X8} | {extOffset} | {distance} | {item.FullName}");
        }

        builder.AppendLine();
        builder.AppendLine("Distâncias mais frequentes entre nomes:");
        if (report.DistanceGroups.Count == 0)
        {
            builder.AppendLine("  Nenhuma distância disponível.");
        }
        else
        {
            foreach (DistanceGroup group in report.DistanceGroups.Take(40))
            {
                builder.AppendLine($"  0x{group.Distance:X8} ({group.Distance,10} bytes) | ocorrências={group.Count}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Contexto estrutural por componente:");
        foreach (ComponentCandidate item in report.Components)
        {
            builder.AppendLine();
            builder.AppendLine($"[{item.Index:000}] {item.FullName} | nome=0x{item.NameOffset:X8}");
            builder.AppendLine($"  Antes ({ContextBeforeLength} bytes): {item.ContextBeforeHex}");
            builder.AppendLine($"  Depois ({ContextAfterLength} bytes): {item.ContextAfterHex}");
            builder.AppendLine("  Inteiros little-endian antes do nome:");
            foreach (IntegerInterpretation value in item.IntegersBeforeName)
            {
                builder.AppendLine(
                    $"    rel={value.RelativeOffset,4} | u16={value.UInt16,5} | " +
                    $"u32={value.UInt32,10} | i32={value.Int32,11} | u64={value.UInt64,20}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Validação de compressão: {report.CompressionResults.Count} candidatos testados");
        builder.AppendLine("Offset     | Algoritmo | Estado   | Comp. lidos | Saída       | Prefixo/erro");
        builder.AppendLine(new string('-', 110));
        foreach (CompressionValidationResult result in report.CompressionResults)
        {
            string state = result.IsValid ? "VÁLIDO" : "inválido";
            string detail = result.IsValid ? result.OutputPrefixHex : result.Error ?? "erro desconhecido";
            builder.AppendLine(
                $"0x{result.Offset:X8} | {result.Algorithm,-9} | {state,-8} | " +
                $"{result.CompressedBytesRead,11} | {result.DecompressedBytes,11} | {detail}");
        }

        builder.AppendLine();
        builder.AppendLine("Resumo:");
        builder.AppendLine($"  Componentes detectados: {report.Components.Count}");
        builder.AppendLine($"  Fluxos comprimidos válidos: {report.CompressionResults.Count(item => item.IsValid)}");
        builder.AppendLine($"  Candidatos rejeitados: {report.CompressionResults.Count(item => !item.IsValid)}");
        return builder.ToString();
    }

    private static IReadOnlyList<ComponentCandidate> FindComponents(byte[] data)
    {
        var names = ExtractUtf16Runs(data)
            .Where(run => IsLikelyComponentBaseName(run.Value))
            .ToArray();
        var extensions = ExtractUtf16Runs(data)
            .Where(run => IsKnownExtension(run.Value))
            .ToArray();

        var items = new List<ComponentCandidate>(names.Length);
        for (int index = 0; index < names.Length; index++)
        {
            Utf16Run name = names[index];
            Utf16Run? extension = extensions
                .Where(ext => ext.Offset > name.Offset && ext.Offset - name.Offset <= 0x300)
                .OrderBy(ext => ext.Offset)
                .Cast<Utf16Run?>()
                .FirstOrDefault();

            long? nextOffset = index + 1 < names.Length ? names[index + 1].Offset : null;
            long? distance = nextOffset - name.Offset;
            string extText = extension?.Value ?? string.Empty;
            string fullName = extText.Length > 0 && !name.Value.EndsWith(extText, StringComparison.OrdinalIgnoreCase)
                ? name.Value + extText
                : name.Value;

            int beforeStart = Math.Max(0, (int)name.Offset - ContextBeforeLength);
            int beforeLength = (int)name.Offset - beforeStart;
            int afterStart = (int)name.Offset;
            int afterLength = Math.Min(ContextAfterLength, data.Length - afterStart);

            items.Add(new ComponentCandidate(
                index + 1,
                name.Value,
                extText,
                fullName,
                name.Offset,
                extension?.Offset,
                nextOffset,
                distance,
                Convert.ToHexString(data.AsSpan(beforeStart, beforeLength)),
                Convert.ToHexString(data.AsSpan(afterStart, afterLength)),
                InterpretIntegersBefore(data, (int)name.Offset)));
        }

        return items;
    }

    private static IReadOnlyList<IntegerInterpretation> InterpretIntegersBefore(byte[] data, int nameOffset)
    {
        var values = new List<IntegerInterpretation>();
        for (int relative = -32; relative <= -8; relative += 4)
        {
            int position = nameOffset + relative;
            if (position < 0 || position + 8 > data.Length) continue;
            ReadOnlySpan<byte> span = data.AsSpan(position, 8);
            values.Add(new IntegerInterpretation(
                relative,
                BinaryPrimitives.ReadUInt16LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span),
                BinaryPrimitives.ReadInt32LittleEndian(span),
                BinaryPrimitives.ReadUInt64LittleEndian(span)));
        }
        return values;
    }

    private static IReadOnlyList<Utf16Run> ExtractUtf16Runs(byte[] data)
    {
        var result = new List<Utf16Run>();
        for (int parity = 0; parity <= 1; parity++)
        {
            int start = -1;
            var chars = new StringBuilder();
            for (int index = parity; index + 1 < data.Length; index += 2)
            {
                byte low = data[index];
                byte high = data[index + 1];
                bool valid = high == 0 && IsComponentCharacter((char)low);
                if (valid)
                {
                    if (start < 0) start = index;
                    chars.Append((char)low);
                }
                else
                {
                    FlushRun(result, start, chars);
                    start = -1;
                    chars.Clear();
                }
            }
            FlushRun(result, start, chars);
        }

        return result
            .GroupBy(run => run.Offset)
            .Select(group => group.OrderByDescending(run => run.Value.Length).First())
            .OrderBy(run => run.Offset)
            .ToArray();
    }

    private static void FlushRun(List<Utf16Run> result, int start, StringBuilder chars)
    {
        if (start >= 0 && chars.Length is >= 3 and <= 96)
        {
            result.Add(new Utf16Run(start, chars.ToString()));
        }
    }

    private static bool IsComponentCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.';

    private static bool IsLikelyComponentBaseName(string value)
    {
        if (IsKnownExtension(value)) return false;
        if (!ComponentNameRegex().IsMatch(value)) return false;
        return value.StartsWith("comp_", StringComparison.OrdinalIgnoreCase)
            || value.Contains('_')
            || KnownStandaloneNames.Contains(value);
    }

    private static bool IsKnownExtension(string value) =>
        value.Equals(".dat", StringComparison.OrdinalIgnoreCase)
        || value.Equals(".cmt", StringComparison.OrdinalIgnoreCase)
        || value.Equals(".sav", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownStandaloneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgman", "reserves", "inter", "wwclub", "awol", "dispute", "stadium"
    };

    private static IReadOnlyList<CompressionValidationResult> ValidateCompressionCandidates(
        byte[] data,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(long Offset, string Algorithm, int HeaderLength)>();
        for (int index = 0; index + 1 < data.Length && candidates.Count < MaximumCompressionCandidates; index++)
        {
            if (data[index] == 0x1F && data[index + 1] == 0x8B)
            {
                candidates.Add((index, "GZip", 2));
                continue;
            }

            if (data[index] == 0x78 && data[index + 1] is 0x01 or 0x5E or 0x9C or 0xDA)
            {
                int cmfFlg = (data[index] << 8) | data[index + 1];
                if (cmfFlg % 31 == 0)
                {
                    candidates.Add((index, "Zlib", 2));
                }
            }
        }

        var results = new List<CompressionValidationResult>(candidates.Count);
        foreach ((long offset, string algorithm, int headerLength) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(TryDecompress(data, offset, algorithm, headerLength));
        }
        return results;
    }

    private static CompressionValidationResult TryDecompress(byte[] data, long offset, string algorithm, int headerLength)
    {
        string signature = Convert.ToHexString(data.AsSpan((int)offset, Math.Min(headerLength, data.Length - (int)offset)));
        long available = Math.Min(data.Length - offset, MaximumCompressedProbeLength);
        using var source = new MemoryStream(data, (int)offset, (int)available, writable: false, publiclyVisible: true);
        try
        {
            using Stream decompressor = algorithm == "GZip"
                ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true)
                : new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);

            byte[] buffer = new byte[64 * 1024];
            byte[] prefix = new byte[64];
            int prefixLength = 0;
            long outputLength = 0;
            while (true)
            {
                int read = decompressor.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (prefixLength < prefix.Length)
                {
                    int copy = Math.Min(read, prefix.Length - prefixLength);
                    Buffer.BlockCopy(buffer, 0, prefix, prefixLength, copy);
                    prefixLength += copy;
                }
                outputLength += read;
                if (outputLength > MaximumDecompressedProbeLength)
                {
                    throw new InvalidDataException("Saída excedeu o limite de segurança de 64 MiB.");
                }
            }

            return new CompressionValidationResult(
                offset,
                algorithm,
                signature,
                true,
                source.Position,
                outputLength,
                Convert.ToHexString(prefix.AsSpan(0, prefixLength)),
                null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new CompressionValidationResult(
                offset,
                algorithm,
                signature,
                false,
                source.Position,
                0,
                string.Empty,
                exception.Message.Replace(Environment.NewLine, " "));
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private sealed record Utf16Run(long Offset, string Value);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{2,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentNameRegex();
}
