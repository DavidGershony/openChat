# Task: Cache decrypted current user instead of repeated Keystore unprotect calls

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P2

## Problem

470 `Unprotecting X bytes with Android Keystore` calls in a single day. Every call to `GetCurrentUser()` triggers two Keystore decrypt operations (96 bytes + 95 bytes). This happens on startup, every reconnect, every chat load, and every message operation.

## Goal

1. Cache the decrypted user object in `StorageService` after first successful retrieval
2. Invalidate the cache on logout or user switch
3. This also reduces the 4x redundant `Retrieved current user` calls during startup to 1
