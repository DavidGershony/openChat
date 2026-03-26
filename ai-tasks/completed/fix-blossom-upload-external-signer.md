# Fix: Blossom upload fails for external signer (NIP-46) users

## Problem
File attach and voice recording buttons fail with:
```
System.InvalidOperationException: Cannot upload to Blossom: no private key and no external signer.
```

`BlossomUploadService` already supports external signers via `SetExternalSigner()`,
but this method is never called after login. The `NostrService` and `MlsService` both
get the signer wired in `MainViewModel`, but `BlossomUploadService` does not.

## Fix
1. [x] Add `SetExternalSigner(IExternalSigner?)` to `IMediaUploadService` interface
2. [x] Wire it in `MainViewModel` after login alongside the other signer setups
3. [x] Write tests (BlossomUploadServiceTests — 3 tests passing)
4. [x] Verify the fix (build + tests pass)
