# FMGenieScout2005 0.0.3

## ContainerRecordExtractorDiagnostic

Terceira versão experimental do projeto FM Genie Scout 2005, inspirada no fluxo do Genie Scout moderno e construída especificamente para engenharia reversa dos saves do Football Manager 2005.

## Objetivo

Localizar registros estruturais reais pelo marcador:

```text
15 CD 5B 07 02
```

Os quatro primeiros bytes representam `123456789` em little-endian; o quinto byte é tratado como tipo `02`.

A versão valida cada marcador exigindo:

- nome UTF-16LE em `marcador + 5`;
- extensão UTF-16LE em `nome + 0x206`;
- extensão reconhecida: `.dat`, `.cmt` ou `.sav`;
- caracteres plausíveis no nome.

## Recursos

- filtra falsos candidatos encontrados dentro de `game_db.dat`;
- delimita cada registro pelo próximo marcador estrutural válido;
- extrai cada registro bruto como `NNNN_nome.raw.bin`;
- gera `manifest.csv` com offsets, tamanhos e nomes;
- gera `extraction-report.txt`;
- destaca componentes importantes como `game_db.dat`, `player_stats.dat`, `contract_man.dat` e `person_record_manager.dat`;
- nunca altera o save original.

## Como executar

1. Abra `FMGenieScout2005.sln` no Visual Studio.
2. Confirme `FMGenieScout2005.App` como projeto de inicialização.
3. Compile em `Release | Any CPU`.
4. Abra um save `.fm`.
5. Clique em **Extrair componentes...**.
6. Escolha uma pasta de destino.

O aplicativo cria uma subpasta com data e hora contendo os arquivos extraídos, o manifesto e o relatório.

## Limitação desta versão

Os arquivos `.raw.bin` contêm o registro inteiro, desde o marcador até o byte anterior ao próximo marcador validado. A posição exata em que o conteúdo útil começa ainda será investigada. Não renomeie os arquivos para `.dat` como se já fossem componentes completamente decodificados.

## Próxima etapa sugerida

A versão 0.0.4 deverá analisar o cabeçalho e o conteúdo do `game_db.dat`, separando metadados do payload e procurando estruturas de pessoas, clubes e jogadores.
