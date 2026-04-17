# Whitenoise Interop Tests

## Goal
Prove OpenChat ↔ Whitenoise (reference Marmot protocol implementation) interoperability
by running group chats with 2 OpenChat users + 1 Whitenoise user.

## Approach
- Whitenoise runs in Docker (daemon `wnd` + CLI `wn`)
- OpenChat users run in xUnit test process
- Both connect to the same Nostr relay (docker-compose.test.yml)
- Tests drive Whitenoise via `docker exec wn <cmd> --json`

## Files Created
- [x] `tests/whitenoise-docker/Dockerfile` — multi-stage build for whitenoise-rs
- [x] `docker-compose.test.yml` — updated with whitenoise service
- [x] `tests/OpenChat.Diagnostics/WhitenoiseInterop/WhitenoiseDockerClient.cs` — C# Docker CLI driver
- [x] `tests/OpenChat.Diagnostics/WhitenoiseGroupInteropTests.cs` — 3 interop tests

## Tests
- [x] `GroupChat_3Users_2OpenChat_1Whitenoise` — Alice(OC) creates group, adds Bob(OC) + Charlie(WN)
- [x] `GroupChat_4Users_2OpenChat_2Whitenoise` — 4-user variant
- [x] `GroupChat_WhitenoiseCreatesGroup_OpenChatJoins` — Charlie(WN) creates group, Alice+Bob(OC) join

## Running
```bash
docker compose -f docker-compose.test.yml up -d --build
dotnet test tests/OpenChat.Diagnostics --filter "Category=WhitenoiseInterop"
```

## Status
- [x] Docker build: working (whitenoise-rs image builds in ~5 min, cached rebuilds ~30s)
- [x] 3-user test (2 OC + 1 WN): PASSING — cross-client MLS decryption verified
- [ ] 4-user test: needs run (same infrastructure, should work)
- [ ] WN-creates-group test: needs run

## Notes
- `ProfileConfiguration.SetAllowLocalRelays(true)` required for ws:// localhost relay
- Group relay URLs use `ws://host.docker.internal:7777` so both host and Docker can reach the relay
- WN CLI responses are wrapped in `{ "result": ... }` — UnwrapResult() handles this
- WN group IDs are nested objects `{ "value": { "vec": [...] } }` — ExtractGroupIdHex() converts to hex
