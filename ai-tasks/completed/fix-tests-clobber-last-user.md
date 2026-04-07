# Fix tests clobbering last_user.json

## Status: Not Started

## Problem

Headless integration tests run in DEBUG mode, so `ProfileConfiguration.GetDefaultDataDirectory()` returns `OpenChat-Dev` — the same root as the real app. Tests that exercise logout (`ShellViewModel.Logout()` → `ProfileConfiguration.ClearLastUser()`) delete `last_user.json` from the shared `OpenChat-Dev` directory, breaking real session auto-login.

Discovered 2026-04-07: user's Amber signer session stopped auto-logging in after test runs on Apr 6 deleted the file.

## Root cause

`ProfileConfiguration.RootDataDirectory` is a static property initialized once from `GetDefaultDataDirectory()`. Tests use the same static, so `ClearLastUser()` and `WriteLastUserPubKey()` hit the real user's data directory.

## Proposed fix

Isolate `RootDataDirectory` in tests so it points to a temp directory instead of the real `OpenChat-Dev` folder. Options:

- [ ] Add a `SetRootDataDirectory(string path)` method to `ProfileConfiguration` (test-only), called during test setup to redirect to a temp path
- [ ] Ensure all test teardown cleans up any `last_user.json` it created
- [ ] Alternatively: make logout tests that call `ClearLastUser()` use a mock or wrapper instead of hitting the filesystem directly

## Scope

- Headless integration tests (`HeadlessTestBase` and subclasses)
- Any test that exercises `ShellViewModel.Logout()` or `ProfileConfiguration.ClearLastUser()`
- `DoubleInitMessageServiceTests` or similar that may set up full ShellViewModel flows
