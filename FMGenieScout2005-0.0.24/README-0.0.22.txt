FM Genie Scout 2005 — versão 0.0.22
====================================

Objetivo
--------
Converter a estrutura descoberta na 0.0.21 em um parser permanente de
identidades de pessoas do Football Manager 2005.

Estrutura utilizada
-------------------
Int32 NameLength
UTF-16LE Name
UInt16 Terminator = 0
UInt16 RecordType
UInt32 StructureMarker = 189
UInt32 SequenceIndex
UInt32 DatabaseId

Regra principal
---------------
DatabaseIdOffset = NameOffset + NameLength * 2 + 12

Conteúdo novo
-------------
- Domain/PersonIdentity.cs
- Parsing/Fm2005PersonIdentityParser.cs
- Parsing/Fm2005PersonIdentityParseResult.cs
- Diagnostics/PersonIdentityParserDiagnostic.cs
- Models/PersonIdentityParserModels.cs

Validação incorporada
---------------------
- Andre Luiz Tavares / Andrezinho: 302728
- André Bahia: 311169
- Bruno Santos: 8832815
- Dimba: 303048
- Douglas Silva: 3300410
- Fabiano Eller: 3300219
- Júlio César Moraes: 8825041
- Júnior Baiano: 6900111
- Reginaldo Araújo: 3300780

Arquivos produzidos
-------------------
- person-identity-parser-report.txt
- all-person-identities.csv
- known-player-validation.csv
- person-identity-structure.txt

Como executar
-------------
1. Abra FMGenieScout2005.sln.
2. Execute dotnet clean e dotnet build.
3. Inicie o projeto FMGenieScout2005.App.
4. Selecione o game_db.payload.bin.
5. Clique em "Extrair identidades de pessoas...".
6. Compacte a pasta FMGenieScout2005-person-identities-* e envie os resultados.

Observação
----------
O parser é somente leitura e não modifica o save.
