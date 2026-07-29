using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ContainerRecordExtractorDiagnostic
{
    private static readonly byte[] Marker = [0x15, 0xCD, 0x5B, 0x07, 0x02];
    private const int NameOffsetFromMarker = 5;
    private const int ExtensionOffsetFromName = 0x206;
    private const int MaximumBaseNameLength = 96;
    private const int MaximumExtensionLength = 8;
    private const int ContextLength = 48;

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dat", ".cmt", ".sav"
    };

    public async Task<ContainerRecordExtractionReport> AnalyzeAndExtractAsync(
        string filePath,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string fullPath = Path.GetFullPath(filePath);
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("O save selecionado não existe.", fullPath);
        }

        Directory.CreateDirectory(fullOutputDirectory);
        var info = new FileInfo(fullPath);

        progress?.Report("Calculando SHA-256...");
        string sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Lendo o save...");
        byte[] data = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        progress?.Report("Localizando marcadores estruturais...");
        var accepted = new List<RecordCandidate>();
        var rejected = new List<RejectedMarker>();

        foreach (int markerOffset in FindMarkerOffsets(data))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryParseRecordHeader(data, markerOffset, out RecordCandidate? candidate, out string reason))
            {
                accepted.Add(candidate!);
            }
            else
            {
                rejected.Add(new RejectedMarker(
                    markerOffset,
                    reason,
                    GetContextHex(data, markerOffset, ContextLength)));
            }
        }

        accepted.Sort((left, right) => left.MarkerOffset.CompareTo(right.MarkerOffset));
        progress?.Report($"Extraindo {accepted.Count} registros válidos...");

        var records = new List<ContainerRecord>(accepted.Count);
        for (int index = 0; index < accepted.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordCandidate item = accepted[index];
            long? nextMarkerOffset = index + 1 < accepted.Count ? accepted[index + 1].MarkerOffset : null;
            long endOffset = nextMarkerOffset ?? data.LongLength;
            long rawSize = endOffset - item.MarkerOffset;

            string safeName = SanitizeFileName(item.FullName);
            string outputFileName = $"{index + 1:0000}_{safeName}.raw.bin";
            string outputPath = Path.Combine(fullOutputDirectory, outputFileName);
            bool extracted = false;
            string? extractionError = null;

            try
            {
                if (rawSize <= 0 || rawSize > int.MaxValue)
                {
                    throw new InvalidDataException($"Tamanho bruto inválido: {rawSize} bytes.");
                }

                await using var output = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(
                    data.AsMemory(checked((int)item.MarkerOffset), checked((int)rawSize)),
                    cancellationToken).ConfigureAwait(false);
                extracted = true;
            }
            catch (Exception exception)
            {
                extractionError = exception.Message;
            }

            records.Add(new ContainerRecord(
                index + 1,
                item.MarkerOffset,
                item.NameOffset,
                item.ExtensionOffset,
                nextMarkerOffset,
                rawSize,
                item.BaseName,
                item.Extension,
                item.FullName,
                outputFileName,
                GetContextHex(data, item.MarkerOffset, 32),
                extracted,
                extractionError));

            if ((index + 1) % 10 == 0 || index + 1 == accepted.Count)
            {
                progress?.Report($"Extraídos {index + 1}/{accepted.Count} registros...");
            }
        }

        var report = new ContainerRecordExtractionReport(
            fullPath,
            info.Length,
            sha256,
            fullOutputDirectory,
            records,
            rejected,
            DateTimeOffset.UtcNow);

        progress?.Report("Gravando manifesto e relatório...");
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "manifest.csv"),
            FormatCsv(report),
            new UTF8Encoding(true),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "extraction-report.txt"),
            FormatReport(report),
            new UTF8Encoding(true),
            cancellationToken).ConfigureAwait(false);

        progress?.Report("Extração concluída.");
        return report;
    }

    public static string FormatReport(ContainerRecordExtractionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("FM Genie Scout 2005 — ContainerRecordExtractorDiagnostic 0.0.3");
        builder.AppendLine(new string('=', 79));
        builder.AppendLine($"Arquivo: {report.FilePath}");
        builder.AppendLine($"Tamanho: {report.FileSize.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} bytes");
        builder.AppendLine($"SHA-256: {report.Sha256}");
        builder.AppendLine($"Diretório de saída: {report.OutputDirectory}");
        builder.AppendLine($"Analisado em UTC: {report.AnalyzedAtUtc:O}");
        builder.AppendLine($"Marcador: {Convert.ToHexString(Marker)} (0x075BCD15 + tipo 02)");
        builder.AppendLine($"Registros válidos: {report.Records.Count}");
        builder.AppendLine($"Marcadores rejeitados: {report.RejectedMarkers.Count}");
        builder.AppendLine();
        builder.AppendLine("Idx | Marcador   | Nome       | Extensão   | Próximo    | Tamanho     | Estado | Componente");
        builder.AppendLine(new string('-', 118));

        foreach (ContainerRecord item in report.Records)
        {
            string next = item.NextMarkerOffset.HasValue ? $"0x{item.NextMarkerOffset.Value:X8}" : "fim arquivo";
            string state = item.Extracted ? "OK" : "ERRO";
            builder.AppendLine(
                $"{item.Index,3} | 0x{item.MarkerOffset:X8} | 0x{item.NameOffset:X8} | " +
                $"0x{item.ExtensionOffset:X8} | {next,-10} | {item.RawSize,11} | {state,-6} | {item.FullName}");
            if (!item.Extracted)
            {
                builder.AppendLine($"      Falha: {item.ExtractionError}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Arquivos especiais:");
        foreach (ContainerRecord item in report.Records.Where(IsImportantRecord))
        {
            builder.AppendLine(
                $"  {item.FullName,-32} marcador=0x{item.MarkerOffset:X8} tamanho={item.RawSize,11} arquivo={item.OutputFileName}");
        }

        builder.AppendLine();
        builder.AppendLine("Marcadores rejeitados:");
        if (report.RejectedMarkers.Count == 0)
        {
            builder.AppendLine("  Nenhum.");
        }
        else
        {
            foreach (RejectedMarker item in report.RejectedMarkers.Take(200))
            {
                builder.AppendLine($"  0x{item.MarkerOffset:X8} | {item.Reason} | {item.ContextHex}");
            }
            if (report.RejectedMarkers.Count > 200)
            {
                builder.AppendLine($"  ... {report.RejectedMarkers.Count - 200} rejeições adicionais omitidas.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Resumo:");
        builder.AppendLine($"  Registros extraídos: {report.Records.Count(item => item.Extracted)}");
        builder.AppendLine($"  Falhas de extração: {report.Records.Count(item => !item.Extracted)}");
        builder.AppendLine($"  Bytes delimitados: {report.Records.Sum(item => item.RawSize).ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))}");
        builder.AppendLine("  O save original não foi alterado.");
        return builder.ToString();
    }

    public static string FormatCsv(ContainerRecordExtractionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("index,marker_offset,name_offset,extension_offset,next_marker_offset,raw_size,base_name,extension,full_name,output_file,extracted,error");
        foreach (ContainerRecord item in report.Records)
        {
            builder.Append(item.Index).Append(',')
                .Append($"0x{item.MarkerOffset:X8}").Append(',')
                .Append($"0x{item.NameOffset:X8}").Append(',')
                .Append($"0x{item.ExtensionOffset:X8}").Append(',')
                .Append(item.NextMarkerOffset.HasValue ? $"0x{item.NextMarkerOffset.Value:X8}" : string.Empty).Append(',')
                .Append(item.RawSize).Append(',')
                .Append(Csv(item.BaseName)).Append(',')
                .Append(Csv(item.Extension)).Append(',')
                .Append(Csv(item.FullName)).Append(',')
                .Append(Csv(item.OutputFileName)).Append(',')
                .Append(item.Extracted ? "true" : "false").Append(',')
                .Append(Csv(item.ExtractionError ?? string.Empty))
                .AppendLine();
        }
        return builder.ToString();
    }

    private static bool TryParseRecordHeader(
        byte[] data,
        int markerOffset,
        out RecordCandidate? candidate,
        out string reason)
    {
        candidate = null;
        int nameOffset = markerOffset + NameOffsetFromMarker;
        int extensionOffset = nameOffset + ExtensionOffsetFromName;

        if (extensionOffset + 10 > data.Length)
        {
            reason = "Cabeçalho ultrapassa o fim do arquivo.";
            return false;
        }

        if (!TryReadUtf16NullTerminated(data, nameOffset, MaximumBaseNameLength, out string baseName))
        {
            reason = "Nome UTF-16LE ausente ou inválido.";
            return false;
        }

        if (!IsPlausibleBaseName(baseName))
        {
            reason = $"Nome implausível: '{baseName}'.";
            return false;
        }

        if (!TryReadUtf16NullTerminated(data, extensionOffset, MaximumExtensionLength, out string extension))
        {
            reason = "Extensão UTF-16LE ausente ou inválida em nome+0x206.";
            return false;
        }

        if (!KnownExtensions.Contains(extension))
        {
            reason = $"Extensão não reconhecida: '{extension}'.";
            return false;
        }

        string fullName = baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + extension;

        candidate = new RecordCandidate(
            markerOffset,
            nameOffset,
            extensionOffset,
            baseName,
            extension,
            fullName);
        reason = string.Empty;
        return true;
    }

    private static IEnumerable<int> FindMarkerOffsets(byte[] data)
    {
        for (int index = 0; index <= data.Length - Marker.Length; index++)
        {
            if (data[index] != Marker[0]) continue;
            bool matches = true;
            for (int markerIndex = 1; markerIndex < Marker.Length; markerIndex++)
            {
                if (data[index + markerIndex] == Marker[markerIndex]) continue;
                matches = false;
                break;
            }
            if (matches)
            {
                yield return index;
                index += Marker.Length - 1;
            }
        }
    }

    private static bool TryReadUtf16NullTerminated(
        byte[] data,
        int offset,
        int maximumCharacters,
        out string value)
    {
        value = string.Empty;
        if (offset < 0 || offset + 1 >= data.Length) return false;

        var builder = new StringBuilder();
        for (int characterIndex = 0; characterIndex < maximumCharacters; characterIndex++)
        {
            int position = offset + characterIndex * 2;
            if (position + 1 >= data.Length) return false;
            ushort code = (ushort)(data[position] | data[position + 1] << 8);
            if (code == 0)
            {
                value = builder.ToString();
                return value.Length > 0;
            }
            if (code is < 0x20 or > 0x7E) return false;
            builder.Append((char)code);
        }
        return false;
    }

    private static bool IsPlausibleBaseName(string value)
    {
        if (value.Length is < 3 or > MaximumBaseNameLength) return false;
        foreach (char character in value)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static string GetContextHex(byte[] data, int offset, int length)
    {
        int start = Math.Max(0, offset);
        int available = Math.Min(length, data.Length - start);
        return available > 0 ? Convert.ToHexString(data.AsSpan(start, available)) : string.Empty;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }
        string result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "component" : result;
    }

    private static bool IsImportantRecord(ContainerRecord item) =>
        item.FullName.Equals("game_db.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("player_stats.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("person_record_manager.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("contract_man.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("squad_man.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("training_man.dat", StringComparison.OrdinalIgnoreCase) ||
        item.FullName.Equals("transfer_man.dat", StringComparison.OrdinalIgnoreCase);

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record RecordCandidate(
        int MarkerOffset,
        int NameOffset,
        int ExtensionOffset,
        string BaseName,
        string Extension,
        string FullName);
}
