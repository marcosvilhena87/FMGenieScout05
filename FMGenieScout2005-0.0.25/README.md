# FMGenieScout2005 0.0.18 — MultiSaveClubIdentityDiagnostic

Compara dois arquivos `game_db.payload.bin` em modo somente leitura.

## Hipótese testada

- `ClubDatabaseId` é a identidade persistente do clube no banco.
- `SaveClubIndex` é um índice local, compacto e potencialmente diferente em cada save.

## Uso

1. Selecione o `game_db.payload.bin` do Save 1.
2. Selecione o `game_db.payload.bin` do Save 2.
3. Escolha a pasta de saída e execute a comparação.

Para tornar o teste mais informativo, use saves criados com configurações de banco ou ligas diferentes.

## Saídas

- `multi-save-club-identity-report.txt`
- `multi-save-club-identity.csv`
- `changed-save-indices.csv`
- `save-membership-differences.csv`
- `club-name-mismatches.csv`
- `save1-parser/`
- `save2-parser/`

Os arquivos de origem nunca são modificados.
