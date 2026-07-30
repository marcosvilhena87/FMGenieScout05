FM Genie Scout 2005 — atualização 0.0.20
=======================================

Objetivo
--------
Descobrir como o registro de um jogador aponta para seu clube no save do
Football Manager 2005.

Hipóteses comparadas
--------------------
1. O jogador guarda ClubDatabaseId = 322 para o Flamengo.
2. O jogador guarda o SaveClubIndex do Flamengo no save atual.

O diagnóstico não escolhe uma hipótese antecipadamente. Ele procura as duas
referências em torno dos nomes conhecidos e agrupa offsets relativos repetidos.

Pré-requisito
-------------
Aplicar primeiro o overlay 0.0.19, pois esta versão utiliza:

- Domain/Club.cs
- Parsing/Fm2005ClubParser.cs
- Parsing/Fm2005ClubParseResult.cs

Conteúdo novo
-------------
1. Diagnostics/PlayerClubReferenceDiagnostic.cs
2. Models/PlayerClubReferenceModels.cs
3. patches/App-integration-example.txt

Jogadores usados como amostras
------------------------------
Jean, Felipe, Athirson, Andrezinho, Dimba, Júlio César, André Bahia,
Bruno Santos, Ibson, Reginaldo Araújo, Anderson, Fabinho, Jonatas, Juliano,
Fabiano Eller, Douglas Silva, Da Silva, Saraiva, Nélio, Valentim, Diego,
Júlio César Moraes, Dill, Júnior Baiano, Roger e Zinho.

Como aplicar
------------
1. Copie a pasta src deste pacote sobre a pasta src da 0.0.19.
2. Altere <Version>0.0.19</Version> para <Version>0.0.20</Version> nos projetos
   Core e App.
3. Integre a chamada conforme patches/App-integration-example.txt.
4. Execute:

   dotnet clean
   dotnet build

5. Rode o diagnóstico usando o game_db.payload.bin do save exibido na imagem
   do elenco do Flamengo.

Arquivos produzidos
-------------------
- player-club-reference-report.txt
- player-name-occurrences.csv
- player-club-reference-hits.csv
- player-club-relative-offsets.csv
- player-club-top-contexts.txt
- players-not-found.txt

Como interpretar
----------------
O arquivo principal para o próximo passo é:

  player-club-relative-offsets.csv

Procure linhas classificadas como CANDIDATO_FORTE. Um mesmo tipo de referência
no mesmo offset relativo para vários jogadores é evidência de um campo real da
estrutura de jogador.

Cuidados
--------
- Nomes comuns, como Jean, Felipe, Diego e Anderson, podem ter muitas
  ocorrências falsas.
- O diagnóstico também busca UTF-8 para registrar possíveis ocorrências
  auxiliares, mas a estrutura principal do save tende a usar UTF-16LE.
- Nenhum arquivo de save é alterado.

Próximo passo esperado
----------------------
Depois de executar, compacte a pasta de saída e envie os resultados. A versão
0.0.21 deverá validar o melhor offset usando jogadores de outro clube e, se
possível, um segundo save.
