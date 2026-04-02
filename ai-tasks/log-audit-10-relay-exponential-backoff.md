# Task: Add exponential backoff for relay reconnection

## Status: Not Started

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P1

## Problem

250 "Error listening to relay" entries and 50+ reconnect cycles in one day. The app reconnects aggressively with no backoff — at one point 8 reconnects happen in 7 minutes (14:36–14:43). This causes:
- Wasted battery and bandwidth on mobile
- Rate limiting from relays (issue #11)
- Log noise

From log:
```
[ERR] Error listening to relay wss://nos.lol
System.Net.WebSockets.WebSocketException: net_WebSockets_ConnectionClosedPrematurely_Generic
  → SocketException (103): Software caused connection abort
```

## Goal

1. Implement exponential backoff for relay reconnection (e.g. 1s, 2s, 4s, 8s, max 60s)
2. Reset backoff timer on successful connection that stays up for a threshold (e.g. 30s)
3. Log the backoff delay so it's visible in debug logs
