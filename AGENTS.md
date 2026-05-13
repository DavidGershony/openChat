# Scramble — Project Context

## Platform targets

This project has **three UI targets** in the .NET solution, plus a separate Apple target:

| Project | Platform | UI framework | Key entry point | Status |
|---------|----------|--------------|-----------------|--------|
| `src/Scramble.UI` + `src/Scramble.Desktop` | Windows / Linux / macOS (desktop) | Avalonia XAML | `src/Scramble.UI/Views/` | Active |
| `src/Scramble.UI` + `src/Scramble.Mobile.Android` | Android (mobile, Avalonia) | Avalonia XAML | `src/Scramble.UI/Views/` | Active |
| `src/Scramble.Android` | Android (mobile, native) | Android Views / Fragments | `src/Scramble.Android/Fragments/` | Potentially obsolete — depends on Scramble.Mobile.Android performance |
| `src/Scramble.Apple` | macOS (iOS planned) | Avalonia XAML | `src/Scramble.Apple/` | Active — in repo but not in .sln (requires macOS workload) |

`Scramble.UI` contains the shared Avalonia XAML views used by `Scramble.Desktop`, `Scramble.Mobile.Android`, and `Scramble.Apple`.

Shared logic lives in:
- `src/Scramble.Core` — services, models, MLS/Nostr
- `src/Scramble.Presentation` — ReactiveUI ViewModels (used by all targets)

**Any feature that touches the UI should be implemented in `Scramble.UI` (shared by Desktop, Mobile.Android, and Apple). Updates to `Scramble.Android` (native) are optional given its potentially obsolete status.**

## Native library

| Project | Language | Purpose |
|---------|----------|---------|
| `src/Scramble.Native` | Rust | Wraps the Marmot Development Kit (MLS + Nostr) and exposes it to .NET via auto-generated P/Invoke bindings (csbindgen). Built separately via `cargo`, not included in the .sln. |

## Libraries (git submodules in `lib/`)

| Library | Purpose |
|---------|---------|
| `lib/marmot-cs` | C# implementation of the Marmot Messaging Development Kit — secure group messaging combining MLS (RFC 9420) with Nostr. Contains: `MarmotCs.Core`, `MarmotCs.Protocol`, `MarmotCs.Storage.Abstractions`, `MarmotCs.Storage.Memory`, `MarmotCs.Storage.Sqlite` |
| `lib/dotnet-mls` | Pure C# implementation of the MLS protocol (RFC 9420) — the cryptographic group state machine. Contains: `DotnetMls`, `DotnetMls.Crypto` |

All library projects above are included in `Scramble.sln`.

## Tests

| Project | Purpose |
|---------|---------|
| `tests/Scramble.Core.Tests` | Unit tests for `Scramble.Core` |
| `tests/Scramble.UI.Tests` | Unit tests for `Scramble.UI` |
| `tests/Scramble.Diagnostics` | Diagnostic/utility test project |
| `tests/whitenoise-docker` | Docker setup for test infrastructure |
