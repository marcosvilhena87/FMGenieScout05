FM Genie Scout 2005 — 0.0.21
================================

Objetivo
--------
Descobrir a identidade estrutural de nove jogadores conhecidos usando seus IDs reais.

Jogadores e IDs
----------------
Andrezinho 302728; André Bahia 311169; Bruno Santos 8832815; Dimba 303048;
Douglas Silva 3300410; Fabiano Eller 3300219; Júlio César Moraes 8825041;
Júnior Baiano 6900111; Reginaldo Araújo 3300780.

O diagnóstico procura os nomes em UTF-16LE, busca cada ID em torno do nome e
compara campos BYTE/UINT16/UINT32 em offsets relativos de -128 a +256 bytes.

Arquivos produzidos
-------------------
player-identity-layout-report.txt
player-identity-name-hits.csv
player-identity-id-hits.csv
player-identity-offset-summaries.csv
player-identity-field-candidates.csv
player-name-structure-contexts.txt

O arquivo principal é player-identity-layout-report.txt. Nenhum save é alterado.
