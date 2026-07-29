# FMGenieScout2005 0.0.4 — GameDbStructureDiagnostic

Diagnóstico experimental e somente leitura do componente `game_db.dat.raw.bin` extraído pela versão 0.0.3.

## Recursos

- detecta e remove o cabeçalho do contêiner, gerando `game_db.payload.bin`;
- indexa strings UTF-16LE plausíveis;
- registra offset, comprimento e distância da string anterior;
- agrupa strings por proximidade;
- procura clubes e jogadores conhecidos;
- grava contexto hexadecimal para ocorrências dirigidas;
- gera `strings.csv`, `groups.csv`, `search-hits.csv` e `game-db-report.txt`;
- não altera o arquivo de origem.

## Uso

1. Compile e execute `FMGenieScout2005.App`.
2. Abra o arquivo `0077_game_db.dat.raw.bin` produzido pela versão 0.0.3.
3. Clique em **Analisar estrutura...**.
4. Escolha a pasta de saída.
5. Envie `game-db-report.txt`, `groups.csv` e `search-hits.csv` para orientar a versão 0.0.5.

## Limitação

O offset do payload ainda é uma hipótese diagnóstica (`0x220`) e será confirmado por comparação estrutural. Esta versão não interpreta jogadores ou atributos.
