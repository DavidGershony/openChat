# NPub-Based Auto-Profile Design

## Requirements

### Core
1. Each npub gets its own isolated profile folder (DB, logs, MLS state, everything)
2. Profile name derived from npub (e.g., first 16 chars of hex pubkey)
3. On login, the app uses the correct profile for that npub automatically
4. Data from different users must never leak across profiles

### Override
5. `--profile <name>` CLI flag overrides auto-detection (for testing, dev, etc.)
6. `--profile` value cannot be an npub (prevent confusion)

### UX
7. First launch with a new npub should "just work" — no manual profile setup
8. Returning to the app should auto-login to the last used npub/profile
9. Logout should return to login screen, not auto-login again
10. Switching accounts (logout + login with different npub) should switch profiles seamlessly

### Technical Constraints
11. `ProfileConfiguration` is static and set once — it determines DB path, log dir, etc.
12. All services (StorageService, NostrService, MlsService) are created in `App.axaml.cs` before login
13. Services hold open connections (SQLite, WebSockets) and are not designed for re-initialization
14. The login flow (nsec or Amber) happens inside `MainViewModel` after services already exist

## The Core Problem

**We need to know the npub BEFORE creating services, but the npub is determined DURING login (which happens AFTER services are created).**

For returning users this is solvable (read `last_user.json` at startup). For first-time users or account switches, there's a gap.

---

## Alternatives

| # | Approach | Description | Pros | Cons | Complexity |
|---|----------|-------------|------|------|------------|
| 1 | **Restart on mismatch** | Start with last-known profile. After login, if npub doesn't match, save user to target profile DB, update registry, restart process. | Simple to implement. No service changes. Works for all cases. | Visible restart on first login or account switch. Feels like a hack. | Low |
| 2 | **Two-phase startup** | Phase 1: show only login screen with a lightweight "lobby" (no DB, no relays). Phase 2: after login, set profile, create all services, show main UI. | No restart. Clean separation. Correct architecture. | Requires refactoring App.axaml.cs to delay service creation. LoginViewModel currently depends on StorageService (for auto-login). Need a "lobby" StorageService for the registry only. | Medium-High |
| 3 | **Lazy service initialization** | Create service objects at startup but don't initialize them. After login, call `Initialize(profilePath)` on each service. Services hold no connections until initialized. | No restart. Services created once. Profile set after login. | Every service needs an `Initialize` method. Must audit all services for early access. StorageService constructor currently opens DB. Breaking change across the codebase. | High |
| 4 | **Login-first with registry** | At startup, read `last_user.json`. If found, derive profile and init normally (auto-login path). If NOT found (no saved user), show a minimal login screen that only collects the npub/nsec — no services needed yet. After collecting identity, set profile, THEN create services and proceed. | No restart for returning users. First-time login is lightweight. Correct profile from the start. | Login screen needs two modes: "lightweight" (no services) and "full" (with services for Amber/relay). Amber login requires relay connection (NostrService) which needs a profile first — chicken-and-egg for signer users. | Medium |
| 5 | **Re-initializable StorageService** | Keep current startup flow. After login, if profile mismatch, call `StorageService.SwitchDatabase(newPath)` which closes the old connection and opens a new one. Other services get new data from the new DB. | No restart. Minimal interface change (one method). Only StorageService changes. | Need to verify no service caches stale data from old DB. MLS state, relay lists, chat lists all need reloading. Essentially a "soft restart" of the data layer. MessageService holds a pubkey from init. | Medium |
| 6 | **Profile = DB file, not folder** | Don't use ProfileConfiguration folders at all. Instead, use a single app folder but name the DB file by pubkey: `openchat_{first16hex}.db`. MLS state key includes pubkey prefix. Logs shared. | No restart. No folder switching. Minimal change. Profile isolation at DB level. | Logs not isolated. MLS persisted state (saved via StorageService) needs key prefixing. Less clean than folder isolation. Doesn't match existing `--profile` folder model. | Low-Medium |
| 7 | **Default lobby profile + switch** | Always start with a "default" lobby profile. After login, read `last_user.json` or derive from new npub. Call `ProfileConfiguration.SwitchProfile(name)` which updates all paths. Then call `StorageService.SwitchDatabase(newPath)` + `MlsService.ResetAsync()` + reload UI data. Default profile holds only the login screen state (signer session, relay for NIP-46). User data lives in per-npub profile folders. | No restart. Clean separation: lobby = login infra, profile = user data. Matches existing `--profile` folder model. Supports Amber (lobby has relay access for NIP-46). Switching is explicit and controlled. | `ProfileConfiguration` needs to become mutable (add `SwitchProfile`). `StorageService` needs `SwitchDatabase`. Services that cache user-specific state (MessageService, MlsService) need reloading. Logging dir doesn't change mid-session (acceptable — logs stay in lobby or need `SwitchLogDir`). | Medium |

