FM Genie Scout 2005 — versão 0.0.24
===================================

Objetivo
--------
Validar em múltiplos clubes a hipótese descoberta na 0.0.23:

  ClubSaveIndex = UInt32 em PersonDatabaseIdOffset - 17

Amostras
--------
- 9 jogadores conhecidos do CR Flamengo (ClubDatabaseId 322)
- 9 jogadores conhecidos do SC Corinthians Paulista (ClubDatabaseId 319)

O diagnóstico localiza automaticamente o SaveClubIndex dos dois clubes, procura todas
as ocorrências globais dos PersonDatabaseIds e resolve o UInt32 existente 17 bytes antes.

Classificações
--------------
- PLAYER_RECORD_CONFIRMADO
- PLAYER_RECORD_OUTRO_CLUBE
- CLUB_INDEX_INVALIDO
- TABELA_DE_IDENTIDADE

Arquivos produzidos
-------------------
- player-record-structure-report.txt
- player-record-candidates.csv
- player-club-resolution.csv
- player-record-contexts.txt
- player-record-variant-candidates.csv
- people-not-resolved.txt

Critério forte
--------------
Pelo menos 6 jogadores confirmados no offset -17 para cada um dos dois clubes.

Nenhum arquivo do save é alterado.
