# Task: Fix NIP-42 AUTH handler not finding private key

## Status: COMPLETED

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P1

## Problem

`relay.thedude.cloud` requires NIP-42 authentication. The NostrService receives the AUTH challenge but reports "no private key or external signer available" — yet the user HAS a private key (`hasPrivKey=true` at line 52).

From log lines 5486-5488:
```
[INF] NIP-42 AUTH challenge from wss://relay.thedude.cloud: 73d71a4236a849a3
[WRN] NIP-42: cannot respond to AUTH — no private key or external signer available
[WRN] Event rejected by wss://relay.thedude.cloud: auth-required
```

The private key is stored in the user's profile and used for message signing, but the NIP-42 auth handler in NostrService doesn't have access to it. The key needs to be passed to NostrService after login.

## Goal

1. Trace how the private key is passed to NostrService for NIP-42 — check if `_subscribedUserPrivKey` is set
2. Ensure the private key is available in NostrService before any relay connection that might require NIP-42
3. This likely needs to be set during `InitializeAfterLoginAsync` alongside the Welcome subscription setup
