# Image Message Support

## Goal
Parse and render image messages received from other MLS clients (e.g., the web app).

## Background
The web app sends images as rumor events where the `content` field is empty and the image data/URL is in the event tags. OpenChat currently only reads the `content` field, so image messages show as blank.

### Evidence from logs
```
DecryptMessage: extracted content from rumor event (532 bytes -> 0 chars)
DecryptMessage: sender=6A28005A77B3BD07, epoch=1, plaintext length=0 (managed)
```
The rumor is 532 bytes but content extracts to 0 chars -- the image payload is in the tags.

## Requirements
- [x] Parse image tags from decrypted rumor events in `DecryptMessageAsync`
- [x] Store image messages with `MessageType.Image` and the URL/data in the appropriate field
- [x] Render images in the chat UI (MessageBubble) -- show "[Encrypted image: filename]" placeholder
- [x] Handle in both `HandleGroupMessageEventAsync` and `LoadOlderMessagesAsync`
- [x] Fallback display for empty content with imeta tags (show "[Image]" placeholder)

## Implementation Summary
- Added `ImageUrl` and `FileName` fields to `Message` model
- Added `ImageUrl`, `MediaType`, `FileName` fields to `MlsDecryptedMessage`
- Updated `ManagedMlsService.DecryptMessageAsync` to parse `imeta` tags from rumor event JSON
- Updated `MessageService.HandleGroupMessageEventAsync` and `LoadOlderMessagesAsync` to set `MessageType.Image` and populate image fields
- Added `IsImage` and `ImageDisplayText` properties to `MessageViewModel`
- Updated `MessageBubble.axaml` to show image placeholder with icon for image messages

## Future Work
- Implement MIP-04 ChaCha20 decryption to actually display images inline
- Handle inline base64 image data

## Status
- [x] Complete
