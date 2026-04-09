# H2 - Validate Incoming Events Match Subscription Filters

**Severity**: HIGH
**Status**: DONE — implemented 2026-04-09

## Vulnerability

When receiving `EVENT` messages from relays, the code verifies signatures but never checks
that the event matches the subscription filter that was sent. A malicious relay can push
arbitrary events (wrong kind, wrong pubkey, wrong tags) and they'll be processed.

The existing check (NostrService lines 301-309) only validates that the subscription ID
**exists**. It does NOT validate event content against the registered filter.

## Design: NostrConnectionProvider as Centralized Firewall

Instead of per-connection validation, introduce a single enforcement point:

```
Relays → NostrRelayConnection(s) → NostrConnectionProvider (firewall) → NostrService
```

### Why a provider, not per-connection validation

- Single enforcement point — validation can't be bypassed or forgotten
- Unified subscription registry across all relays
- Shrinks NostrService by pulling out connection management
- Subscription state queryable in one place (debugging, reconnect)

---

## New Classes

### 1. ParsedFilter (`src/OpenChat.Core/Services/ParsedFilter.cs`)

Single source of truth for a subscription filter. Parsed once at registration,
used for matching (hot path) and serialized back to JSON on reconnect (rare path).
**No separate filterJson storage** — eliminates drift between stored JSON and parsed state.

```csharp
public class ParsedFilter
{
    public HashSet<int>? Kinds { get; private set; }
    public HashSet<string>? Authors { get; private set; }
    public HashSet<string>? Ids { get; private set; }
    public HashSet<string>? TagP { get; private set; }  // #p values
    public HashSet<string>? TagE { get; private set; }  // #e values
    public HashSet<string>? TagH { get; private set; }  // #h values
    public long? Since { get; private set; }             // unix timestamp
    public long? Until { get; private set; }             // unix timestamp

    // Parse from Nostr filter JSON (e.g. {"kinds":[1059],"#p":["abc123"]})
    public static ParsedFilter FromJson(string filterJson);

    // Match an event — every present field must match (AND per NIP-01).
    // Absent fields accept anything.
    public bool Matches(NostrEventReceived evt);

    // Serialize back to JSON for REQ replay on reconnect.
    // This is the ONLY serialization path — no stored filterJson.
    public string ToFilterJson();
}
```

**Matching rules (NIP-01):**
- `Kinds`: event.Kind must be in the set
- `Authors`: event.PublicKey must be in the set
- `Ids`: event.EventId must be in the set
- `TagP`: event must have a "p" tag whose value is in the set
- `TagE`: event must have an "e" tag whose value is in the set
- `TagH`: event must have an "h" tag whose value is in the set
- `Since`: event.CreatedAt (as unix timestamp) must be >= Since
- `Until`: event.CreatedAt (as unix timestamp) must be <= Until
- All present fields must match (AND). Absent fields are ignored.

### 2. TrackedSubscription (`src/OpenChat.Core/Services/TrackedSubscription.cs`)

```csharp
public class TrackedSubscription
{
    public string SubId { get; }
    public ParsedFilter Filter { get; }              // THE source of truth
    public HashSet<string> ActiveRelays { get; }     // relay URLs carrying this sub
    public DateTimeOffset CreatedAt { get; }
}
```

**Key design decisions:**
- Subscription is the first-class concept (not the relay)
- `Filter` is the single source of truth — no filterJson stored alongside
- `ActiveRelays` tracks which relays carry this sub — on reconnect, replay for that relay;
  on unsubscribe, remove relay and only send CLOSE when set empties

### 3. NostrConnectionProvider (`src/OpenChat.Core/Services/NostrConnectionProvider.cs`)

```csharp
public class NostrConnectionProvider : IAsyncDisposable
{
    // Connection management
    NostrRelayConnection GetOrCreateConnection(string relayUrl);
    Task ConnectAsync(string relayUrl);
    Task DisconnectAsync(string relayUrl);
    Task DisconnectAllAsync();
    IReadOnlyList<string> ConnectedRelayUrls { get; }

    // Subscription registry (the unified brain)
    Task<string> SubscribeAsync(string subIdPrefix, ParsedFilter filter, IEnumerable<string> relayUrls);
    Task UnsubscribeAsync(string subId);
    TrackedSubscription? GetSubscription(string subId);

    // Event stream (post-firewall — only validated events reach consumers)
    IObservable<(string relayUrl, string message)> ValidatedMessages { get; }

    // The firewall: called internally before emitting to ValidatedMessages
    // Returns true if event passes, false if dropped
    bool ValidateEvent(string subId, NostrEventReceived evt);
}
```

**Firewall flow:**
1. `NostrRelayConnection` receives raw message from relay
2. Provider extracts subscription ID from `root[1]`
3. Provider looks up `TrackedSubscription` by subId — unknown subId → drop + log
4. Provider parses event, calls `filter.Matches(evt)` — mismatch → drop + log
5. Only matching events emitted on `ValidatedMessages`

---

## Modified Classes

### NostrRelayConnection (`src/OpenChat.Core/Services/NostrRelayConnection.cs`)

