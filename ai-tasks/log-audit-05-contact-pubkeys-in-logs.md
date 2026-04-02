# Task: Truncate contact pubkeys in log output

## Status: Not Started

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P3

## Problem

Full hex pubkeys of contacts (e.g. `859bad91fda1f28c`, `e9b03d7d20c787ce`) are logged in message handling, Welcome events, and chat loading. Combined with timestamps, this reveals the user's contact graph — a metadata leak if the log is shared.

## Goal

1. Ensure all contact/sender pubkeys are truncated to 8 hex chars in log output
2. Review `MessageService`, `NostrService`, and `ChatViewModel` log calls
