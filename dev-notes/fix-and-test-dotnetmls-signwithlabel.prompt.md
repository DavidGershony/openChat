# Fix DotnetMls SignWithLabel Encoding & Verify Cross-MDK Interop

## Background

The C# MLS library (`DotnetMls`) at `C:\Users\david\openCodeProjects\dotnet-mls` has a critical bug in `BuildSignContent()` and `RefHash()` in `src/DotnetMls.Crypto/CipherSuite0x0001.cs`. Both methods are missing QUIC-style VarInt length prefixes required by RFC 9420 for `opaque <V>` fields.

The detailed bug analysis and fix code is in:
`C:\Users\david\openCodeProjects\dotnet-mls\dev-notes\fix-signwithlabel-encoding.prompt.md`

This prompt describes how to **apply the fix, rebuild, update dependencies, and run the end-to-end cross-MDK verification test**.

## Step 1: Apply the Fix in DotnetMls

Working directory: `C:\Users\david\openCodeProjects\dotnet-mls`

### 1a. Fix `BuildSignContent` in `src/DotnetMls.Crypto/CipherSuite0x0001.cs`

Replace the `BuildSignContent` method (line ~276) with proper VarInt-prefixed encoding. Add an `EncodeVarInt` private helper. The full replacement code is in the prompt file above.

Key change: from raw `"MLS 1.0 " || Label || Content` to `VarInt(len) || "MLS 1.0 " || Label || VarInt(len) || Content`.

### 1b. Fix `RefHash` in the same file

Replace the `RefHash` method (line ~158) to use VarInt prefixes for both `label` and `value` fields. Currently uses `uint16` for label and nothing for value — both should be VarInt.

### 1c. Update doc comments in `ICipherSuite.cs`

Update `SignWithLabel` (line ~98) and `RefHash` (line ~77) doc comments to reflect the correct VarInt encoding.

### 1d. Run DotnetMls unit tests

```bash
cd C:/Users/david/openCodeProjects/dotnet-mls
dotnet test
```

All existing tests should still pass (sign/verify round-trips work since both sides use the new encoding). If any tests have hardcoded expected byte values, they'll need updating.

## Step 2: Rebuild & Publish NuGet Packages Locally

The dependency chain is:
```
OpenChat.Core
  └─ DotnetMls (0.1.0-alpha.1)
  └─ MarmotMdk.Core (0.1.0-alpha.1)
       └─ DotnetMls (0.1.0-alpha.1)
  └─ MarmotMdk.Storage.Memory (0.1.0-alpha.1)
```

MarmotMdk.Core and MarmotMdk.Protocol (at `C:\Users\david\openCodeProjects\marmut-mdk`) also reference `DotnetMls 0.1.0-alpha.1`.

### Option A: Bump version and publish locally

```bash
# 1. Pack DotnetMls with new version
cd C:/Users/david/openCodeProjects/dotnet-mls
dotnet pack src/DotnetMls.Crypto/DotnetMls.Crypto.csproj -c Release -o ./nupkg /p:Version=0.1.0-alpha.2
dotnet pack src/DotnetMls/DotnetMls.csproj -c Release -o ./nupkg /p:Version=0.1.0-alpha.2

# 2. Add local NuGet source (if not already done)
dotnet nuget add source C:/Users/david/openCodeProjects/dotnet-mls/nupkg --name local-dotnetmls

# 3. Update MarmotMdk to use new version
cd C:/Users/david/openCodeProjects/marmut-mdk
# Edit src/MarmotMdk.Core/MarmotMdk.Core.csproj: DotnetMls Version → 0.1.0-alpha.2
# Edit src/MarmotMdk.Protocol/MarmotMdk.Protocol.csproj: DotnetMls Version → 0.1.0-alpha.2
dotnet pack -c Release -o ./nupkg /p:Version=0.1.0-alpha.2

# 4. Add local MarmotMdk source
dotnet nuget add source C:/Users/david/openCodeProjects/marmut-mdk/nupkg --name local-marmutmdk

# 5. Update OpenChat to use new versions
cd C:/Users/david/openCodeProjects/openChat
# Edit src/OpenChat.Core/OpenChat.Core.csproj:
#   DotnetMls Version → 0.1.0-alpha.2
#   MarmotMdk.Core Version → 0.1.0-alpha.2
#   MarmotMdk.Storage.Memory Version → 0.1.0-alpha.2
dotnet restore
```

### Option B: Use ProjectReference instead (simpler for dev)

Temporarily replace NuGet PackageReferences with ProjectReferences in `OpenChat.Core.csproj`:

