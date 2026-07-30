FM Genie Scout 2005 — versão 0.0.25
===================================

Objetivo
--------
Localizar a estrutura dinâmica que liga uma pessoa ao contrato e ao clube atual,
comparando jogadores do Flamengo e do Corinthians.

Mudança de hipótese
-------------------
O campo encontrado 17 bytes antes do PersonDatabaseId foi rejeitado como clube
atual, clube anterior e referência confiável de carreira. A versão 0.0.25 não
assume um offset fixo e exclui o -17 dos resumos principais.

Amostras
--------
- 9 jogadores do Flamengo (ClubDatabaseId 322)
- 9 jogadores do Corinthians (ClubDatabaseId 319)

O diagnóstico resolve automaticamente o SaveIndex dos dois clubes no save.

Busca
-----
Para cada jogador, são localizadas todas as ocorrências UInt32 de:
- PersonDatabaseId
- InternalPersonId

Em torno de cada ocorrência, o diagnóstico procura o SaveIndex do clube atual
esperado em uma janela de +/- 4096 bytes, como UInt16 e UInt32.

Arquivos gerados
----------------
- current-contract-reference-report.txt
- current-contract-occurrences.csv
- current-club-reference-hits.csv
- current-club-offset-summaries.csv
- current-club-contexts.txt
- players-without-current-club-pattern.txt

Arquivo principal
-----------------
current-club-offset-summaries.csv

Critério forte
--------------
O mesmo tipo de referência, largura e offset relativo deve aparecer para pelo
menos 5 jogadores do Flamengo e 5 jogadores do Corinthians.

Segurança
---------
Somente leitura. O save de origem não é modificado.
