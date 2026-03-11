# Task: Enable mdk-sqlite-storage for the Rust Native Backend

## Status: Blocked (Windows build toolchain)

## Problem

The Rust native backend (`OpenChat.Native`) currently uses `mdk-memory-storage` which is ephemeral -- all MLS state (KeyPackages, group state, private keys) is lost on app restart. This causes "no key in the store" errors when processing Welcome messages after a restart.

The C# managed backend (`ManagedMlsService`) has been fixed to persist multiple KeyPackages and is now the default. The Rust backend is opt-in via `--mdk rust`.

## Goal

Switch the Rust backend from `MdkMemoryStorage` to `MdkSqliteStorage` for persistent MLS state, matching how all production Marmot apps (White Noise, Pika, Haven) work.

## What Was Attempted

### 1. Added `mdk-sqlite-storage` dependency to `Cargo.toml`

```toml
mdk-sqlite-storage = { git = "https://github.com/marmot-protocol/mdk", branch = "master" }
```

### 2. Rewrote `client.rs` with dual-backend support

Created an `MdkBackend` enum wrapping both storage types with a `with_mdk!` dispatch macro:

```rust
enum MdkBackend {
    Memory(MDK<MdkMemoryStorage>),
    Sqlite(MDK<MdkSqliteStorage>),
}

// Dispatch macro
macro_rules! with_mdk {
    ($mdk:expr, read, |$m:ident| $body:expr) => {{
        let guard = $mdk.read();
        match &*guard {
            MdkBackend::Memory($m) => $body,
            MdkBackend::Sqlite($m) => $body,
        }
    }};
    // ... write variant too
}
```

`MarmotClient::new()` accepted `db_path: Option<&str>` -- SQLite when provided, memory otherwise.

### 3. Updated FFI layer

- `lib.rs`: `marmot_create_client` accepts `db_path: *const c_char`
- `MarmotInterop.cs`: P/Invoke binding updated with `string? dbPath`
- `MarmotWrapper.cs`: Passes `Path.Combine(ProfileConfiguration.DataDirectory, "mls_native.db")`

### 4. Build failed

`mdk-sqlite-storage` depends on `rusqlite` with `bundled-sqlcipher` feature, which requires OpenSSL.

**Attempt A**: Build as-is -- failed with `Missing environment variable OPENSSL_DIR`.

**Attempt B**: Added `libsqlite3-sys` with `bundled-sqlcipher-vendored-openssl` to vendor OpenSSL from source -- failed because Git Bash's Perl is missing `Locale::Maketext::Simple` module needed by OpenSSL's Configure script.

No pre-built OpenSSL dev libraries are installed on the system.

## How to Resolve

Pick one of these approaches:

### Option 1: Install OpenSSL Dev (simplest)

```bash
winget install ShiningLight.OpenSSL.Dev
# Then set env var:
# OPENSSL_DIR = "C:\Program Files\OpenSSL-Win64"
```

Then `mdk-sqlite-storage` should build with its `bundled-sqlcipher` feature finding OpenSSL headers/libs.

### Option 2: Install Strawberry Perl

Strawberry Perl has all Perl modules needed to build OpenSSL from source. Then the `bundled-sqlcipher-vendored-openssl` feature flag approach works:

```toml
libsqlite3-sys = { version = "0.35", features = ["bundled-sqlcipher-vendored-openssl"] }
```

### Option 3: Build in WSL/Docker

Avoids Windows toolchain issues entirely. Linux has OpenSSL dev libs readily available.

### Option 4: Cross-compile from CI

Set up GitHub Actions to build `openchat_native.dll` on a Linux or Windows runner with OpenSSL pre-installed.

## Code to Restore

The `client.rs` dual-backend code and all FFI changes were reverted. The current code keeps `_db_path` in the FFI signature but ignores it. To restore:

1. Add `mdk-sqlite-storage` back to `Cargo.toml`
2. Restore the `MdkBackend` enum + `with_mdk!` macro in `client.rs`
3. The `MarmotInterop.cs` and `MarmotWrapper.cs` changes are already in place (dbPath parameter is already wired through)

## Reference: How Production Apps Do It

All production Marmot MDK apps use `mdk-sqlite-storage`:

- **White Noise** (whitenoise-rs): `mdk-sqlite-storage` 0.7.1, SQLite with SQLCipher
- **Pika** (sledtools/pika): `mdk-sqlite-storage` 0.7.1, same setup
- **Haven App**: `mdk-sqlite-storage`, same setup
- **MDK UniFFI bindings** (Kotlin/Swift/Python): Always use `MdkSqliteStorage`

The `MdkSqliteStorage` supports three modes:
1. `new(path, service_id, key_id)` -- encrypted with platform keyring
2. `new_with_key(path, config)` -- encrypted with manual key
3. `new_unencrypted(path)` -- unencrypted (dev only)

We planned to use `new_unencrypted()` for simplicity.

## Files Involved

| File | Status |
|------|--------|
| `src/OpenChat.Native/Cargo.toml` | Reverted (no mdk-sqlite-storage) |
| `src/OpenChat.Native/src/client.rs` | Reverted (memory-only, _db_path unused) |
| `src/OpenChat.Native/src/lib.rs` | Has db_path param in FFI (kept) |
| `src/OpenChat.Core/Marmot/MarmotInterop.cs` | Has dbPath param (kept) |
| `src/OpenChat.Core/Marmot/MarmotWrapper.cs` | Passes DB path (kept) |
| `src/OpenChat.Core/Configuration/ProfileConfiguration.cs` | Default changed to Managed |
