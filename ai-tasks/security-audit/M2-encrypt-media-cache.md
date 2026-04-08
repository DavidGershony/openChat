# M2 - Media Cache At-Rest Encryption

**Severity**: MEDIUM
**Status**: CLOSED — Accepted risk (industry standard)

## Decision (2026-04-08)

Storing decrypted media as plaintext files on disk is standard behavior across all major
messengers (WhatsApp, Signal, Telegram). None encrypt cached media at the application level.
OS-level full-disk encryption (BitLocker, LUKS, Android FBE) is the appropriate layer.

Blossom servers have limited retention, so re-downloading is not always possible — caching
decrypted media locally is necessary for a good UX.

## Future Feature Ideas (not security fixes)
- **Per-file encrypt-at-rest**: Lock icon on images — user opts in to store that file encrypted
  locally using ISecureStorage. Default remains plaintext (industry standard).
- **Delete from local cache**: Allow users to remove specific cached media from disk.
- **Cache TTL**: See `ai-tasks/media-cache-ttl-settings.md` for configurable expiry.
