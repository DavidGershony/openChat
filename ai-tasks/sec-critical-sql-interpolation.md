# SEC-CRITICAL: SQL Interpolation in Migrations

## Problem
`StorageService.cs:235` uses string interpolation for column names in ALTER TABLE:
```csharp
migrate.CommandText = $"ALTER TABLE Users ADD COLUMN {col} TEXT";
```
Currently `col` comes from a hardcoded array, but the pattern is dangerous.

## Attack Vector
Low practical risk today (hardcoded source), but establishes a dangerous pattern that could be copy-pasted or extended unsafely.

## Location
- `StorageService.cs:235` — migration ALTER TABLE

## Fix
- [x] Extracted `ValidateMigrationColumnName()` — whitelist regex `^[a-zA-Z_]+$`
- [x] Called before interpolation in migration loop
- [x] Added comment explaining why DDL column names can't be parameterized
- [x] Audited: no other string-interpolated SQL found

## Tests (10 in StorageServiceTests.cs)
- [x] 3 valid column names accepted: `ValidColumn`, `Signer_Relay_Url`, `column_name`
- [x] 7 injection patterns rejected: `; DROP TABLE`, `' OR '1'='1`, `)`, spaces, digits, empty, hyphens

## Status
- [x] Complete
