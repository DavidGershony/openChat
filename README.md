# OpenChat

A desktop messenger built on [Nostr](https://nostr.com/) with end-to-end encrypted group chat powered by the [Messaging Layer Security (MLS)](https://messaginglayersecurity.rocks/) protocol via [Marmot](https://github.com/marmot-protocol/mdk).

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Avalonia UI](https://img.shields.io/badge/Avalonia-11.2-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## What is this?

OpenChat is an open-source, decentralized chat application that combines:

- **Nostr** for relay-based message transport (no central server)
- **MLS (RFC 9420)** for forward-secret, post-compromise-secure group encryption
- **Avalonia UI** for a cross-platform native desktop experience

Messages are encrypted client-side before being published to Nostr relays. Only group members holding the correct MLS keys can decrypt them.

## Features

- **Direct Messages** &mdash; NIP-44 encrypted 1-on-1 conversations
- **Group Chats** &mdash; MLS-encrypted group messaging via Marmot (kind 443/444/445 events)
- **Multiple Login Methods** &mdash; Import an existing `nsec` key, generate a new keypair, or connect a remote signer via NIP-46 bunker URL (e.g. [Amber](https://github.com/greenart7c3/Amber))
- **Profile Metadata** &mdash; Fetches display names, avatars, and bios from Nostr (kind 0)
- **Multi-Relay Support** &mdash; Connect to multiple relays simultaneously with per-relay status indicators and individual reconnect controls
- **Cross-Platform** &mdash; Runs on Windows, macOS, and Linux via Avalonia

## Project Structure

```
OpenChat.sln
src/
  OpenChat.Core/          Core library: models, services, Nostr protocol, MLS integration
  OpenChat.UI/            Avalonia views and view models (MVVM with ReactiveUI)
  OpenChat.Desktop/       Desktop app entry point
  OpenChat.Native/        Rust native library (MLS via Marmot MDK)
tests/
  OpenChat.Core.Tests/    Unit and integration tests for core services
  OpenChat.UI.Tests/      View model and UI tests
```

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Rust toolchain](https://rustup.rs/) (only if building the native MLS library)

## Getting Started

### 1. Clone the repo

```bash
git clone https://github.com/DavidGershony/openChat.git
cd openChat
```

### 2. Build the native MLS library (optional)

The native library provides real MLS encryption via the Marmot protocol. Without it, the app falls back to a mock MLS service.

```bash
cd src/OpenChat.Native
cargo build --release
```

Copy the resulting library to the Desktop project:

```bash
# Windows
cp target/release/openchat_native.dll ../OpenChat.Desktop/

# macOS
cp target/release/libopenchat_native.dylib ../OpenChat.Desktop/

# Linux
cp target/release/libopenchat_native.so ../OpenChat.Desktop/
```

### 3. Build and run

```bash
dotnet build
dotnet run --project src/OpenChat.Desktop
```

### 4. Log in

On first launch you'll see the login screen with three options:

| Method | Description |
|---|---|
| **Private Key** | Paste an existing `nsec` or hex private key |
| **Generate New** | Creates a fresh Nostr keypair |
| **Bunker (NIP-46)** | Connect a remote signer like Amber by pasting a `bunker://` URL |

After login, OpenChat connects to default relays (`relay.damus.io`, `nos.lol`, `relay.nostr.band`), publishes your MLS KeyPackage, and loads your conversations.

## How It Works

### Nostr Events

| Kind | Purpose |
|---|---|
| **0** | User metadata (profile) |
| **443** | MLS KeyPackage &mdash; published so others can invite you to groups |
| **444** | MLS Welcome &mdash; sent to invite a user into an encrypted group |
| **445** | MLS Group Message &mdash; encrypted application messages and commits |

### MLS Group Lifecycle

1. **Create group** &mdash; The creator initializes an MLS group and generates a Welcome message
2. **Invite members** &mdash; The Welcome (kind 444) is published to Nostr, targeted at the invitee's public key
3. **Join group** &mdash; The invitee processes the Welcome to obtain the group's encryption keys
4. **Send messages** &mdash; Messages are MLS-encrypted and published as kind 445 events tagged with the group ID
5. **Forward secrecy** &mdash; MLS ratchets the group key with every commit, providing forward secrecy and post-compromise security

### Relay Communication

OpenChat uses raw WebSocket connections to Nostr relays (NIP-01). It handles:
- `REQ` subscriptions with filters (by kind, author, tags)
- `EVENT` publishing with proper event ID computation and Schnorr signing
- `OK` / `NOTICE` / `EOSE` relay responses
- Automatic reconnection per relay

## Running Tests

```bash
dotnet test
```

Some integration tests require a running Nostr relay (see `docker-compose.test.yml`):

```bash
docker compose -f docker-compose.test.yml up -d
dotnet test
```

## Configuration

- **Relays** &mdash; Add or remove relays from the Settings page
- **Profile** &mdash; Edit your display name, username, about, and avatar URL in Settings
- **Logs** &mdash; View application logs from Settings > View Logs

Data is stored locally via LiteDB in your OS application data folder.

## Tech Stack

| Layer | Technology |
|---|---|
| UI Framework | [Avalonia UI 11](https://avaloniaui.net/) |
| MVVM | [ReactiveUI](https://www.reactiveui.net/) + [Fody](https://github.com/Fody/Fody) |
| Nostr | Hand-rolled WebSocket client (NIP-01, NIP-04, NIP-44, NIP-46) |
| MLS Encryption | [Marmot MDK](https://github.com/marmot-protocol/mdk) via Rust FFI |
| Local Storage | [LiteDB](https://www.litedb.org/) |
| Logging | [Serilog](https://serilog.net/) |
| Runtime | .NET 9 |

## License

MIT
