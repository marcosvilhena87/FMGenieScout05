FM Genie Scout 2005 — atualização 0.0.19
=======================================

Objetivo
--------
Converter a descoberta validada pelo GlobalClubHeaderDiagnostic 0.0.17 em um
parser reutilizável de produção para clubes do FM 2005.

Conteúdo
--------
1. Domain/Club.cs
2. Parsing/Fm2005ClubParser.cs
3. Parsing/Fm2005ClubParseResult.cs
4. Correção documentada para o erro CS8629 do MultiSaveClubIdentityDiagnostic.

Como aplicar sobre a 0.0.18
---------------------------
1. Copie a pasta src deste pacote sobre a pasta src da 0.0.18.
2. Aplique a alteração descrita em patches/MultiSaveClubIdentityDiagnostic-CS8629.patch.txt.
3. Altere <Version>0.0.18</Version> para <Version>0.0.19</Version> nos projetos Core e App.
4. Execute:

   dotnet clean
   dotnet build

Exemplo de uso
--------------

var parser = new Fm2005ClubParser();
Fm2005ClubParseResult result = await parser.ParseFileAsync("game_db.payload.bin");
Club? flamengo = result.FindByDatabaseId(322);

Console.WriteLine($"Clubes: {result.Clubs.Count}");
Console.WriteLine($"Flamengo: {flamengo?.FullName}");

Critérios esperados no save já validado
---------------------------------------
- Aproximadamente 1.820 clubes únicos.
- Flamengo com DatabaseId 322.
- Corinthians com DatabaseId 319.
- Palmeiras com DatabaseId 329.
- São Paulo com DatabaseId 337.

Observação
----------
Este pacote é um overlay de atualização. Ele preserva todos os diagnósticos da
0.0.18 e acrescenta o primeiro parser de domínio permanente do projeto.
