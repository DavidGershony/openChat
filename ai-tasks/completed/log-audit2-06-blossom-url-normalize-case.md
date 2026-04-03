# Task: Normalize Blossom server URL to lowercase on save

## Status: COMPLETED

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P3

## Problem

The saved Blossom URL has wrong casing: `https://Blossom.thedude.cloud` (capital B). While HTTP hostnames are case-insensitive per RFC, some servers or CDN configurations may behave unexpectedly with non-lowercase hostnames.

From log line 34:
```
[INF] Blossom server URL updated: https://Blossom.thedude.cloud
```

## Goal

1. Normalize the Blossom server URL to lowercase when saving in SettingsViewModel
2. Apply the same normalization to relay URLs if not already done