```xml
<!-- Comment out NuGet refs -->
<!-- <PackageReference Include="DotnetMls" Version="0.1.0-alpha.1" /> -->
<!-- <PackageReference Include="MarmotMdk.Core" Version="0.1.0-alpha.1" /> -->
<!-- <PackageReference Include="MarmotMdk.Storage.Memory" Version="0.1.0-alpha.1" /> -->

<!-- Add project refs -->
<ProjectReference Include="..\..\..\dotnet-mls\src\DotnetMls\DotnetMls.csproj" />
<ProjectReference Include="..\..\..\marmut-mdk\src\MarmotMdk.Core\MarmotMdk.Core.csproj" />
<ProjectReference Include="..\..\..\marmut-mdk\src\MarmotMdk.Storage.Memory\MarmotMdk.Storage.Memory.csproj" />
```

And similarly in `marmut-mdk`'s .csproj files, replace the DotnetMls PackageReference with a ProjectReference to the local dotnet-mls.

## Step 3: Run the Cross-MDK Integration Test

### Prerequisites

1. Docker relay must be running:
   ```bash
   cd C:/Users/david/openCodeProjects/openChat
   docker compose -f docker-compose.test.yml up -d
   ```

2. Native Rust MLS DLL (`openchat_native.dll`) must be at `src/OpenChat.Desktop/openchat_native.dll`

### 3a. Remove the Skip attribute

In `tests/OpenChat.Core.Tests/CrossMdkRelayIntegrationTests.cs`, change:
```csharp
[Fact(Skip = "Blocked by DotnetMls SignWithLabel encoding bug — see fix-signwithlabel-encoding.prompt.md")]
public async Task CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay()
```
to:
```csharp
[Fact]
public async Task CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay()
```

### 3b. Run the test

```bash
cd C:/Users/david/openCodeProjects/openChat
dotnet test tests/OpenChat.Core.Tests --filter "CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay" -v n
```

### What the test does

1. **User A (Rust/native MLS)** creates a group via `MlsService` (calls `openchat_native.dll`)
2. **User B (C#/managed MLS)** generates a KeyPackage via `ManagedMlsService` (calls DotnetMls)
3. User B publishes KeyPackage to localhost relay (ws://localhost:7777)
4. User A fetches User B's KeyPackage from relay
5. **User A calls `AddMemberAsync`** — this is where the Rust DLL validates the LeafNode signature from User B's KeyPackage. **THIS IS THE STEP THAT CURRENTLY FAILS** with "The leaf node signature is not valid" because DotnetMls signs without VarInt prefixes
6. User A publishes Welcome to relay
7. User B receives Welcome, processes it, joins the group
8. Both users exchange encrypted messages bidirectionally (Rust→C# and C#→Rust)

### Expected result after fix

All 8 phases complete successfully. The test logs will show:
```
CROSS-MDK TEST PASSED: Rust → Relay → Managed Welcome + bidirectional message exchange
```

## Step 4: Run ALL Relay Integration Tests

After the cross-MDK test passes, run the full relay test suite to ensure nothing regressed:

```bash
dotnet test tests/OpenChat.Core.Tests --filter "Category=Relay" -v n
```

Expected: all tests pass (15 Relay tests + 4 EndToEnd tests).

## Step 5: Run DotnetMls-only tests (no relay needed)

```bash
cd C:/Users/david/openCodeProjects/dotnet-mls
dotnet test
```

## File Reference

| File | Location | Purpose |
|------|----------|---------|
| `CipherSuite0x0001.cs` | `dotnet-mls/src/DotnetMls.Crypto/` | Contains `BuildSignContent` and `RefHash` (bugs to fix) |
| `ICipherSuite.cs` | `dotnet-mls/src/DotnetMls.Crypto/` | Interface doc comments to update |
| `CrossMdkRelayIntegrationTests.cs` | `openChat/tests/OpenChat.Core.Tests/` | End-to-end cross-MDK verification test |
| `OpenChat.Core.csproj` | `openChat/src/OpenChat.Core/` | DotnetMls NuGet reference (0.1.0-alpha.1) |
| `MarmotMdk.Core.csproj` | `marmut-mdk/src/MarmotMdk.Core/` | Also references DotnetMls (0.1.0-alpha.1) |
| `MarmotMdk.Protocol.csproj` | `marmut-mdk/src/MarmotMdk.Protocol/` | Also references DotnetMls (0.1.0-alpha.1) |
| `nuget.config` | `openChat/` | NuGet sources (nuget.org + GitHub packages) |
| `fix-signwithlabel-encoding.prompt.md` | `dotnet-mls/dev-notes/` | Detailed bug analysis and fix code |
