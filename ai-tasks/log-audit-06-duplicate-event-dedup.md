# Task: Deduplicate relay events earlier to reduce log noise and processing

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P2

## Problem

15,840 "Skipping duplicate event" log entries in a single day — ~23% of the entire log. The app subscribes to 4 relays and processes every incoming event through the full receive pipeline before checking for duplicates. This wastes CPU, battery, and makes logs hard to read.

## Goal

1. Move deduplication to the relay listener level — check event ID against a seen-set before dispatching to `MessageService`
2. Log duplicate skips at `Verbose`/`Trace` level instead of `Debug` so they don't pollute normal debug logs
3. Consider using a bounded `HashSet` or bloom filter for the seen-set to cap memory usage
