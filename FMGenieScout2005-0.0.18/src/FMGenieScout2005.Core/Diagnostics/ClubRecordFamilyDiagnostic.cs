using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FMGenieScout2005.Core.Models;

namespace FMGenieScout2005.Core.Diagnostics;

public sealed class ClubRecordFamilyDiagnostic
{
    private const int WindowBefore = 256;
    private const int WindowAfter = 512;

    private static readonly (string Name, int Division, uint Id)[] Clubs =
    [
        ("Atlético Paranaense",1,107206),("Atlético Mineiro",1,314),("Botafogo",1,316),("Corinthians",1,319),
        ("Coritiba",1,104776),("Criciúma",1,320),("Cruzeiro",1,321),("Figueirense",1,301306),
        ("Flamengo",1,322),("Fluminense",1,323),("Goiás",1,102555),("Grêmio",1,324),("Guarani",1,325),
        ("Internacional",1,326),("Juventude",1,327),("Palmeiras",1,329),("Paraná",1,330),("Paysandu",1,331),
        ("Ponte Preta",1,332),("Santos",1,335),("São Caetano",1,301354),("São Paulo",1,337),("Vasco",1,339),("Vitória",1,313273),
        ("América (MG)",2,107201),("América (RN)",2,107203),("Anapolina",2,301146),("Avaí",2,107208),
        ("Bahia",2,315),("Brasiliense",2,309670),("CRB",2,301102),("Caxias",2,301266),("Ceará",2,104749),
        ("Fortaleza",2,104750),("Ituano",2,107216),("Joinville",2,301310),("Londrina",2,900678),("Marília",2,311026),
        ("Mogi Mirim",2,107222),("Náutico",2,328),("Paulista",2,301338),("Portuguesa",2,333),("Sport Recife",2,338),
        ("Remo",2,334),("Santa Cruz",2,107232),("Santo André",2,301352),("São Raimundo (AM)",2,301111),("Vila Nova (GO)",2,311107)
    ];

