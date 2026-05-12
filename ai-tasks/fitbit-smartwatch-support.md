# Fitbit Versa 4 Smartwatch Support

## Goal
Add Fitbit Versa 4 support to OpenChat via a phone-proxied bridge architecture. The watch acts as a thin client; the Android app handles all crypto.

## Steps

- [x] **Phase 1: Bridge HTTP Service** — Created `BridgeHttpService`, `BridgeAuthService`, `BridgeRequestRouter`, `BridgeSseHandler` in `src/OpenChat.Android/Services/Bridge/`. Localhost-only HTTP server exposing REST API for chats, messages, and SSE push.
- [x] **Phase 1: MainActivity Integration** — Bridge starts on login, stops on logout. Added `GetMessageService()`/`GetStorageService()` to MainViewModel.
- [x] **Phase 1: Build & Test** — Compiles cleanly, 210/210 unit tests pass (10 pre-existing Rust MLS failures unrelated).
- [x] **Phase 2: Pairing Flow** — Added watch pairing properties/commands to SettingsViewModel, Smartwatch card to settings layout, reactive bindings in SettingsFragment, platform delegates wired in MainActivity.
- [x] **Phase 3: Fitbit Companion App** — Created `fitbit/companion/index.js` with bridge connection, SSE/polling, watch relay. Settings page `fitbit/settings/index.jsx` for pairing.
- [x] **Phase 4: Fitbit Watch App** — Created `fitbit/app/` with 3 screens: chat list, message list, quick reply picker. SVG layout + CSS + JS.
- [ ] **Phase 5: End-to-end Testing** — Test on Versa 4: see chats, read messages, send quick reply.

## Status
IN PROGRESS — Phase 1-4 complete, ready for device testing
