FM Genie Scout 2005 — versão 0.0.20 integrada
================================================

Esta distribuição contém o projeto completo, com a interface ligada diretamente
ao PlayerClubReferenceDiagnostic.

O que foi corrigido
-------------------
- MainForm não executa mais o MultiSaveClubIdentityDiagnostic por padrão.
- A interface solicita somente um game_db.payload.bin.
- O botão principal executa PlayerClubReferenceDiagnostic.
- A pasta de saída usa o prefixo FMGenieScout2005-player-club-.
- Core e App foram atualizados para Version 0.0.20.
- O CS8629 de MultiSaveClubIdentityDiagnostic foi corrigido.

Como executar
-------------
1. Abra FMGenieScout2005.sln.
2. Execute Limpar Solução e Recompilar Solução.
3. Inicie FMGenieScout2005.App.
4. Clique em "Abrir game_db.payload.bin...".
5. Escolha o payload do save mostrado na imagem do elenco do Flamengo.
6. Clique em "Investigar jogador → clube...".
7. Escolha uma pasta de saída.

Arquivos esperados
------------------
- player-club-reference-report.txt
- player-name-occurrences.csv
- player-club-reference-hits.csv
- player-club-relative-offsets.csv
- player-club-top-contexts.txt
- players-not-found.txt

O arquivo principal para a próxima versão é player-club-relative-offsets.csv.