    public async Task<ClubRecordFamilyReport> AnalyzeAsync(string inputFile, string outputDirectory,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(inputFile);
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo game_db.payload.bin não existe.", source);
        string output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        progress?.Report("Lendo game_db.payload.bin...");
        byte[] data = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        progress?.Report("Reutilizando o parser de ClubId...");
        var idDiagnostic = new ClubIdStructureDiagnostic();
        ClubIdStructureReport ids = await idDiagnostic.AnalyzeAsync(source, Path.Combine(output, "club-id-source"), progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("Associando os 48 clubes pelos IDs confirmados...");
        List<ClubFamilyMatch> matches = [];
        foreach ((string name, int division, uint expectedId) in Clubs)
        {
            ClubIdRecord? r = ids.Clubs.Where(x => x.ClubId == expectedId)
                .OrderByDescending(x => x.Confidence == "HIGH").ThenBy(x => x.IdBlockOffset).FirstOrDefault();
            matches.Add(r is null
                ? new(name, division, null, null, expectedId, null, null, null, null, null, null, Family(expectedId), "NAO_ENCONTRADO", "ID_EXATO")
                : new(name, division, r.FullName, r.ShortName, expectedId, r.ClubId, r.IdBlockOffset, r.LocalIndex,
                    r.Delta, r.IdRelativeToShortEnd, r.EstimatedSize, Family(r.ClubId), "ENCONTRADO", "ID_EXATO"));
        }

        List<ClubFamilySummary> families = matches.Where(x => x.Status == "ENCONTRADO")
            .GroupBy(x => x.Family).OrderBy(g => g.Min(x => x.FoundClubId ?? uint.MaxValue))
            .Select(g => new ClubFamilySummary(g.Key, g.Count(), g.Count(x => x.Division == 1), g.Count(x => x.Division == 2),
                g.Min(x => x.FoundClubId!.Value), g.Max(x => x.FoundClubId!.Value),
                g.Where(x => x.Delta.HasValue).Select(x => x.Delta!.Value).Distinct().Count(),
                Modes(g.Where(x => x.Delta.HasValue).Select(x => x.Delta!.Value)),
                Modes(g.Where(x => x.IdRelativeToShortEnd.HasValue).Select(x => x.IdRelativeToShortEnd!.Value)),
                string.Join(" | ", g.Take(8).Select(x => x.ExpectedDisplayName)))).ToList();

        progress?.Report("Comparando campos dentro de cada família estrutural...");
        List<FamilyFieldCandidate> candidates = [];
        foreach (IGrouping<string, ClubFamilyMatch> group in matches.Where(x => x.Status == "ENCONTRADO").GroupBy(x => x.Family))
        {
            ClubFamilyMatch[] a = group.Where(x => x.Division == 1).ToArray();
            ClubFamilyMatch[] b = group.Where(x => x.Division == 2).ToArray();
            if (a.Length < 2 || b.Length < 2) continue;
            for (int rel = -WindowBefore; rel <= WindowAfter; rel++)
            {
                AddCandidate<byte>(candidates, group.Key, "BYTE", rel, a, b, data, 1, ReadByte);
                if ((rel & 1) == 0) AddCandidate<ushort>(candidates, group.Key, "UINT16", rel, a, b, data, 2, ReadUInt16);
                if ((rel & 3) == 0) AddCandidate<uint>(candidates, group.Key, "UINT32", rel, a, b, data, 4, ReadUInt32);
            }
        }
        FamilyFieldCandidate[] ordered = candidates.OrderByDescending(x => x.SeparationScore)
            .ThenByDescending(x => Math.Min(x.SerieAModeRate, x.SerieBModeRate)).ThenBy(x => Math.Abs(x.RelativeOffset)).Take(2000).ToArray();

        List<MissingClubSearchHit> hits = [];
        foreach (ClubFamilyMatch m in matches.Where(x => x.Status != "ENCONTRADO"))
        {
            byte[] idBytes = BitConverter.GetBytes(m.ExpectedClubId);
            foreach (int p in FindAll(data, idBytes).Take(20)) hits.Add(new(m.ExpectedDisplayName, m.ExpectedClubId, "UINT32_ID", p, m.ExpectedClubId.ToString(CultureInfo.InvariantCulture), HexContext(data, p)));
            foreach (string term in SearchTerms(m.ExpectedDisplayName))
            {
                byte[] utf = Encoding.Unicode.GetBytes(term);
                foreach (int p in FindAll(data, utf).Take(10)) hits.Add(new(m.ExpectedDisplayName, m.ExpectedClubId, "UTF16_NAME", p, term, HexContext(data, p)));
            }
        }

        var report = new ClubRecordFamilyReport(source, data.LongLength, sha, matches, families, ordered, hits, output, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-family-report.txt"), FormatReport(report), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "club-record-families.csv"), FormatFamiliesCsv(report), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "family-field-candidates.csv"), FormatCandidatesCsv(report), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "family-contexts.txt"), FormatContexts(report, data), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "missing-club-search.csv"), FormatMissingCsv(report), new UTF8Encoding(true), cancellationToken);
        progress?.Report("Diagnóstico de famílias concluído.");
        return report;
    }

    private static string Family(uint id) => id switch
    {
        < 1000 => "LEGACY_000xxx",
        >= 100000 and < 103000 => "DB_102xxx",
        >= 103000 and < 106000 => "DB_104xxx",
        >= 106000 and < 109000 => "DB_107xxx",
        >= 300000 and < 302000 => "DB_301xxx",
        >= 309000 and < 310000 => "DB_309xxx",
        >= 311000 and < 312000 => "DB_311xxx",
        >= 313000 and < 314000 => "DB_313xxx",
        >= 900000 and < 901000 => "DB_900xxx",
        _ => $"DB_{id / 1000}xxx"
    };

    private delegate bool TryRead<T>(byte[] data, long offset, out T value) where T : struct;
    private static void AddCandidate<T>(List<FamilyFieldCandidate> output, string family, string type, int rel,
        IReadOnlyList<ClubFamilyMatch> aClubs, IReadOnlyList<ClubFamilyMatch> bClubs, byte[] data, int width, TryRead<T> reader)
        where T : struct, IEquatable<T>
    {
        List<T> a = Values(aClubs, data, rel, width, reader); List<T> b = Values(bClubs, data, rel, width, reader);
        if (a.Count < 2 || b.Count < 2) return;
        var am = Mode(a); var bm = Mode(b); double ar = am.Count / (double)a.Count; double br = bm.Count / (double)b.Count;
        bool distinct = !am.Value.Equals(bm.Value); double balance = Math.Min(a.Count / (double)aClubs.Count, b.Count / (double)bClubs.Count);
        double score = distinct ? ar * br * balance : 0;
        string assessment = !distinct ? "MESMO_MODO" : score >= .75 ? "SEPARACAO_FORTE" : score >= .45 ? "SEPARACAO_MEDIA" : "SEPARACAO_FRACA";
        output.Add(new(family, type, rel, a.Count, am.Value.ToString() ?? "", am.Count, ar, b.Count, bm.Value.ToString() ?? "", bm.Count, br, score, assessment));
    }
    private static List<T> Values<T>(IReadOnlyList<ClubFamilyMatch> clubs, byte[] data, int rel, int width, TryRead<T> reader) where T : struct
    { var list = new List<T>(); foreach (var c in clubs) { if (!c.IdBlockOffset.HasValue) continue; long p = c.IdBlockOffset.Value + rel; if (p < 0 || p + width > data.LongLength) continue; if (reader(data, p, out T v)) list.Add(v); } return list; }
    private static (T Value,int Count) Mode<T>(IEnumerable<T> values) where T : IEquatable<T> => values.GroupBy(x=>x).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.GetHashCode()).Select(g=>(g.Key,g.Count())).First();
    private static bool ReadByte(byte[] d,long p,out byte v){v=d[checked((int)p)];return true;}
    private static bool ReadUInt16(byte[] d,long p,out ushort v){v=BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(checked((int)p),2));return true;}
    private static bool ReadUInt32(byte[] d,long p,out uint v){v=BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(checked((int)p),4));return true;}
    private static string Modes(IEnumerable<int> values) => string.Join(" | ", values.GroupBy(x=>x).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).Take(5).Select(g=>$"{g.Key}:{g.Count()}"));
    private static IEnumerable<int> FindAll(byte[] data, byte[] pattern){ if(pattern.Length==0) yield break; for(int i=0;i<=data.Length-pattern.Length;i++){int j=0;for(;j<pattern.Length;j++)if(data[i+j]!=pattern[j])break;if(j==pattern.Length)yield return i;}}
    private static IEnumerable<string> SearchTerms(string name) => name switch { "Goiás" => ["Goiás","Goias","Goiás EC","Goiás E Clube"], "CRB" => ["CRB","Clube de Regatas Brasil","Regatas Brasil"], _ => [name] };
    private static string HexContext(byte[] data,int p){int s=Math.Max(0,p-16),n=Math.Min(64,data.Length-s);return Convert.ToHexString(data.AsSpan(s,n));}

    public static string FormatReport(ClubRecordFamilyReport r)
    {
        var sb=new StringBuilder(); sb.AppendLine("FM Genie Scout 2005 — ClubRecordFamilyDiagnostic 0.0.14"); sb.AppendLine(new string('=',92));
        sb.AppendLine($"Arquivo: {r.SourceFile}"); sb.AppendLine($"Tamanho: {r.SourceSize:N0} bytes"); sb.AppendLine($"SHA-256: {r.Sha256}");
        sb.AppendLine($"Série A encontrados: {r.ClubMatches.Count(x=>x.Division==1&&x.Status=="ENCONTRADO")}/24");
        sb.AppendLine($"Série B encontrados: {r.ClubMatches.Count(x=>x.Division==2&&x.Status=="ENCONTRADO")}/24");
        sb.AppendLine(); sb.AppendLine("Famílias estruturais:"); foreach(var f in r.Families) sb.AppendLine($"  {f.Family,-14} total={f.Total,2} A={f.SerieA,2} B={f.SerieB,2} ids={f.MinimumClubId}-{f.MaximumClubId} deltas=[{f.CommonDeltas}] distâncias=[{f.CommonDistances}] | {f.Examples}");
        sb.AppendLine(); sb.AppendLine("Melhores campos por família:"); foreach(var c in r.Candidates.Where(x=>x.Assessment!="MESMO_MODO").Take(40)) sb.AppendLine($"  {c.Family,-14} {c.DataType,-6} rel={c.RelativeOffset,5} | A={c.SerieAMode} ({c.SerieAModeRate:P1},n={c.SerieASamples}) B={c.SerieBMode} ({c.SerieBModeRate:P1},n={c.SerieBSamples}) score={c.SeparationScore:F3} {c.Assessment}");
        sb.AppendLine(); sb.AppendLine("Não encontrados:"); foreach(var m in r.ClubMatches.Where(x=>x.Status!="ENCONTRADO")) sb.AppendLine($"  Série {m.Division}: {m.ExpectedDisplayName} id={m.ExpectedClubId}");
        sb.AppendLine(); sb.AppendLine("Arquivos gerados: club-record-family-report.txt, club-record-families.csv, family-field-candidates.csv, family-contexts.txt, missing-club-search.csv e club-id-source/."); sb.AppendLine("O arquivo de origem não foi modificado."); return sb.ToString();
    }
    private static string FormatFamiliesCsv(ClubRecordFamilyReport r){var sb=new StringBuilder("expected_name,division,full_name,short_name,expected_club_id,found_club_id,id_block_offset,local_index,delta,id_relative_to_short_end,estimated_size,family,status,match_method\r\n");foreach(var m in r.ClubMatches)sb.AppendLine($"{Csv(m.ExpectedDisplayName)},{m.Division},{Csv(m.FullName??"")},{Csv(m.ShortName??"")},{m.ExpectedClubId},{m.FoundClubId?.ToString()??""},{(m.IdBlockOffset.HasValue?$"0x{m.IdBlockOffset:X8}":"")},{m.LocalIndex?.ToString()??""},{m.Delta?.ToString()??""},{m.IdRelativeToShortEnd?.ToString()??""},{m.EstimatedSize?.ToString()??""},{m.Family},{m.Status},{m.MatchMethod}");return sb.ToString();}
    private static string FormatCandidatesCsv(ClubRecordFamilyReport r){var sb=new StringBuilder("family,data_type,relative_offset,serie_a_samples,serie_a_mode,serie_a_mode_count,serie_a_mode_rate,serie_b_samples,serie_b_mode,serie_b_mode_count,serie_b_mode_rate,separation_score,assessment\r\n");foreach(var c in r.Candidates)sb.AppendLine($"{c.Family},{c.DataType},{c.RelativeOffset},{c.SerieASamples},{c.SerieAMode},{c.SerieAModeCount},{c.SerieAModeRate.ToString("F6",CultureInfo.InvariantCulture)},{c.SerieBSamples},{c.SerieBMode},{c.SerieBModeCount},{c.SerieBModeRate.ToString("F6",CultureInfo.InvariantCulture)},{c.SeparationScore.ToString("F6",CultureInfo.InvariantCulture)},{c.Assessment}");return sb.ToString();}
    private static string FormatContexts(ClubRecordFamilyReport r,byte[] data){var sb=new StringBuilder();foreach(var c in r.Candidates.Where(x=>x.Assessment!="MESMO_MODO").Take(25)){sb.AppendLine($"[{c.Family} {c.DataType} rel={c.RelativeOffset}] A={c.SerieAMode} B={c.SerieBMode} score={c.SeparationScore:F3}");foreach(var m in r.ClubMatches.Where(x=>x.Family==c.Family&&x.Status=="ENCONTRADO")){long p=m.IdBlockOffset!.Value+c.RelativeOffset;if(p<0||p+16>data.LongLength)continue;sb.AppendLine($"  S{m.Division} {m.ExpectedDisplayName,-24} id={m.FoundClubId,6} offset=0x{p:X8} bytes={Convert.ToHexString(data.AsSpan((int)p,16))}");}sb.AppendLine();}return sb.ToString();}
    private static string FormatMissingCsv(ClubRecordFamilyReport r){var sb=new StringBuilder("club,expected_club_id,search_kind,offset,value,context_hex\r\n");foreach(var h in r.MissingSearchHits)sb.AppendLine($"{Csv(h.Club)},{h.ExpectedClubId},{h.SearchKind},0x{h.Offset:X8},{Csv(h.Value)},{h.ContextHex}");return sb.ToString();}
    private static string Csv(string s)=>'"'+s.Replace("\"","\"\"")+'"';
}
