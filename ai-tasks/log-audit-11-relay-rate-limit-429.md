# Task: Handle relay HTTP 429 rate-limiting gracefully

## Status: Not Started

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P1

## Problem

`wss://relay.thedude.cloud` returns HTTP 429 (Too Many Requests) 16 times when the app reconnects too aggressively. The app treats this as a generic connection failure and retries immediately, making the problem worse.

From log line 4085:
```
[ERR] Failed to connect to relay: wss://relay.thedude.cloud
System.Net.WebSockets.WebSocketException: net_WebSockets_ConnectStatusExpected, 429, 101
```

## Goal

1. Detect HTTP 429 during WebSocket connection and apply a longer backoff (e.g. 30-60s)
2. If the relay provides a `Retry-After` header, honor it
3. This is closely related to log-audit-10 (exponential backoff) — can be implemented together
