# Scramble — Project Context

## Platform targets

This project has **two UI targets** that must both be kept in sync when making UI changes:

| Project | Platform | UI framework | Key entry point |
|---------|----------|--------------|-----------------|
| `src/Scramble.Android` | Android (mobile) | Android Views / Fragments | `src/Scramble.Android/Fragments/` |
| `src/Scramble.UI` + `src/Scramble.Desktop` | Windows / Linux / macOS (desktop) | Avalonia XAML | `src/Scramble.UI/Views/` |

Shared logic lives in:
- `src/Scramble.Core` — services, models, MLS/Nostr
- `src/Scramble.Presentation` — ReactiveUI ViewModels (used by both targets)

**Any feature that touches the UI must be implemented in both `Scramble.Android` and `Scramble.UI`.**
