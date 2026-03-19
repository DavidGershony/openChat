# SEC-HIGH: Relay URL Validation (SSRF Prevention)

## Problem
Relay URLs passed to `NostrService.ConnectAsync` are not validated. No scheme, hostname, or private IP checks. An attacker-controlled relay URL could target internal network services.

## Location
- `NostrService.cs:68-107` — `ConnectToRelayAsync`

## Fix
- [ ] Validate scheme is `wss://` or `ws://` (prefer wss)
- [ ] DNS resolve hostname and block private/reserved IP ranges
- [ ] Block localhost, 127.x, 10.x, 172.16-31.x, 192.168.x, 169.254.x, ::1, fc00::/7
- [ ] Tests proving SSRF is blocked

## Tests Required
- [ ] `wss://` URLs accepted
- [ ] `http://`, `ftp://`, `file://` URLs rejected
- [ ] Private IP relay URLs rejected
- [ ] Localhost relay URL rejected

## Status
- [ ] Not started
