# MIP-00 Compliance Audit: KeyPackage Events (Kind 443)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip00` and OpenChat's `NostrService` correctly implement
MIP-00 (KeyPackage event creation, publishing, fetching, and parsing) and are interoperable with the
Rust marmot reference implementation.

## Specification Summary

- **Nostr Kind**: 443
- **Content**: base64-encoded MLS KeyPackage bytes
- **Required Tags**:
  - `encoding`: `"mls-base64"`
  - `protocol_version`: `"0"`
  - `ciphersuite`: `"1"` (MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519)
  - `extensions`: `""` (empty)
  - `relays`: relay URLs
  - `i`: KeyPackageRef hex (RFC 9420 Section 5.2 RefHash)
- **KeyPackageRef**: `RefHash("MLS 1.0 KeyPackage Reference", keyPackageBytes)` → lowercase hex

## Audit Checklist

### marmot-cs Protocol Layer
- [ ] `KeyPackageEventBuilder.BuildKeyPackageEvent` produces all required tags in correct order
- [ ] `encoding` tag value is exactly `"mls-base64"` (not `"base64"`)
- [ ] `i` tag contains correct KeyPackageRef (verify with known test vector)
- [ ] `KeyPackageEventParser.ParseKeyPackageEvent` correctly validates encoding tag
- [ ] Parser handles missing optional tags gracefully (relays)
- [ ] Parser rejects events with wrong encoding

### OpenChat Integration
- [ ] `NostrService.PublishKeyPackageAsync` uses `KeyPackageEventBuilder` (or equivalent) with all MIP-00 tags
- [ ] Published events include relay list from connected relays
- [ ] `NostrService.FetchKeyPackagesAsync` correctly parses returned events
- [ ] Tag validation in `NostrService` accepts both naming conventions (`mls_ciphersuite`/`ciphersuite`)
- [ ] NIP-70 `-` tag is filtered out before publishing (known prior bug)

### Interop with Rust MDK
- [ ] C# KeyPackage events are accepted by Rust `add_member` (full kind-443 JSON with all tags)
- [ ] Rust-published KeyPackage events are correctly parsed by C#
- [ ] KeyPackageRef computation matches between implementations

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip00/KeyPackageEventBuilder.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip00/KeyPackageEventParser.cs`
- `openChat/src/OpenChat.Core/Services/NostrService.cs` (PublishKeyPackageAsync, FetchKeyPackagesAsync)
- `marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs`

## Test Strategy

1. Unit tests: roundtrip build→parse in marmot-cs
2. Cross-MDK test: C# KeyPackage → Rust `add_member` acceptance
3. Relay integration: publish from C#, fetch from Rust (and vice versa)
