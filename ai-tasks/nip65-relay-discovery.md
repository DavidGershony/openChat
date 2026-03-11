# NIP-65 Relay Discovery Implementation

## Overview

Implement NIP-65 relay list metadata discovery so the app knows which relays a user reads from and writes to, instead of relying on hardcoded relay lists.

## Background

- **NIP-65**: Users publish kind 10002 (replaceable) events listing their relay preferences
- **Discovery relays**: Specialized relays like purplepages (`wss://purplepag.es`) index kind 10002 events for efficient lookup
- **Current state**: App hardcodes `relay.damus.io`, `nos.lol`, `relay.nostr.band` everywhere

## Kind 10002 Event Structure

```json
{
  "kind": 10002,
  "tags": [
    ["r", "wss://relay.damus.io", "read"],
    ["r", "wss://nos.lol", "write"],
    ["r", "wss://relay.example.com"],
    ["r", "wss://inbox.nostr.band", "read"]
  ],
  "content": ""
}
```

- Each `r` tag has a relay URL and optional marker: `read`, `write`, or omitted (both)
- This is a replaceable event (kind 10000-19999) — only the latest one matters

## Required Changes

### 1. Fetch Relay List on Login (existing key)

When a user logs in with an existing key:
1. Query discovery relays (`wss://purplepag.es`, `wss://relay.nostr.band`) for kind 10002 by the user's pubkey
2. Parse the relay list into read/write/both categories
3. Store in `ProfileConfiguration` or a new `UserRelayList` model
4. Connect to those relays instead of (or in addition to) the hardcoded defaults
5. Fall back to defaults if no kind 10002 found

### 2. Publish Relay List on New Key / Settings Change

When a user creates a new key OR changes their relay list in Settings:
1. Build a kind 10002 event with the user's configured relays
2. Mark relays as read, write, or both based on user preference (default: both)
3. Publish to connected relays AND to discovery relays (`wss://purplepag.es`)
4. Also publish when generating a new KeyPackage (the KeyPackage's `relays` tag should match)

### 3. Use Relay Lists for Other Users

When we need to interact with another user (fetch their KeyPackages, send them a Welcome):
1. Query discovery relays for their kind 10002
2. Fetch their KeyPackages from their **write** relays (where they publish)
3. Send Welcomes/messages to their **read** relays (where they receive)
4. Fall back to checking default relays

### 4. Marmot/MDK Integration

MDK does NOT manage its own relay connections — all relay comms go through `NostrService`. The integration points are:

- **KeyPackage publishing**: The `relays` tag in kind 443 events should list the user's write relays (from NIP-65)
- **Welcome relay hints**: The `relays` tag in kind 444 events should list the group's relay list
- **Group message subscription**: Use relay hints from Welcome events to subscribe to group messages on the right relays

No changes needed inside MDK itself — the relay discovery feeds into `NostrService` which MDK already uses.

## Implementation Plan

### NostrService changes:
- Add `FetchRelayListAsync(string publicKeyHex)` → queries discovery relays for kind 10002
- Add `PublishRelayListAsync(List<RelayPreference> relays, string? privateKeyHex)` → publishes kind 10002
- Modify `FetchKeyPackagesAsync` to first discover target user's relays, then query those
- Modify `PublishWelcomeAsync` to send to recipient's read relays

### New model:
```csharp
public class RelayPreference
{
    public string Url { get; set; }
    public RelayUsage Usage { get; set; } // Read, Write, Both
}

public enum RelayUsage { Read, Write, Both }
```

### ProfileConfiguration / StorageService:
- Store the user's relay list (persisted across restarts)
- Load on startup, use for initial connections

### SettingsViewModel:
- Allow marking relays as read/write/both
- Publish kind 10002 on relay list changes

### Discovery relay constants:
```
wss://purplepag.es     — primary NIP-65 discovery relay
wss://relay.nostr.band — also indexes kind 10002
```

## Flow Diagram

```
Login with existing key:
  1. Connect to discovery relays
  2. Fetch kind 10002 for own pubkey
  3. Parse relay list → connect to user's relays
  4. Subscribe to welcomes/group messages on those relays

Invite a user:
  1. Fetch their kind 10002 from discovery relay
  2. Fetch their KeyPackages from their WRITE relays
  3. Send Welcome to their READ relays

Publish KeyPackage:
  1. Generate KeyPackage
  2. Include own write relays in `relays` tag
  3. Publish to own write relays + discovery relays
  4. Publish/update kind 10002 if relay list changed
```
