# Auto-download media with local cache

## Status: In Progress

## Problem

When MIP-04 is enabled, users must manually tap "Tap to load" for every media message.
When returning to a chat, previously loaded media is gone (only held in memory).

## Solution

1. When MIP-04 is enabled, auto-download and decrypt media on message receive
2. Cache decrypted files to disk in the profile's media directory
3. On chat reload, check cache first — show cached media instantly

## Cache location

`{ProfileDataDirectory}/media/{messageId}.{ext}`

## Tasks

- [x] Add media cache service (save/load decrypted bytes by message ID)
- [x] On MessageViewModel creation, check cache and load if present
- [x] After successful download+decrypt, save to cache
- [x] Auto-trigger download for new media messages when MIP-04 enabled
- [ ] Run tests
