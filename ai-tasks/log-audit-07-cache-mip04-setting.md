# Task: Cache mip04_enabled setting instead of reading from DB per message

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P2

## Problem

`GetSetting: key=mip04_enabled` is called 1,477 times in the log — once per message when loading a chat. Loading a single conversation fires ~50 consecutive DB reads for the same value within milliseconds.

From log lines 729-782:
```
[DBG] GetSetting: key=mip04_enabled, found=true  (x50 in rapid succession)
```

## Goal

1. Cache the `mip04_enabled` value in memory after first read
2. Invalidate the cache only when the setting is explicitly changed via `SaveSetting`
3. Apply the same caching pattern to other frequently-read settings like `blossom_server_url`
