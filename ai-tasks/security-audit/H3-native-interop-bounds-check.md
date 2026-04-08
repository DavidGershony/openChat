# H3 - Add Bounds Checking to Native Interop (MarmotWrapper)

**Severity**: HIGH
**Status**: COMPLETED

## Context

`MarmotWrapper` calls native `openchat_native.dll` functions that return `int length` values. These are passed directly to `new byte[length]` and `Marshal.Copy` without validation. A compromised or buggy native library returning negative or huge values could cause crashes or memory corruption.

## Files
- `src/OpenChat.Core/Marmot/MarmotWrapper.cs` (lines 92-101, 159-169, 211-221, 322-335, and other Marshal.Copy sites)

## Tasks
- [ ] Add a helper method: `ValidateNativeBufferLength(int length, string operation)` that throws if `length < 0 || length > MaxBufferSize` (e.g. 50MB)
- [ ] Call this helper before every `new byte[length]` + `Marshal.Copy` pair
- [ ] Log the operation name and length at Debug level for diagnostics
- [ ] Add tests: valid length succeeds, negative length throws, oversized length throws
