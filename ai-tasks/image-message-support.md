# Image Message Support

## Goal
Parse and render image messages received from other MLS clients (e.g., the web app).

## Background
The web app sends images as rumor events where the `content` field is empty and the image data/URL is in the event tags. OpenChat currently only reads the `content` field, so image messages show as blank.

### Evidence from logs
```
DecryptMessage: extracted content from rumor event (532 bytes → 0 chars)
DecryptMessage: sender=6A28005A77B3BD07, epoch=1, plaintext length=0 (managed)
```
The rumor is 532 bytes but content extracts to 0 chars — the image payload is in the tags.

## Requirements
- [ ] Investigate the web app's rumor event format for images (likely `imeta` tag or similar with a URL)
- [ ] Parse image tags from decrypted rumor events in `DecryptMessageAsync` or `HandleGroupMessageEventAsync`
- [ ] Store image messages with `MessageType.Image` and the URL/data in the appropriate field
- [ ] Render images in the chat UI (ChatView) — show image inline or as a thumbnail
- [ ] Handle both URL-based images and inline base64 image data
- [ ] Fallback display for unsupported media types (show "[Image]" placeholder)

## Technical Notes
- The decrypted rumor event is a full Nostr event JSON — need to parse `tags` array, not just `content`
- Check MIP-03 spec for media attachment format
- The `Message` model may need an `ImageUrl` or `MediaUrl` field
- Consider adding `MessageType.Image` enum value if not already present

## Status
- [ ] Not started
