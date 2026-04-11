# OpenChat — Project Context

## Platform targets

This project has **two UI targets** that must both be kept in sync when making UI changes:

| Project | Platform | UI framework | Key entry point |
|---------|----------|--------------|-----------------|
| `src/OpenChat.Android` | Android (mobile) | Android Views / Fragments | `src/OpenChat.Android/Fragments/` |
| `src/OpenChat.UI` + `src/OpenChat.Desktop` | Windows / Linux / macOS (desktop) | Avalonia XAML | `src/OpenChat.UI/Views/` |

Shared logic lives in:
- `src/OpenChat.Core` — services, models, MLS/Nostr
- `src/OpenChat.Presentation` — ReactiveUI ViewModels (used by both targets)

**Any feature that touches the UI must be implemented in both `OpenChat.Android` and `OpenChat.UI`.**