---

## Alternative 7 — Detailed Impact Analysis

### Concept
The app always boots into a lightweight "default" profile. This profile has its own DB — just enough to support the login screen (signer session persistence, NIP-46 relay for Amber). After login succeeds and we know the npub, we switch to the user's profile folder. All user data (chats, messages, MLS state, relay lists) lives there.

### Startup Flow
```
1. App starts → ProfileConfiguration uses "default" profile
2. Services created (StorageService opens default/openchat.db)
3. LoginViewModel checks for auto-login:
   a. Read last_user.json → derive profile → SwitchProfile + SwitchDatabase → auto-login from user profile DB
   b. No last_user.json → show login screen (using default profile for signer session)
4. After login:
   a. Derive profile name from CurrentUser.PublicKeyHex
   b. SwitchProfile(derivedName) → updates ProfileConfiguration paths
   c. StorageService.SwitchDatabase(newDbPath) → close old DB, open new
   d. MlsService.ResetAsync() → clear old MLS state
   e. StorageService.InitializeAsync() → ensure schema exists in new DB
   f. Save CurrentUser to new DB
   g. MlsService.InitializeAsync() → fresh keys for this user
   h. Continue with InitializeAfterLoginAsync (load chats, connect relays, etc.)
5. Update last_user.json with current pubkey
```

### Code Impact

