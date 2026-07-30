FM Genie Scout 2005 — versão 0.0.26
===================================

Objetivo
--------
Delimitar possíveis blocos de contrato, inscrição, empréstimo e referências
auxiliares por meio de assinaturas estruturais ao redor de PersonDatabaseId e
InternalPersonId.

Esta versão não assume que exista um único offset universal e não interpreta
nenhum campo como clube atual.

O diagnóstico
-------------
- usa 18 jogadores conhecidos de Flamengo e Corinthians;
- separa amostras NORMAL e com estados especiais (Emp, Lst e Prt);
- localiza todas as ocorrências de PersonDatabaseId e InternalPersonId;
- extrai contexto de -128 a +256 bytes;
- cria uma assinatura categórica de campos UInt32 em -48 a +96 bytes;
- estima limites por sequências de pelo menos 8 bytes zero;
- mede distâncias até referências conhecidas anteriores e posteriores;
- agrupa layouts compartilhados e variantes menores.

Arquivos gerados
----------------
- contract-record-boundary-report.txt
- contract-reference-occurrences.csv
- contract-signature-clusters.csv
- contract-boundary-candidates.csv
- contract-record-contexts.txt
- people-not-resolved.txt

Arquivos principais
-------------------
1. contract-signature-clusters.csv
2. contract-boundary-candidates.csv

Interpretação
-------------
SHARED_LAYOUT_STRONG ou SHARED_LAYOUT_MEDIUM indica uma estrutura semelhante
presente em jogadores dos dois clubes. SAME_CLUB_OR_VARIANT_CLUSTER pode indicar
uma variante de contrato, empréstimo, listagem, inscrição ou histórico.

BOUNDARY_CANDIDATE_STRONG ou MEDIUM indica uma distância repetida até uma possível
fronteira ou outra referência conhecida.

Compilação
----------
dotnet clean
dotnet build

Nenhum arquivo do save é alterado.
