FM Genie Scout 2005 — versão 0.0.23
=======================================

Objetivo
--------
Localizar globalmente todas as ocorrências do DatabaseId e do identificador
interno dos nove jogadores conhecidos do Flamengo, procurando referências ao
clube em uma janela de ±256 bytes.

Hipóteses comparadas
--------------------
- outras estruturas referenciam a pessoa pelo DatabaseId;
- outras estruturas referenciam a pessoa pelo InternalPersonId/SequenceIndex;
- o vínculo com o clube usa ClubDatabaseId 322;
- o vínculo usa o SaveClubIndex do Flamengo no save atual.

Arquivos produzidos
-------------------
- person-reference-report.txt
- person-reference-occurrences.csv
- person-reference-club-hits.csv
- person-reference-offset-summaries.csv
- person-reference-contexts.txt
- people-not-resolved.txt

Arquivo principal
-----------------
person-reference-offset-summaries.csv

Procure CANDIDATO_FORTE ou CANDIDATO_MEDIO com o mesmo tipo de referência e
o mesmo offset relativo para vários jogadores. O diagnóstico ignora como
conclusão automática padrões que cobrem apenas uma pessoa.

Como executar
-------------
1. Abra a solução e compile.
2. Selecione o game_db.payload.bin do save do Flamengo.
3. Clique em "Localizar referências globais da pessoa...".
4. Escolha a pasta de saída.
5. Compacte a pasta FMGenieScout2005-person-references-* e envie os resultados.

Segurança
---------
Somente leitura. O save não é modificado.
