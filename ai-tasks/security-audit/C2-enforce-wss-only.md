# C2 - Enforce WSS-Only Relay Connections

**Severity**: CRITICAL
**Status**: COMPLETED

## Context

`NostrService.ValidateRelayUrlAsync()` at line 2751 accepts both `ws://` and `wss://`. Unencrypted WebSocket connections expose all Nostr event traffic (including NIP-42 AUTH challenges) to network eavesdropping and MITM.

The default relays in `NostrConstants.cs` already use `wss://`, but users can add custom relays.

## Files
- `src/OpenChat.Core/Services/NostrService.cs` (line 2751)
- `tests/OpenChat.Core.Tests/RelayUrlValidationTests.cs` (line 16 tests ws:// as valid -- must update)

## Tasks
- [ ] Change validation to reject `ws://` -- only allow `wss://`
- [ ] Update existing test that expects `ws://` to be valid
- [ ] Add test: `ws://relay.example.com` is rejected with clear error message
- [ ] Add test: `wss://relay.example.com` still passes
- [ ] Consider: allow `ws://` ONLY when `--allow-local-relays` flag is set (for local development)