**Remove:**
- `_activeSubscriptions` dictionary (moves to provider)
- `RegisterSubscriptionAsync` / `UnregisterSubscriptionAsync` (moves to provider)
- `HasSubscription` (moves to provider)
- `ReplaySubscriptionsAsync` (provider handles replay)

**Keep:**
- `ConnectAsync`, `DisconnectAsync`, `SendAsync` (raw transport)
- `Messages` observable (raw message stream — provider subscribes to this)
- `ConnectionStatus` observable
- Auto-reconnect logic (but notify provider on reconnect so it can replay)

### NostrService (`src/OpenChat.Core/Services/NostrService.cs`)

**Replace:**
- `ConcurrentDictionary<string, NostrRelayConnection> _relayConnections` → inject `NostrConnectionProvider`
- All 4 `RegisterSubscriptionAsync` call sites → `provider.SubscribeAsync(...)`
- H2 check at lines 301-309 → removed (provider does this before events arrive)
- Connection management methods → delegate to provider

**Keep untouched (~1,100+ lines):**
- `ProcessRelayMessageAsync` event routing (kind 1059, 445, etc.)
- `HandleAuthChallengeAsync` (NIP-42)
- Gift wrap / unwrap logic (NIP-59)
- All publish methods
- Key management, signature verification

---

## TDD Test Plan

All tests use xUnit. Run with: `dotnet test --filter "FullyQualifiedName~<TestClass>"`

### ParsedFilterTests (`tests/OpenChat.Core.Tests/ParsedFilterTests.cs`)

**Parsing from JSON:**
- `Parse_KindsOnly_ExtractsKinds` — `{"kinds":[1059]}` → Kinds = {1059}
- `Parse_MultipleKinds_ExtractsAll` — `{"kinds":[1059,445,0]}` → 3 kinds
- `Parse_TagP_ExtractsPubkeys` — `{"kinds":[1059],"#p":["abc","def"]}` → TagP = {"abc","def"}
- `Parse_TagH_ExtractsGroupIds` — `{"kinds":[445],"#h":["g1","g2"]}` → TagH = {"g1","g2"}
- `Parse_TagE_ExtractsEventIds` — `{"#e":["eid1"]}` → TagE = {"eid1"}
- `Parse_Authors_ExtractsAuthors` — `{"authors":["pk1","pk2"]}` → Authors = {"pk1","pk2"}
- `Parse_Ids_ExtractsIds` — `{"ids":["id1","id2"]}` → Ids = {"id1","id2"}
- `Parse_Since_ExtractsTimestamp` — `{"since":1700000000}` → Since = 1700000000
- `Parse_Until_ExtractsTimestamp` — `{"until":1800000000}` → Until = 1800000000
- `Parse_EmptyFilter_AllFieldsNull` — `{}` → all null
- `Parse_InvalidJson_Throws` — `"not json"` → ArgumentException

**Matching events:**
- `Matches_CorrectKind_ReturnsTrue` — filter kinds:[1059], event kind=1059 → true
- `Matches_WrongKind_ReturnsFalse` — filter kinds:[1059], event kind=445 → false
- `Matches_NoKindsInFilter_AcceptsAnyKind` — filter {}, event kind=9999 → true
- `Matches_CorrectAuthor_ReturnsTrue` — filter authors:["abc"], event pubkey="abc" → true
- `Matches_WrongAuthor_ReturnsFalse` — filter authors:["abc"], event pubkey="wrong" → false
- `Matches_CorrectTagP_ReturnsTrue` — filter #p:["mypk"], event has p tag "mypk" → true
- `Matches_WrongTagP_ReturnsFalse` — filter #p:["mypk"], event has p tag "other" → false
- `Matches_MissingTagP_ReturnsFalse` — filter #p:["mypk"], event has no p tag → false
- `Matches_CorrectTagH_ReturnsTrue` — same pattern for #h
- `Matches_WrongTagH_ReturnsFalse`
- `Matches_CorrectTagE_ReturnsTrue` — same pattern for #e
- `Matches_CorrectId_ReturnsTrue` — filter ids:["abc"], event id="abc" → true
- `Matches_WrongId_ReturnsFalse`
- `Matches_EventBeforeSince_ReturnsFalse` — event timestamp < since → false
- `Matches_EventAfterSince_ReturnsTrue`
- `Matches_EventAfterUntil_ReturnsFalse` — event timestamp > until → false
- `Matches_AllCriteriaMustMatch_KindOk_AuthorWrong_ReturnsFalse` — AND logic
- `Matches_AllCriteriaMustMatch_AllOk_ReturnsTrue`
- `Matches_MultipleTagValues_AnyMatchSuffices` — filter #h:["g1","g2"], event h="g2" → true

**Serialization (round-trip):**
- `ToFilterJson_RoundTrips_KindsAndTagP` — parse → serialize → reparse → same fields
- `ToFilterJson_RoundTrips_KindsAndTagH_WithSince` — includes since timestamp
- `ToFilterJson_EmptyFilter_ProducesEmptyObject` — {} → "{}"

### TrackedSubscriptionTests (`tests/OpenChat.Core.Tests/TrackedSubscriptionTests.cs`)

