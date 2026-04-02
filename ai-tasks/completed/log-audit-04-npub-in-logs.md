# Task: Stop logging full npub and truncate pubkeys in logs

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P1

## Problem

The full npub (`npub13tq2ykqpxldzj2md9tsd325jrgv9dct90j2he776pw83jxqt47lq3spzpu`) is logged dozens of times across startup, reconnect, and chat loading. Since logs can be exported and shared (line 69697 shows an export), anyone reading the log can correlate all activity to a specific Nostr identity.

## Goal

1. Truncate npub to first 8 characters in all log messages (e.g. `npub13tq...`)
2. Review all `Log.Information` / `Log.Debug` calls in `StorageService`, `ShellViewModel`, and `MainViewModel` that log the full npub
3. The profile hex prefix (e.g. `8ac0a2580137da29`) is already truncated in some places — apply the same approach consistently
