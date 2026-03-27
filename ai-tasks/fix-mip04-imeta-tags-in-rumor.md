# Fix MIP-04: Include imeta tags in MLS-encrypted rumor

## Problem
When sending media messages, OpenChat encrypts only the placeholder text
(`[Encrypted image: filename.jpg]`) in the MLS rumor. The MIP-04 metadata
(URL, SHA-256, nonce, MIME type) is stored locally but NOT included in the
encrypted rumor's tags.

The web client (marmot-ts) expects `imeta` tags inside the kind 9 rumor to
download and decrypt the media file. Without them, it only sees the placeholder text.

## Current Flow (broken for cross-impl)
1. OpenChat encrypts: `content = "[Encrypted image: file.jpg]"`, `tags = []`
2. Web client decrypts and sees plain text, no media metadata

## Expected Flow (MIP-04 compliant)
1. OpenChat encrypts: `content = "[Encrypted image: file.jpg]"`, `tags = [["imeta", "url ...", "m ...", "x ...", "nonce ...", "enc mip04-v2"]]`
2. Web client decrypts, finds `imeta` tag, downloads and decrypts the media

## Fix
In `MessageService.SendMediaMessageAsync`, build the rumor with imeta tags
before passing to `EncryptMessageAsync`. This requires either:
- Passing the imeta metadata to EncryptMessageAsync so it can include tags in the rumor
- Or building the full rumor JSON in MessageService and having MLS encrypt raw bytes

## Files to Change
- `src/OpenChat.Core/Services/MessageService.cs` — SendMediaMessageAsync
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — EncryptMessageAsync (accept tags)
- Check MIP-04 spec for exact imeta tag format
