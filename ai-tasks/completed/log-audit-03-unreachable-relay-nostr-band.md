# Task: Remove or replace unreachable relay.nostr.band for NIP-65 lookups

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P3

## Problem

Every NIP-65 relay list fetch from `wss://relay.nostr.band` fails — either with a timeout (`TaskCanceledException`) or `SocketException 113: No route to host`. This relay appears hardcoded for NIP-65 lookups but never successfully connects from the device.

From log lines 788, 1661:
```
[WRN] Failed to fetch relay list from wss://relay.nostr.band
System.Threading.Tasks.TaskCanceledException
```
```
System.Net.Sockets.SocketException (113): No route to host
```

## Goal

1. Replace `relay.nostr.band` with a more reliable relay for NIP-65 lookups, or use the user's already-connected relays
2. Add a timeout/circuit-breaker so repeated failures to the same relay don't keep wasting resources
