# Task: Add Blossom server fallback when upload is rejected

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P0

## Problem

After switching the Blossom server to `blossom.thedude.cloud`, both voice and media uploads fail with `401 Unauthorized: Pubkey not authorized by any storage rule`. The NIP-98 auth retry doesn't help because the server doesn't authorize the user's pubkey. There is no fallback, so the user is stuck with broken uploads until they manually change the setting back.

From log lines 49840-49842:
```
[WRN] Blossom upload attempt failed: "Unauthorized" Pubkey not authorized by any storage rule
[ERR] Failed to send media file
System.InvalidOperationException: Blossom upload failed: 401 Unauthorized: Pubkey not authorized by any storage rule
```

## Goal

1. When the configured Blossom server returns 401 after NIP-98 auth, fall back to the default server (`blossom.primal.net`)
2. Notify the user that the configured server rejected the upload and the default was used
3. Add a failing test that reproduces the fallback scenario before fixing
