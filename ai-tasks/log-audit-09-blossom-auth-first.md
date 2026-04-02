# Task: Send NIP-98 auth on first Blossom upload attempt

## Status: Not Started

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P2

## Problem

Every Blossom upload to `blossom.primal.net` follows this pattern:
1. Anonymous upload → 400 "missing auth event"
2. Retry with NIP-98 auth → success

This wastes one round trip per upload. Since the server always requires auth, the first attempt is always futile.

From log lines 35031-35033:
```
[WRN] Blossom upload attempt failed: "BadRequest" missing auth event
[INF] Anonymous upload rejected, retrying with NIP-98 auth
[INF] Blossom upload succeeded
```

## Goal

1. Always include NIP-98 auth header on the first upload attempt
2. Remove the anonymous-first-then-retry logic, or make it opt-in for servers that support anonymous uploads
