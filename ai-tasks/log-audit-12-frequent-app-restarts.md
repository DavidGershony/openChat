# Task: Investigate 27 app restarts in a single day

## Status: Not Started

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P1

## Problem

The app was started 27 times in ~20 hours. Several pairs are only 1 second apart (e.g. 17:40:12 and 17:40:13, 17:44:56 and 17:44:57), suggesting crash-restart loops or Android killing and immediately restarting the process.

From log:
```
17:40:12 === OpenChat Application Started ===
17:40:13 === OpenChat Application Started ===
...
17:44:56 === OpenChat Application Started ===
17:44:57 === OpenChat Application Started ===
```

## Goal

1. Add unhandled exception logging (if not already present in Android) so crashes are captured before the process dies
2. Check if Android is killing the app due to excessive memory or battery usage (related to issues #6, #8, #10)
3. Log the reason for app start if detectable (cold start, warm restart, crash recovery)
4. Once backoff and caching fixes land (issues #8, #10), re-evaluate whether restart frequency drops
