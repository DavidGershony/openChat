# Fix Bidirectional MLSMessage Envelope: C# Must Wrap, Not Just Strip

## Problem

Rust MDK (OpenMLS) wraps all MLS messages in an `MLSMessage` envelope per RFC 9420 Section 6. C# MarmotCs does not. This breaks bidirectional messaging:

- **Rust → C# (partially fixed)**: `Mdk.cs` in `ProcessMessageAsync` detects the `0x00 0x01` header and strips the envelope before processing. This works but is a workaround.
- **C# → Rust (broken)**: `CreateMessageAsync` sends raw `PrivateMessage` bytes without the envelope. Rust MDK expects the envelope and will fail to parse the message.

The same applies to `Commit` and `PublicMessage` — any MLS wire message C# sends to Rust will be missing the envelope.

## What is the MLSMessage Envelope?

RFC 9420 Section 6 defines `MLSMessage` as the top-level wire format:

```
struct {
    ProtocolVersion version = mls10;   // u16, value 0x0001
    WireFormat wire_format;            // u16: 1=PublicMessage, 2=PrivateMessage, ...
    select (MLSMessage.wire_format) {
        case mls_public_message:  PublicMessage;
        case mls_private_message: PrivateMessage;
        case mls_welcome:         Welcome;
        case mls_group_info:      GroupInfo;
        case mls_key_package:     KeyPackage;
    };
} MLSMessage;
```

So a PrivateMessage on the wire looks like:
```
0x00 0x01  (ProtocolVersion = MLS 1.0)
0x00 0x02  (WireFormat = PrivateMessage)
<PrivateMessage bytes>
```

## Why C# Doesn't Wrap (and Why It Should)

The C# MLS implementation (`DotnetMls`) operates at a lower level than OpenMLS. When DotnetMls creates a `PrivateMessage`, it returns the raw message struct without the `MLSMessage` envelope wrapper. This is because:

1. **DotnetMls models individual message types** — `PrivateMessage`, `PublicMessage`, `Welcome` are separate types with their own `WriteTo`/`ReadFrom`, but there's no automatic wrapping
2. **The `MlsMessage` type exists** in DotnetMls (`src/DotnetMls/Types/MlsMessage.cs`) but it's used for parsing incoming enveloped messages, not for constructing outgoing ones
3. **MarmotCs.Core's `Mdk.cs`** calls `mlsGroup.CreatePrivateMessage()` → `TlsCodec.Serialize(pm.WriteTo)` directly, skipping the envelope

OpenMLS, by contrast, always returns `MLSMessage`-wrapped bytes from its public API — the envelope is baked into the output at the framework level.

The right approach is **not** to change DotnetMls's low-level types but to have MarmotCs.Core wrap messages at the protocol boundary — the same layer that currently strips them on receive.

## Fix Required

### In `MarmotCs.Core/Mdk.cs`:

**`CreateMessageAsync`** — wrap the outgoing `PrivateMessage` in an `MLSMessage` envelope:

```csharp
// After creating the PrivateMessage
var pm = mlsGroup.CreatePrivateMessage(rumor, ...);

// Wrap in MLSMessage envelope for wire compatibility
byte[] messageBytes = TlsCodec.Serialize(writer =>
{
    writer.WriteUint16(0x0001);  // ProtocolVersion = MLS 1.0
    writer.WriteUint16(0x0002);  // WireFormat = PrivateMessage
    pm.WriteTo(writer);
});
```

**`CreateCommitAsync` / `SelfUpdateAsync`** — same pattern with `WireFormat = PublicMessage (0x0001)`:

```csharp
byte[] commitBytes = TlsCodec.Serialize(writer =>
{
    writer.WriteUint16(0x0001);  // ProtocolVersion
    writer.WriteUint16(0x0001);  // WireFormat = PublicMessage
    publicMessage.WriteTo(writer);
});
```

**`ProcessMessageAsync`** — can keep the envelope-stripping logic as a fallback for backward compatibility, but once both sides wrap properly, the stripping becomes the normal path rather than a workaround.

### Alternative: Wrap in DotnetMls

Add a `MlsMessage.Wrap(PrivateMessage pm)` static method to DotnetMls that produces the envelope. This is cleaner but requires a change in the dotnet-mls repo.

## Testing

1. **MarmotCs integration tests**: Existing `Messages_After_SelfUpdate_Still_Decrypt` should still pass (C#↔C# now both wrap and strip)
2. **Cross-MDK test**: After fixing the `i` tag issue, the message exchange step should work in both directions:
   - Rust → C#: Already works (C# strips envelope)
   - C# → Rust: Will work once C# wraps with envelope

## File Reference

| File | Repo | Purpose |
|------|------|---------|
| `Mdk.cs` | marmot-cs/src/MarmotCs.Core/ | CreateMessageAsync (needs wrapping), ProcessMessageAsync (already strips) |
| `MlsMessage.cs` | dotnet-mls/src/DotnetMls/Types/ | Existing MLSMessage type (used for parsing) |
| `WireFormat.cs` | dotnet-mls/src/DotnetMls/Types/ | WireFormat enum values |
| `CrossMdkRelayIntegrationTests.cs` | openChat/tests/OpenChat.Core.Tests/ | End-to-end bidirectional test |