- `Constructor_SetsSubIdAndFilter` — fields set correctly
- `AddRelay_AppearsInActiveRelays`
- `RemoveRelay_DisappearsFromActiveRelays`
- `RemoveRelay_LastOne_ActiveRelaysEmpty`
- `HasRelay_ReturnsTrueForActive_FalseForUnknown`
- `Filter_IsSameInstancePassedToConstructor` — no copies, no drift

### NostrConnectionProviderTests (`tests/OpenChat.Core.Tests/NostrConnectionProviderTests.cs`)

**Subscription tracking:**
- `SubscribeAsync_CreatesTrackedSubscription`
- `SubscribeAsync_AddsRelaysToSubscription`
- `UnsubscribeAsync_RemovesSubscription`
- `GetSubscription_ReturnsNullForUnknown`

**Firewall (the H2 fix):**
- `ValidateEvent_MatchingEvent_ReturnsTrue`
- `ValidateEvent_WrongKind_ReturnsFalse`
- `ValidateEvent_WrongTagP_ReturnsFalse`
- `ValidateEvent_UnknownSubId_ReturnsFalse`
- `ValidateEvent_EmptyFilter_AcceptsAll`

**Real filter patterns used in codebase:**
- `ValidateEvent_WelcomeFilter_AcceptsKind1059WithCorrectP`
  - filter: `{"kinds":[1059],"#p":["userpubkey"]}`
  - accept: kind=1059, p tag matches
  - reject: kind=445 (wrong kind), kind=1059 with different p tag
- `ValidateEvent_GroupFilter_AcceptsKind445WithCorrectH`
  - filter: `{"kinds":[445],"#h":["group1","group2"]}`
  - accept: kind=445, h tag = "group1"
  - reject: kind=1059, kind=445 with h tag = "group3"
- `ValidateEvent_GroupFilterWithSince_RejectsOldEvents`
  - filter: `{"kinds":[445],"#h":["g1"],"since":1700000000}`
  - reject: event with timestamp < since

---

## Implementation Order (TDD red-green-refactor)

### Phase 1: ParsedFilter (pure, zero dependencies)
1. RED: Create `ParsedFilterTests.cs` with all tests above
2. RED: Create `ParsedFilter.cs` stub (NotImplementedException) — tests compile, all fail
3. GREEN: Implement `FromJson` — parsing tests pass
4. GREEN: Implement `Matches` — matching tests pass
5. GREEN: Implement `ToFilterJson` — serialization tests pass

### Phase 2: TrackedSubscription (pure, depends only on ParsedFilter)
6. RED: Create `TrackedSubscriptionTests.cs`
7. RED: Create `TrackedSubscription.cs` stub
8. GREEN: Implement — all tests pass

### Phase 3: NostrConnectionProvider (depends on TrackedSubscription + NostrRelayConnection)
9. RED: Create `NostrConnectionProviderTests.cs`
10. RED: Create `NostrConnectionProvider.cs` stub
11. GREEN: Implement subscription tracking + firewall
12. GREEN: Wire to NostrRelayConnection.Messages observable

### Phase 4: Refactor NostrService (swap internals, keep behavior)
13. Run ALL existing NostrService tests — capture baseline
14. Inject NostrConnectionProvider into NostrService
15. Replace 4 RegisterSubscriptionAsync sites → provider.SubscribeAsync
16. Replace connection management → delegate to provider
17. Remove H2 check at lines 301-309 (provider handles this)
18. Run ALL existing tests — must still pass (no regressions)

### Phase 5: Cleanup
19. Remove subscription tracking from NostrRelayConnection
20. Update NostrRelayConnectionTests (remove subscription-related tests)
21. Run full test suite
22. Update this task file → DONE

---

## Risk Mitigations

| Risk | Mitigation |
|------|-----------|
| Filter JSON parse failure | `FromJson` wraps in try-catch; on parse error throw `ArgumentException` — caller logs and handles |
| Tag keys vary (#p, #h, #e) | Match exact key names used in existing `Dictionary<string, object>` serialization |
| Gift wraps (NIP-59) have randomized timestamps | Existing Welcome filters have NO `since` field → timestamp check not triggered |
| Performance: matching on every event | HashSet.Contains = O(1) per field; ParsedFilter parsed once, matched many times; cheaper than current approach |
| Reconnect serialization correctness | `ToFilterJson` round-trip tested — parse → serialize → reparse → same fields |
| Regression during NostrService refactor | Phase 4 runs all existing tests before AND after changes |

## Battery Impact (Mobile)

- **Neutral to slightly positive**
- Hot path: few HashSet.Contains calls per event (nanoseconds)
- Saves CPU: invalid events dropped before expensive NIP-59 gift wrap decryption
- No change to WebSocket count, reconnect behavior, or KeepAlive interval
- `ToFilterJson` serialization only on reconnect (rare), not per event

## Out of Scope

The 5 ephemeral fetch methods (lines 1670-2353) use raw `ClientWebSocket` with inline kind
checks. These are short-lived request-response patterns with smaller attack surface. Not
worth the regression risk in this change. Can migrate to provider later if needed.
