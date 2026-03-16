# MIP-00 Compliance Audit: KeyPackage Events (Kind 443)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip00` and OpenChat's `NostrService` correctly implement
MIP-00 (KeyPackage event creation, publishing, fetching, and parsing) and are interoperable with the
Rust marmot reference implementation.

## Specification Summary

- **Nostr Kind**: 443
- **Content**: base64-encoded MLS KeyPackage bytes
- **Required Tags**:
  - `encoding`: `"base64"`
  - `mls_protocol_version`: `"1.0"`
  - `mls_ciphersuite`: `"0x0001"` (MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519)
  - `mls_extensions`: supported extension type IDs (e.g. `"0x000a"`, `"0xf2ee"`)
  - `relays`: relay URLs
  - `i`: KeyPackageRef hex (RFC 9420 Section 5.2 RefHash)
- **KeyPackageRef**: `RefHash("MLS 1.0 KeyPackage Reference", keyPackageBytes)` → lowercase hex

## Audit Checklist

### marmot-cs Protocol Layer
- [x] `KeyPackageEventBuilder.BuildKeyPackageEvent` produces all required tags in correct order
  - Tags: encoding, mls_protocol_version, mls_ciphersuite, mls_extensions, relays, i
  - Test: `Build_ContainsRequiredTags` (includes mls_extensions)
- [x] `encoding` tag value is exactly `"base64"`
  - Test: `Build_EncodingTagIsBase64`
- [x] `i` tag contains correct KeyPackageRef (lowercase hex, 64 chars = SHA-256)
  - Uses `RefHash("MLS 1.0 KeyPackage Reference", value)` via `KeyPackageRef.Compute()`
  - Tests: `Build_ITagContainsKeyPackageRef`, `Build_KeyPackageRefIsLowercaseHex`
- [x] `KeyPackageEventParser.ParseKeyPackageEvent` correctly validates encoding tag
  - Rejects any encoding != "base64"
  - Test: `Parse_MissingEncoding_Throws`
- [x] Parser handles missing optional tags gracefully (relays)
  - Returns empty array when relays tag is absent
  - Test: `Build_NoRelays_ParsesWithEmptyRelays`
- [x] Parser rejects events with wrong encoding
  - Test: `Parse_WrongEncoding_Throws` (encoding="hex" -> FormatException)

### OpenChat Integration
- [x] `NostrService.PublishKeyPackageAsync` uses `KeyPackageEventBuilder` (or equivalent) with all MIP-00 tags
  - `ManagedMlsService.GenerateKeyPackageAsync()` calls `KeyPackageEventBuilder.BuildKeyPackageEvent()`
    with `supportedExtensionTypes: new ushort[] { 0x000A, 0xF2EE }`, passes tags to `PublishKeyPackageAsync`
  - `PublishKeyPackageAsync` passes MDK tags through directly (no normalization layer)
- [x] Published events include relay list from connected relays
  - Lines 737-753: populates empty relay tags with connected/default relay URLs
- [x] `NostrService.FetchKeyPackagesAsync` correctly parses returned events
  - Validates MIP-00 tags (encoding=base64, mls_ciphersuite, mls_protocol_version)
  - Decodes base64 content, sanity-checks length >= 64 bytes
  - Deduplicates events by ID across multiple relays
- [x] Tag validation in `NostrService` only accepts `mls_ciphersuite`/`mls_protocol_version`
  - Legacy tag names (`ciphersuite`/`protocol_version`) removed -- only standard names accepted
- [x] NIP-70 `-` tag is filtered out before publishing (known prior bug)
  - Line 742: `.Where(t => !(t.Count == 1 && t[0] == "-"))`

### Interop with Rust MDK
- [x] C# KeyPackage events are accepted by Rust `add_member` (full kind-443 JSON with all tags)
  - Test: `CSharpKeyPackage_AcceptedByRustAddMember` (direct, no relay)
  - Bug found and fixed: missing `0x000A` (LastResort) extension required by Rust MDK
- [x] Rust-published KeyPackage events are correctly parsed by C#
  - Test: `RustKeyPackage_ParsedByCSharpParser`
- [x] KeyPackageRef computation matches between implementations
  - Test: `KeyPackageRef_MatchesBetweenCSharpAndRust`
  - Both use `RefHash("MLS 1.0 KeyPackage Reference", value)` -- hash output matches

## Bugs Found and Fixed

1. **Missing LastResort extension (0x000A)**: C# KeyPackages only declared support for `0xF2EE`
   (NostrGroupData). Rust MDK requires `0x000A` (LastResort, RFC 9420 Section 17.3).
   Fixed in `ManagedMlsService.GenerateKeyPackageAsync()` -- now declares both `{ 0x000A, 0xF2EE }`.

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip00/KeyPackageEventBuilder.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip00/KeyPackageEventParser.cs`
- `openChat/src/OpenChat.Core/Services/NostrService.cs` (PublishKeyPackageAsync, FetchKeyPackagesAsync)
- `openChat/src/OpenChat.Core/Services/ManagedMlsService.cs` (GenerateKeyPackageAsync)
- `marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs`
- `openChat/tests/OpenChat.Core.Tests/Mip00InteropTests.cs`

## Test Coverage

### marmot-cs unit tests (13 tests passing)
1. `BuildAndParseKeyPackageEvent_RoundTrips`
2. `Build_ITagContainsKeyPackageRef`
3. `Build_ContainsRequiredTags` (all 6 tags including mls_extensions)
4. `Build_EncodingTagIsBase64`
5. `Build_TagValuesAreCorrect` (mls_protocol_version="1.0", mls_ciphersuite="0x0001")
6. `Build_WithExtensionTypes_ContainsHexValues` (0xf2ee, 0x000a)
7. `Build_KeyPackageRefIsLowercaseHex` (64 chars)
8. `Parse_MissingEncoding_Throws`
9. `Parse_WrongEncoding_Throws` (encoding="hex")
10. `Parse_MissingITag_Throws`
11. `Build_EmptyKpBytes_Throws`
12. `Build_EmptyIdentity_Throws`
13. `Build_NoRelays_ParsesWithEmptyRelays`

### Cross-MDK interop tests (5 tests passing, requires native DLL)
1. `CSharpKeyPackage_AcceptedByRustAddMember` -- C# KP -> Rust add_member (no relay)
2. `RustKeyPackage_ParsedByCSharpParser` -- Rust KP -> C# parser
3. `KeyPackageRef_MatchesBetweenCSharpAndRust` -- same bytes, same hash
4. `CSharpKeyPackage_SignedEvent_RoundTrips` -- build -> sign -> parse roundtrip
5. `RustKeyPackage_HasCorrectMip00TagNames` -- Rust tag names match spec
