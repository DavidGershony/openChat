# MLS State Persistence Fix — Impact on WhiteNoise Interop

## What changed

`ManagedMlsService` now explicitly exports and saves MLS group state to the `MlsStates` SQLite table after every state-changing operation, matching the pattern the Rust backend already uses.

### Operations that now persist state:
- `CreateGroupAsync` — after group creation
- `AddMemberAsync` — after adding a member (epoch advance)
- `ProcessWelcomeAsync` — after joining a group via Welcome
- `EncryptMessageAsync` — after encrypting a message
- `EncryptReactionAsync` — after encrypting a reaction
- `DecryptMessageAsync` → `ProcessMessageAsync` — after decrypting a message or commit
- `ProcessCommitAsync` — after processing a standalone commit
- `SelfUpdateAsync` — after self-update

### On initialization:
`ManagedMlsService.InitializeAsync` now restores all group states from the `MlsStates` table after building the MDK. This replaces the removed code from `MainViewModel.InitializeAfterLoginAsync`.

## WhiteNoise testing needed

1. **Group chat with WhiteNoise still works** — the persist calls are non-breaking (they only write to DB), but verify that the additional SQLite writes don't cause contention or slowdowns during active group messaging.

2. **Cross-MDK interop after restart** — if a WhiteNoise user is in a group with an OpenChat user, and the OpenChat user restarts, verify the OpenChat user can still decrypt messages from WhiteNoise. The state restoration should now work correctly.

3. **Epoch consistency** — verify that the exporter secret stays in sync after multiple messages + app restart. The persist-after-every-operation approach ensures no state is lost between DB writes.
