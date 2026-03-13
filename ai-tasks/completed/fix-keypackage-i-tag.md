# Fix KeyPackage `i` Tag: Wrong Value and Extra Element

## Problem

The C# `KeyPackageEventBuilder` creates the `i` tag incorrectly for kind 443 (KeyPackage) Nostr events. This causes Rust MDK v0.7.1 to reject the KeyPackage with:

```
MarmotException: Internal error: Failed to add member: i tag must contain exactly one value
```

## C# (Wrong)

File: `C:\Users\david\openCodeProjects\marmot-cs\src\MarmotCs.Protocol\Mip00\KeyPackageEventBuilder.cs` line 55

```csharp
new[] { "i", identityHex, "mls" }
```

Produces: `["i", "<owner_pubkey_hex>", "mls"]` — **3 elements**

Two problems:
1. **Extra element**: The `"mls"` suffix (an NIP-39 identity convention) is not part of MIP-00
2. **Wrong value**: Contains the owner's public key, but should contain the **KeyPackageRef hash**

## Rust MDK (Correct)

File: `~/.cargo/git/checkouts/mdk-7d5a3a2420b194f5/d3cb3f1/crates/mdk-core/src/key_packages.rs` line 170

```rust
Tag::custom(TagKind::i(), [key_package_ref_hex])
```

Produces: `["i", "<key_package_ref_hex>"]` — **2 elements**

The `key_package_ref_hex` is computed as `hex::encode(hash_ref.as_slice())` where `hash_ref` is the MLS `KeyPackageRef` — a `HashReference` of the serialized KeyPackage per RFC 9420 Section 5.2.

## Rust Validation (v0.7.1)

File: `~/.cargo/git/checkouts/mdk-7d5a3a2420b194f5/d3cb3f1/crates/mdk-core/src/key_packages.rs` lines 566-590

```rust
fn validate_key_package_ref_tag(&self, tag: &Tag) -> Result<(), Error> {
    let slice = tag.as_slice();
    // Exactly 2 elements: ["i", "<hex_value>"]
    if slice.len() != 2 {
        return Err(Error::KeyPackage("i tag must contain exactly one value".to_string()));
    }
    let hex_value = &slice[1];
    if hex_value.is_empty() {
        return Err(Error::KeyPackage("i tag value must not be empty".to_string()));
    }
    hex::decode(hex_value.as_str()).map_err(|e| {
        Error::KeyPackage(format!("i tag must contain valid hex-encoded data: {}", e))
    })?;
    Ok(())
}
```

## What is a KeyPackageRef?

Per RFC 9420 Section 5.2, a `KeyPackageRef` (called `HashReference`) is:

```
KeyPackageRef = MakeKeyPackageRef(value)
             = KDF.Extract("", value) → truncated/hashed
```

In OpenMLS, it's computed as:
```rust
let hash_ref = key_package.hash_ref(crypto_provider.crypto())?;
```

This is essentially a hash of the serialized KeyPackage bytes that uniquely identifies the KeyPackage. It allows relays to be queried for specific KeyPackages by reference without downloading all of them.

## Fix Required

### In `MarmotCs.Protocol` (`KeyPackageEventBuilder.cs`):

1. **Compute the KeyPackageRef hash** from the `keyPackageBytes` parameter
2. **Change the `i` tag** to `["i", "<hex_encoded_kp_ref>"]` (2 elements, no "mls" suffix)

### Computing the KeyPackageRef:

The `KeyPackageRef` uses `RefHash` from DotnetMls:
```
RefHash(label, value) = KDF.Extract(KDF.Expand(secret=zeros, info=label, L=hash_len), value)
```

Or more specifically per RFC 9420 Section 5.2:
```
MakeKeyPackageRef(value) = RefHash("MLS 1.0 KeyPackage Reference", value)
```

DotnetMls already has `ICipherSuite.RefHash(string label, byte[] value)` in `CipherSuite0x0001.cs`. The builder needs access to a cipher suite instance or the hash computation needs to be extracted as a static utility.

### Updated `BuildKeyPackageEvent`:

```csharp
public static (string content, string[][] tags) BuildKeyPackageEvent(
    byte[] keyPackageBytes,
    string identityHex,
    string[] relays)
{
    // Compute KeyPackageRef per RFC 9420 Section 5.2
    byte[] kpRef = ComputeKeyPackageRef(keyPackageBytes);
    string kpRefHex = Convert.ToHexString(kpRef).ToLowerInvariant();

    string[][] tags = new[]
    {
        new[] { "encoding", "mls-base64" },
        new[] { "protocol_version", "0" },
        new[] { "ciphersuite", "1" },
        new[] { "extensions", "" },
        relaysTag,
        new[] { "i", kpRefHex }  // Fixed: single hex value, no "mls" suffix
    };
    // ...
}
```

### OpenChat NostrService normalization:

File: `C:\Users\david\openCodeProjects\openChat\src\OpenChat.Core\Services\NostrService.cs` lines 703-766

The existing tag normalization in `PublishKeyPackageAsync` does not touch the `i` tag, so no changes needed there. The fix is entirely in `KeyPackageEventBuilder`.

## Testing

1. **Unit test in `MarmotCs.Protocol.Tests`**: Verify the `i` tag has exactly 2 elements and the value is valid hex
2. **Cross-MDK test**: `CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay` — the `add_member` step should no longer fail with "i tag must contain exactly one value"

## File Reference

| File | Repo | Purpose |
|------|------|---------|
| `KeyPackageEventBuilder.cs` | marmot-cs/src/MarmotCs.Protocol/Mip00/ | Tag construction (fix here) |
| `CipherSuite0x0001.cs` | dotnet-mls/src/DotnetMls.Crypto/ | Has `RefHash` implementation |
| `key_packages.rs` | Rust MDK (mdk-core/src/) | Reference: correct tag + validation |
| `NostrService.cs` | openChat/src/OpenChat.Core/Services/ | Tag normalization (no change needed) |
| `ManagedMlsService.cs` | openChat/src/OpenChat.Core/Services/ | Calls `BuildKeyPackageEvent` |
| `CrossMdkRelayIntegrationTests.cs` | openChat/tests/OpenChat.Core.Tests/ | Integration test |
