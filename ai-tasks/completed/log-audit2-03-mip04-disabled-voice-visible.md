# Task: Hide voice/media buttons when MIP-04 is disabled

## Status: COMPLETED

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P1

## Problem

MIP-04 media encryption is loaded as `false` but the user can still attempt to send voice messages, which require MIP-04 encryption. All attempts fail because there's no media exporter secret without MIP-04.

From log lines 31 and 3396:
```
[INF] Loaded MIP-04 setting: false
...
[ERR] Failed to send voice message
System.InvalidOperationException: Blossom upload failed: 401 Unauthorized
```

## Goal

1. When MIP-04 is disabled, hide or disable the voice recording button and file attach button in ChatViewModel
2. Alternatively, support sending voice/media without MIP-04 encryption (unencrypted upload) if that's the desired behavior
3. Show a clear message if the user somehow triggers a media send with MIP-04 off
