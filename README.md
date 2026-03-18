# OpenChat

A decentralized encrypted messenger built on [Nostr](https://nostr.com/) + [MLS](https://messaginglayersecurity.rocks/) via [Marmot](https://github.com/marmot-protocol/mdk). Talk to your friends without a server in the middle.

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Avalonia UI](https://img.shields.io/badge/Avalonia-11.2-blue)
![License](https://img.shields.io/badge/license-MIT-green)

> **Fair warning:** This entire app was vibe coded with AI. Every feature, every bug fix, every test. The humans mostly pointed and said "make it work" and "that's broken, fix it." It works surprisingly well, but you've been warned.

## What does it do?

End-to-end encrypted group chat over Nostr. Your messages are encrypted before they leave your machine. Relays just pass around blobs they can't read. No accounts, no phone numbers, no email signups. Just keys.

- **Group chats** with MLS encryption (forward secrecy, the works)
- **Direct messages** with NIP-44 encryption
- **Login with Amber** (NIP-46) or a local nsec &mdash; your choice
- **Desktop** (Windows/Linux/macOS) and **Android**
- **Talk to the web app** &mdash; interoperable with [Marmot web client](https://github.com/marmot-protocol/marmot-ts)

## Quick start

```bash
git clone https://github.com/DavidGershony/openChat.git
cd openChat
dotnet run --project src/OpenChat.Desktop
```

That's it. Log in with an nsec, generate a fresh key, or scan a QR code with Amber.

## Features

| Feature | Status |
|---|---|
| MLS encrypted group chat (kind 443/444/445) | Working |
| NIP-59 gift-wrapped invites | Working |
| NIP-46 remote signer (Amber) | Working |
| Auto-reconnect signer on restart | Working |
| KeyPackage auto-lookup on invite | Working |
| Load older messages from relays | Working |
| Image message detection (MIP-04) | Placeholder |
| NIP-65 relay discovery | Working |
| Profile metadata publish (kind 0) | Working |
| Secure key storage (DPAPI/Android Keystore) | Working |
| Cross-MDK interop (Rust + C# MLS) | Working |
| Direct messages (NIP-44) | Basic |

## How it works

You publish encrypted blobs to Nostr relays. Other group members decrypt them with MLS keys. Nobody else can read them &mdash; not the relay operators, not us, not even the AI that wrote this code.

| Nostr Event Kind | What it does |
|---|---|
| **0** | Your profile (name, avatar) |
| **443** | MLS KeyPackage &mdash; so people can invite you |
| **444** | MLS Welcome &mdash; the invite itself (NIP-59 gift wrapped) |
| **445** | Group messages &mdash; MIP-03 encrypted |
| **1059** | Gift wrap envelope (NIP-59) |
| **10002** | Your relay list (NIP-65) |

## Project layout

```
src/
  OpenChat.Core/          Models, services, Nostr protocol, MLS integration
  OpenChat.Presentation/  ViewModels (ReactiveUI + Fody)
  OpenChat.UI/            Avalonia views and platform services
  OpenChat.Desktop/       Desktop entry point
  OpenChat.Android/       Android entry point
  OpenChat.Native/        Rust native MLS library (Marmot MDK FFI)
tests/
  OpenChat.Core.Tests/    Unit, integration, and real-relay tests
  OpenChat.UI.Tests/      ViewModel tests
```

## Running tests

```bash
# Unit tests (fast, no relay needed)
dotnet test --filter "Category!=Relay&Category!=Integration"

# Real relay integration tests (needs docker)
docker compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"

# Everything
docker compose -f docker-compose.test.yml up -d
dotnet test
```

## Building the native MLS library (optional)

The C# MLS backend works out of the box. The Rust native backend is optional:

```bash
cd src/OpenChat.Native
cargo build --release
cp target/release/openchat_native.dll ../OpenChat.Desktop/  # Windows
```

## Tech stack

| What | How |
|---|---|
| UI | [Avalonia 11](https://avaloniaui.net/) |
| MVVM | [ReactiveUI](https://www.reactiveui.net/) + Fody |
| Nostr | Raw WebSocket client (NIP-01, NIP-44, NIP-46, NIP-59, NIP-65) |
| MLS | [Marmot MDK](https://github.com/marmot-protocol/mdk) (Rust FFI) + [marmot-cs](https://github.com/ArcaneLibs/marmot-cs) (pure C#) |
| Storage | SQLite with DPAPI/Android Keystore encryption for keys |
| Logging | Serilog (file-based, new file per session) |
| Runtime | .NET 9 |
| Vibes | Claude |

## License

MIT
