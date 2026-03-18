# OpenChat

A [Marmot Protocol](https://github.com/marmot-protocol) client for desktop and Android. End-to-end encrypted group messaging over [Nostr](https://nostr.com/), powered by [MLS (RFC 9420)](https://messaginglayersecurity.rocks/).

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Avalonia UI](https://img.shields.io/badge/Avalonia-11.2-blue)
![Marmot](https://img.shields.io/badge/Marmot-MIP--00%20to%20MIP--04-purple)
![License](https://img.shields.io/badge/license-MIT-green)

> **Fair warning:** This entire app was vibe coded with AI. Every feature, every bug fix, every test. The humans mostly pointed and said "make it work" and "that's broken, fix it." It works surprisingly well, but you've been warned.

## What is Marmot?

[Marmot](https://github.com/marmot-protocol) is a protocol for encrypted group messaging on Nostr. It uses MLS (Messaging Layer Security, RFC 9420) to give you forward secrecy and post-compromise security in group chats &mdash; the same kind of encryption Signal uses, but decentralized over Nostr relays.

OpenChat is a Marmot client. It implements the [Marmot Improvement Proposals (MIPs)](https://github.com/marmot-protocol/mdk):

| MIP | What it defines | OpenChat status |
|---|---|---|
| **MIP-00** | KeyPackage publishing (kind 443) | Implemented |
| **MIP-01** | Group metadata & NostrGroupData extension (0xF2EE) | Implemented |
| **MIP-02** | Welcome events (kind 444) with NIP-59 gift wrap | Implemented |
| **MIP-03** | Group messages (kind 445) with ChaCha20-Poly1305 | Implemented |
| **MIP-04** | Encrypted media attachments via Blossom | Detection only |

Any Marmot-compatible client can join the same groups and exchange messages with OpenChat.

## What does it do?

Encrypted group chat over Nostr. Your messages are encrypted before they leave your machine. Relays just pass around blobs they can't read. No accounts, no phone numbers, no email signups. Just keys.

- **Marmot group chats** &mdash; MLS encryption with forward secrecy
- **Direct messages** &mdash; NIP-44 encryption
- **Login with Amber** (NIP-46) or a local nsec &mdash; your choice
- **Desktop** (Windows/Linux/macOS) and **Android**
- **Two MLS backends** &mdash; Rust ([Marmot MDK](https://github.com/marmot-protocol/mdk)) or pure C# ([marmot-cs](https://github.com/DavidGershony/marmot-cs))
- **Interoperable** &mdash; works with any Marmot protocol implementation

## Quick start

```bash
git clone https://github.com/DavidGershony/openChat.git
cd openChat
dotnet run --project src/OpenChat.Desktop
```

Log in with an nsec, generate a fresh key, or scan a QR code with Amber.

## Features

| Feature | Status |
|---|---|
| Marmot MLS encrypted group chat (MIP-00 to MIP-03) | Working |
| Cross-client interop (any Marmot implementation) | Working |
| NIP-59 gift-wrapped Welcome invites | Working |
| NIP-46 remote signer (Amber) with auto-reconnect | Working |
| KeyPackage auto-lookup on invite | Working |
| Load older messages from relays | Working |
| MIP-04 image message detection | Placeholder |
| NIP-65 relay discovery | Working |
| Profile metadata publish (kind 0) | Working |
| Secure key storage (DPAPI / Android Keystore) | Working |
| Dual MLS backend (Rust MDK + C# marmot-cs) | Working |
| Direct messages (NIP-44) | Basic |

## How Marmot works on Nostr

Marmot maps MLS operations to Nostr event kinds. Your client publishes a KeyPackage so others can invite you. Invites arrive as NIP-59 gift-wrapped Welcome events. Once you join a group, messages are MLS-encrypted and published with the group's NostrGroupId in the `h` tag.

| Nostr Event Kind | Marmot Usage |
|---|---|
| **443** | MLS KeyPackage &mdash; "here's how to invite me" (MIP-00) |
| **444** | MLS Welcome &mdash; group invite, gift wrapped via NIP-59 (MIP-02) |
| **445** | Group message &mdash; MLS + ChaCha20-Poly1305 encrypted (MIP-03) |
| **1059** | NIP-59 gift wrap envelope for Welcome events |
| **0** | User profile metadata |
| **10002** | NIP-65 relay list |

### MLS Group Lifecycle (Marmot style)

1. **Publish KeyPackage** (kind 443) &mdash; announce your MLS public key material to relays
2. **Create group** &mdash; initialize an MLS group with the 0xF2EE NostrGroupData extension
3. **Invite** &mdash; fetch the invitee's KeyPackage, add them to the MLS group, gift-wrap the Welcome (kind 444)
4. **Accept** &mdash; unwrap the gift wrap, process the MLS Welcome, join the group
5. **Chat** &mdash; encrypt messages with MLS + MIP-03 ChaCha20-Poly1305, publish as kind 445
6. **Forward secrecy** &mdash; MLS ratchets keys with every commit

## Project layout

```
src/
  OpenChat.Core/          Nostr protocol, MLS integration, Marmot MIP implementations
  OpenChat.Presentation/  ViewModels (ReactiveUI + Fody)
  OpenChat.UI/            Avalonia views and platform services
  OpenChat.Desktop/       Desktop entry point
  OpenChat.Android/       Android entry point
  OpenChat.Native/        Rust native library (Marmot MDK FFI)
tests/
  OpenChat.Core.Tests/    Unit, integration, real-relay, and MIP compliance tests
  OpenChat.UI.Tests/      ViewModel tests
```

## Running tests

```bash
# Unit tests (fast, no relay needed)
dotnet test --filter "Category!=Relay&Category!=Integration"

# Real relay integration tests (needs docker)
docker compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"
```

The integration tests run a full Marmot flow through a real Nostr relay: create group, publish KeyPackage, send Welcome, exchange encrypted messages, verify MIP-03 wire format.

## Building the Rust MLS backend (optional)

The pure C# MLS backend ([marmot-cs](https://github.com/DavidGershony/marmot-cs)) works out of the box. The Rust backend ([Marmot MDK](https://github.com/marmot-protocol/mdk)) is optional:

```bash
cd src/OpenChat.Native
cargo build --release
cp target/release/openchat_native.dll ../OpenChat.Desktop/  # Windows
```

Run with `--mdk rust` to use the Rust backend, or `--mdk managed` (default) for C#.

## Tech stack

| What | How |
|---|---|
| Protocol | [Marmot](https://github.com/marmot-protocol) (MIP-00 through MIP-04) |
| MLS | [Marmot MDK](https://github.com/marmot-protocol/mdk) (Rust) + [marmot-cs](https://github.com/DavidGershony/marmot-cs) (C#) |
| Transport | [Nostr](https://nostr.com/) (NIP-01, NIP-44, NIP-46, NIP-59, NIP-65) |
| UI | [Avalonia 11](https://avaloniaui.net/) |
| MVVM | [ReactiveUI](https://www.reactiveui.net/) + Fody |
| Storage | SQLite + DPAPI/Android Keystore for key encryption |
| Logging | Serilog |
| Runtime | .NET 9 |
| Vibes | Claude |

## Related projects

- [Marmot Protocol](https://github.com/marmot-protocol) &mdash; the protocol spec and reference implementation
- [Marmot MDK](https://github.com/marmot-protocol/mdk) &mdash; Rust MLS development kit
- [marmot-cs](https://github.com/DavidGershony/marmot-cs) &mdash; pure C# Marmot implementation
- [marmot-ts](https://github.com/marmot-protocol/marmot-ts) &mdash; TypeScript/web Marmot client
- [dotnet-mls](https://github.com/ArcaneLibs/dotnet-mls) &mdash; C# MLS (RFC 9420) implementation

## License

MIT
