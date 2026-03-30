# Task: Reduce NIP-46 Signer Timeout and Surface Errors Faster

## Status: Not Started

## Problem

When the NIP-46 signer relay rate-limits requests, the sign_event never reaches Amber. The app waits the full 180s timeout before showing an error, leaving the UI stuck with no feedback.

Observed in logs:
```
["OK","bd2942b7...",false,"rate-limited: you are noting too much"]
```
Followed 180s later by:
```
System.TimeoutException: Signer request timed out after 180s (sign_event)
```

## Goal

1. Reduce default signer timeout from 180s to 30-60s
2. Detect relay rejection ("OK" with `false`) for NIP-46 request events and fail immediately instead of waiting for timeout
3. Surface the error to the user quickly with a clear message ("Signer relay rejected request: rate-limited")
4. Consider retry with backoff or switching to a different signer relay
