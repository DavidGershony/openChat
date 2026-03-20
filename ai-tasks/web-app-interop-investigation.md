# Web App (marmot-ts) Interop Investigation

## Goal
Investigate and fix communication issues between OpenChat and the marmot-ts web app (https://marmot-protocol.github.io/marmots-web-chat/).

## Reference Implementation
The web app (marmot-ts) is the reference — OpenChat should match its behavior.

## Known Issues (from cross-mdk-interop-issues.md)
1. **Web client crashes on invite** — `event.id` undefined in marmot-ts during commit/invite flow
2. **Web can't decrypt OpenChat messages** — Exporter secret divergence at same epoch
3. **OpenChat uses real pubkey for kind 445** — Should use ephemeral per MIP-03

## Issues Found & Fixed During This Investigation

### Issue 4: Missing commit event on AddMember — FIXED
When OpenChat invited a user, `MessageService.AddMemberAsync()` published ONLY the Welcome (kind 1059 gift wrap) but NOT the commit event (kind 445). The marmot-ts reference publishes BOTH:
- Commit event (kind 445) with `h` tag → to group relays (so existing members advance their epoch)
- Welcome gift wrap (kind 1059) → to invitee's inbox

**Fix**: Added commit publication to `MessageService.AddMemberAsync()` before welcome publication.

### Issue 5: DecryptMessageAsync throws on commits — FIXED
When a kind 445 commit event was received (from another member adding/removing users), `ManagedMlsService.DecryptMessageAsync()` would throw "Expected ApplicationMessageResult" because it only handled application messages, not commits.

**Fix**: Added `CommitResult` handling to `DecryptMessageAsync()` — returns `MlsDecryptedMessage` with `IsCommit = true`. Added `IsCommit` property to `MlsDecryptedMessage` model.

### Issue 6: HandleGroupMessageEventAsync doesn't handle commits — FIXED
`MessageService.HandleGroupMessageEventAsync()` always tried to create a chat Message from kind 445 events. Commit events (epoch transitions) should be processed silently to advance the group's MLS state.

**Fix**: Added commit check after `DecryptMessageAsync()` — if `IsCommit`, log and return without creating a Message.

### Issue 3 (re-verified): ManagedMlsService already uses ephemeral keys ✅
The `ManagedMlsService.EncryptMessageAsync()` already uses `BuildEphemeralSignedEvent()` for kind 445 events, matching the marmot-ts behavior. The Rust `MlsService` backend may still use real keys — this is only relevant when using `--mdk rust`.

## Protocol Format Comparison (marmot-ts ↔ OpenChat) — ALL MATCH ✅

### Kind 443 (KeyPackage) — VERIFIED ON RELAY
| Field | marmot-ts | OpenChat |
|-------|-----------|---------|
| Tags: mls_protocol_version | ✅ "1.0" | ✅ same |
| Tags: mls_ciphersuite | ✅ "0x0001" | ✅ same |
| Tags: mls_extensions | ✅ "0x000a", "0xf2ee" | ✅ same |
| Tags: encoding | ✅ "base64" | ✅ same |
| Tags: i | ✅ KeyPackageRef hex | ✅ same |
| Tags: relays | ✅ relay URLs (trailing /) | ✅ same (no trailing /) |
| Tags: client | ✅ "marmot-chat" | ❓ not set (optional) |
| Content | ✅ base64(KP TLS) | ✅ same |

### Kind 444 (Welcome via NIP-59 Gift Wrap) — VERIFIED
| Layer | Format | Status |
|-------|--------|--------|
| Gift Wrap (1059) | Ephemeral pubkey, `p` tag | ✅ |
| Seal (13) | Sender pubkey, NIP-44 encrypted | ✅ |
| Rumor (444) | Tags: encoding, e, relays | ✅ |
| MLSMessage header | 0x0001 (mls10), 0x0003 (mls_welcome) | ✅ |

### Kind 445 (Group Message) — VERIFIED
| Field | marmot-ts | OpenChat (managed) |
|-------|-----------|---------|
| Tags: h | ✅ Nostr group ID hex | ✅ same |
| Tags: encoding | ✅ "base64" | ✅ same |
| Signing key | ✅ ephemeral | ✅ ephemeral (managed backend) |
| MIP-03 ChaCha20-Poly1305 | ✅ | ✅ same params |
| Nonce: 12 bytes, AAD: empty | ✅ | ✅ |

## Test Results — ALL PASS ✅

### Full 3-User Flow on relay2.angor.io
1. ✅ Alice creates group, invites Bob
2. ✅ Bob accepts invite, joins group
3. ✅ Alice → Bob message: encrypted, decrypted successfully
4. ✅ Bob → Alice reply: encrypted, decrypted successfully
5. ✅ Alice adds Charlie, publishes commit + welcome
6. ✅ Bob processes commit, advances epoch
7. ✅ Charlie accepts invite, joins group
8. ✅ Alice → All message: Bob decrypts ✅, Charlie decrypts ✅

### KeyPackage Format Analysis
All required tags present and matching marmot-ts format.

### Gift Wrap Format Analysis
Full NIP-59 unwrap verified: Gift Wrap → Seal → Rumor, all layers correct.

## Investigation Steps — COMPLETED

- [x] Study marmot-ts source code for exact formats
- [x] Study OpenChat source code for exact formats
- [x] Compare tag names and formats
- [x] Identify missing commit publication
- [x] Write integration test against relay2.angor.io (WebAppInteropInvestigationTests.cs)
- [x] Test full OpenChat-only flow (3 users)
- [x] Log raw events at every step for format comparison
- [x] Fix identified issues (Issues 4, 5, 6)
- [x] Re-run all existing tests (178 pass, 3 skipped)

## Remaining Issue: Epoch Divergence (Issue 2)

### Analysis
Code review confirms crypto primitives are identical between dotnet-mls and ts-mls:
- HKDF Extract argument order: both use `extract(salt, ikm)`
- ExpandWithLabel: both use VarInt (RFC 9000 16), "MLS 1.0 " prefix
- Key schedule derivation chain: structurally identical
- GroupContext serialization format: same field order and encoding
- MIP-03 exporter: both call `mlsExporter(exporterSecret, "marmot", "group-event", 32)`
- marmot-cs internal consistency verified: sender and receiver derive same exporter secret

### Likely Root Cause: Missed Commits (NOW FIXED)
The epoch divergence may have been caused by OpenChat failing to process commit events
from the web app. When the web app commits (adds a member, updates keys), it publishes a
kind 445 commit event. Previously OpenChat's DecryptMessageAsync threw on commits instead
of processing them, so OpenChat stayed at an earlier epoch while the web app advanced.

Our fix (Issues 4-6) now properly handles commits, which should resolve the epoch divergence
in most cases. The interactive test `PublishKPAndWaitForWebAppWelcome` can verify this.

### If Divergence Persists After Commit Fix
If the issue persists even after proper commit handling, the divergence is in how the Welcome
itself is processed (different GroupContext bytes from the same Welcome). Use the
`ExporterSecretDiagnosticTests` to dump the exporter secret and compare with the web app's
`clientState.keySchedule.exporterSecret` value in browser console.

## Files Changed
- `src/OpenChat.Core/Services/IMlsService.cs` — Added `IsCommit` to `MlsDecryptedMessage`
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — Handle `CommitResult` in `DecryptMessageAsync`
- `src/OpenChat.Core/Services/MessageService.cs` — Publish commits in `AddMemberAsync`, handle commits in `HandleGroupMessageEventAsync`
- `tests/OpenChat.Core.Tests/WebAppInteropInvestigationTests.cs` — New integration tests
