# SEC-HIGH: SQLite Concurrency Control

## Problem
StorageService opens new SQLite connections for each operation without connection pooling or write coordination. Concurrent writes can cause "database is locked" errors or data corruption.

## Location
- `StorageService.cs` — all async methods create new `SqliteConnection`

## Fix
- [ ] Use WAL mode for SQLite (allows concurrent reads with single writer)
- [ ] Add write serialization (SemaphoreSlim or single writer channel)
- [ ] Or use connection pooling with proper configuration

## Tests Required
- [ ] Concurrent read+write operations don't throw "database is locked"
- [ ] Concurrent writes are serialized correctly

## Status
- [ ] Not started