#### ProfileConfiguration.cs
- Add `SwitchProfile(string name)` — same as `SetProfile` but allowed after startup
- Add `RootDataDirectory` property (always `%LOCALAPPDATA%\OpenChat\`)
- Add `DeriveProfileName(string publicKeyHex)` → first 16 hex chars
- Add `ReadLastUserPubKey()` / `WriteLastUserPubKey()` / `ClearLastUser()` — read/write `last_user.json` in root dir
- `WasExplicitlySet` flag for `--profile` override

#### StorageService.cs
- Add `SwitchDatabase(string newDbPath)` method:
  - Close current SQLite connection
  - Update `_connectionString` to point to new path
  - Call `InitializeAsync()` to ensure schema
  - Clear any in-memory caches
- The `_initialized` flag needs resetting so `InitializeAsync` works again

#### ManagedMlsService.cs
- `ResetAsync()` already implemented (from our earlier fix)
- After switch, `InitializeAsync()` must be callable again (guard `_mdk != null` already returns early — need to allow re-init after reset)

#### MessageService.cs
- Holds `_currentUserPubKeyHex` from init — needs re-initialization
- Add `ResetAsync()` or `ReinitializeAsync(string pubkey)` to clear cached state
- Subscriptions to relay events may need re-wiring

#### MainViewModel.cs
- After login, call the switch sequence (profile → DB → MLS → message service → reload UI)
- On logout, call `ProfileConfiguration.ClearLastUser()` + switch back to default profile
- Need to reload chat list, clear chat view after switch

#### LoginViewModel.cs
- For auto-login path: read registry, switch to user profile, THEN check for stored user
- For fresh login: login happens in default profile context, switch after

#### NostrService.cs
- Stateless regarding profile (connects to relays, doesn't persist to DB directly)
- No changes needed — relay connections are established after profile switch

#### App.axaml.cs / Program.cs
- Program.cs: if `--profile` given, use it. Otherwise, always start with default.
- App.axaml.cs: no changes — services created as before with default profile

#### Logging
- Logs start in default profile's log dir
- After switch, could optionally redirect — but acceptable to keep all logs in one place for the session
- Alternative: always log to root `%LOCALAPPDATA%\OpenChat\logs\` regardless of profile

### What Lives Where

| Data | Default (lobby) profile | User profile (per-npub) |
|------|------------------------|------------------------|
| last_user.json | Root dir (not in any profile) | — |
| Signer session (NIP-46) | Yes (for Amber login) | No |
| User record | No | Yes |
| Chats & messages | No | Yes |
| MLS state | No | Yes |
| Relay lists | No | Yes |
| Settings | No | Yes |
| Logs | Shared (root or lobby) | — |

### Risks
- **Signer session in lobby DB**: After switching to user profile, the signer session details (relay, keys) are in the lobby DB, not the user profile. MainViewModel currently persists signer session on the CurrentUser record. After the switch, it saves to the user profile DB — this works if we save AFTER the switch.
- **MessageService state**: MessageService is initialized with a pubkey and subscribes to events. After profile switch, it needs to be told "new user" or re-initialized.
- **Race conditions**: If login completes while the old profile's data is still being read, we could get stale data. The switch should be synchronous (block UI briefly) or sequenced carefully.

## Alternative 8 — Login-first parent ViewModel

### Concept
Invert the ViewModel hierarchy. Instead of `MainViewModel` owning `LoginViewModel` as a child,
create a top-level `ShellViewModel` that owns the login flow. Login runs BEFORE any user-scoped
services exist. Once the npub is known, switch profile, create all services, THEN create MainViewModel.

### Startup Flow
```
1. App starts → ProfileConfiguration uses "default" lobby profile
2. Create ONLY lightweight services: ExternalSignerService (for Amber NIP-46)
3. ShellViewModel shows LoginView
   a. Auto-login path: read last_user.json → derive profile → set profile → create services → create MainViewModel → done
   b. Fresh login: user enters nsec or scans Amber QR → identity obtained
4. ShellViewModel has the npub now:
   a. ProfileConfiguration.SetProfile(DeriveProfileName(pubkey))
   b. Create StorageService, NostrService, MlsService, MessageService with correct profile
   c. Create MainViewModel (receives fully initialized services)
   d. Switch ShellView content from LoginView → MainView
5. Update last_user.json
```

### Code Impact

#### New: ShellViewModel.cs
- Top-level ViewModel, set as Window DataContext
- Owns LoginViewModel (for login flow)
- After login: creates services + MainViewModel
- Exposes `CurrentContent` (LoginView or MainView) via reactive property
- On logout: disposes MainViewModel + services, switches back to LoginView

#### ProfileConfiguration.cs
- Same additions as Alt 7: DeriveProfileName, ReadLastUserPubKey, WriteLastUserPubKey, etc.
- SetProfile only called ONCE per session (before services, after login) — stays immutable after set

#### App.axaml.cs
- Simplified: only creates ShellViewModel + lightweight login dependencies
- Does NOT create StorageService, NostrService, MlsService, MessageService at startup
- Service creation moves into ShellViewModel.OnLoginCompleted()

#### MainViewModel.cs
- No longer owns LoginViewModel
- No longer handles the logged-out → logged-in transition
- Receives fully initialized services — cleaner constructor
- Logout calls back to ShellViewModel (event or callback)

#### LoginViewModel.cs
- Minimal dependencies: only ExternalSignerService + NostrService-like helpers for Amber
- For nsec login: pure local operation, no services needed
- For Amber: needs a relay connection — ShellViewModel provides a lightweight NostrService
  OR ExternalSignerService handles its own WebSocket (it already does)

#### StorageService, MlsService, MessageService, NostrService
- NO changes needed — they are created with the correct profile from the start
- No SwitchDatabase, no ResetAsync mid-session, no reload

#### MainWindow.axaml
- DataContext = ShellViewModel
- ContentControl bound to ShellViewModel.CurrentContent (LoginView or MainView)

### Pros
- Cleanest architecture: services never see the wrong profile
- No restart, no reload, no mid-session switching
- ProfileConfiguration stays immutable after SetProfile (one-time set)
- Logout is clean: dispose services, go back to login
- Natural place for future multi-account support (ShellViewModel manages accounts)

### Cons
- Largest refactor: MainViewModel/LoginViewModel relationship inverted
- Service creation moves from App.axaml.cs to ShellViewModel (less declarative)
- LoginViewModel's Amber flow needs ExternalSignerService without full NostrService
  (but ExternalSignerService already manages its own WebSocket — no real issue)
- All existing Views that bind to MainViewModel.LoginViewModel need rebinding

### Estimated Effort
- ShellViewModel: new file, ~150 lines
- App.axaml.cs: simplify (remove service creation)
- MainViewModel: remove login logic, simplify constructor
- LoginViewModel: minimal changes (already mostly self-contained)
- MainWindow.axaml: change DataContext + add ContentControl switching
- Service creation: move to a factory method called by ShellViewModel

Medium complexity, but the result is the correct architecture going forward.

## Deep Dive: Alternative 8 vs Alternative 2

### Key Finding: Services Are NOT Initialized at Startup

The exploration revealed something important: **StorageService, NostrService, MlsService are all created
but NOT initialized until after login**. No DB connections, no relays, no MLS state. Initialization
only happens in `InitializeAfterLoginAsync()`. This significantly reduces the complexity of both approaches.

---

### Alternative 2: Two-Phase Startup

**Concept**: Phase 1 = login screen with lightweight services. Phase 2 = full services after login.

**What Actually Needs to Change**:
- `App.axaml.cs`: Split service creation into "login services" and "post-login services"
- LoginViewModel currently needs: `NostrService` (for `ImportPrivateKey` + `GenerateKeyPair`),
  `StorageService` (for `SaveCurrentUserAsync`), `QrCodeGenerator`, `ExternalSignerService`
- The NostrService methods LoginViewModel uses are essentially static crypto operations —
  they don't need relay connections
- StorageService IS needed to persist the user (but could use a "lobby" DB)

**The Problem**: LoginViewModel saves the user to the DB. If we haven't set the profile yet,
it saves to the wrong DB. We'd need either:
- A lobby DB that's separate from user data
- Or delay saving until after profile is set

**Surprises**:
- ExternalSignerService manages its own WebSocket (doesn't need NostrService) — Amber login works standalone
- `NostrService.ImportPrivateKey()` and `GenerateKeyPair()` are pure crypto, no state needed
- `StorageService.InitializeAsync()` is idempotent — can be called on a new DB after profile switch
- But: StorageService doesn't support switching its DB path mid-session (`_connectionString` is readonly)

**Effort**: Need to refactor `App.axaml.cs` to defer service creation. LoginViewModel needs
to work with a temporary/lobby StorageService. After login, create real StorageService with correct
profile path. Medium complexity but messy — two StorageService instances.

---

### Alternative 8: Login-First Parent (ShellViewModel)

**Concept**: ShellViewModel owns the login flow. After login + profile set, creates MainViewModel
with fully correct services.

**What Actually Needs to Change**:

#### New: ShellViewModel (~150 lines)
- Created by `App.axaml.cs` instead of MainViewModel
- Owns: LoginViewModel, ExternalSignerService
- Exposes: `CurrentContent` (reactive property switching between LoginView and MainView)
- After login: sets profile → creates services → creates MainViewModel → switches content
- On logout: disposes MainViewModel + services → switches back to LoginView

#### App.axaml.cs — Simplified
```
Before: Create ALL services → Create MainViewModel → Set as DataContext
After:  Create ShellViewModel (lightweight) → Set as DataContext
        Service creation moves to ShellViewModel.OnLoginCompleted()
```

#### LoginViewModel — Minimal Changes
- `NostrService.ImportPrivateKey()` → extract to a static `NostrCrypto` helper or keep NostrService as
  lightweight (it has no state until relays connect)
- `StorageService` dependency: LoginViewModel needs it ONLY to save the user.
  **But we can defer saving until after profile switch.**
  LoginViewModel just returns the User object → ShellViewModel saves it to the correct DB.
- `ExternalSignerService`: already self-contained, created by LoginViewModel or ShellViewModel

#### MainViewModel — Cleaner
- Remove LoginViewModel creation (moved to ShellViewModel)
- Remove login/logout transition logic
- Remove `InitializeAsync()` auto-login check (ShellViewModel handles this)
- Logout = callback to ShellViewModel (not internal state toggle)
- Constructor receives already-initialized services

#### MainWindow.axaml
```xml
<!-- Before -->
<Grid IsVisible="{Binding IsLoggedIn}"> ... main UI ... </Grid>
<LoginView IsVisible="{Binding !IsLoggedIn}" />

<!-- After -->
<ContentControl Content="{Binding CurrentContent}" />
```
ShellViewModel.CurrentContent switches between LoginView and MainView.

#### Android Impact
- `MainActivity` creates ShellViewModel instead of MainViewModel
- Fragment switching observes `ShellViewModel.IsLoggedIn` (same pattern)
- Fragments get MainViewModel via `ShellViewModel.MainViewModel`
- `OnResume()` signer reconnect: ShellViewModel holds ExternalSignerService reference
- **Manageable change, not breaking**

---

### Head-to-Head Comparison

| Aspect | Alt 2: Two-Phase Startup | Alt 8: ShellViewModel |
|--------|-------------------------|----------------------|
| **Profile timing** | Set between phases (after login, before service init) | Set before service creation (ShellViewModel orchestrates) |
| **Service creation** | Deferred in App.axaml.cs (conditional) | Moved to ShellViewModel (explicit) |
| **StorageService** | Two instances (lobby + real) OR switch mid-session | One instance, created after profile is known |
| **LoginViewModel changes** | Needs to work without real StorageService | Returns User object instead of saving directly |
| **MainViewModel changes** | Remove auto-login, add profile-awareness | Remove login logic entirely (cleaner) |
| **Logout flow** | Reset services, switch back to phase 1 | Dispose MainViewModel + services, show LoginView |
| **Android impact** | Similar to desktop refactor | Fragment switching changes to ShellViewModel |
| **ExternalSignerService** | Survives across phases (tricky lifecycle) | Owned by ShellViewModel (clean lifecycle) |
| **Window/View changes** | Minor — keep current layout, adjust visibility | ContentControl switch (cleaner, more standard) |
| **New files** | None (refactor existing) | ShellViewModel.cs, ShellView.axaml |
| **Risk of stale state** | Medium — services exist before profile is correct | Low — services created with correct profile |
| **Complexity** | Medium-High (messy two-StorageService issue) | Medium (clean but larger surface area) |
| **Future extensibility** | Limited — no natural place for account switcher | ShellViewModel is the natural multi-account host |

---

### Surprises / Risks for Alternative 8

1. **LoginViewModel.SaveCurrentUserAsync** — currently saves to DB during login. In Alt 8,
   LoginViewModel wouldn't have a StorageService yet. Fix: LoginViewModel returns the User
   to ShellViewModel, which saves after profile is set. Minor interface change.

2. **ExternalSignerService lifecycle** — Currently created inside LoginViewModel. In Alt 8,
   ShellViewModel should own it and pass to LoginViewModel. After login, it's also needed by
   MainViewModel (for NostrService signer wiring). ShellViewModel passes it to both.

3. **Auto-login path** — Currently `MainViewModel.InitializeAsync()` checks DB for saved user.
   In Alt 8, ShellViewModel reads `last_user.json` → derives profile → creates StorageService
   → checks for saved user → if found, skips LoginView entirely and goes straight to MainView.
   Faster startup for returning users.

4. **`InitializeAfterLoginAsync` is NOT awaited on fresh login (line 201)** — it's fire-and-forget.
   This means the UI shows immediately while network init runs in background. Alt 8 preserves
   this pattern naturally since MainViewModel is created after login.

5. **NostrService has static-like methods** — `ImportPrivateKey()`, `GenerateKeyPair()`, `DerivePublicKey()`
   are pure crypto. LoginViewModel could use a static helper instead of a full NostrService instance.
   This makes Alt 8 cleaner since LoginViewModel truly has no service dependencies beyond QrCodeGenerator
   and ExternalSignerService.

6. **Platform services (AudioRecording, Blossom, FilePicker)** — These are set as static properties
   on ChatViewModel. They don't depend on profile. Can be created in App.axaml.cs as before,
   or in ShellViewModel. No issue.

### Recommendation

**Alternative 8 (ShellViewModel) is the better choice.** The main advantages:

- Services are NEVER created with the wrong profile — eliminates an entire class of bugs
- ProfileConfiguration stays immutable after SetProfile — no mid-session switching
- Clean ownership: ShellViewModel owns login + service lifecycle
- Natural place for future multi-account support
- LoginViewModel becomes simpler (no DB dependency)
- MainViewModel becomes simpler (no login logic)
- The "two StorageService" problem of Alt 2 doesn't exist

The main cost is touching more files, but the changes to each file are straightforward
and the end result is cleaner than what exists today.

## Decision

_TBD — discuss with user_
