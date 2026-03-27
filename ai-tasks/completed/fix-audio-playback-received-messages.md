# Fix: Received voice messages not playing in UI

## Problem
When a voice message is received (from self or other users), OpenChat shows an error
instead of an audio player. The log shows `ValidateImageMagicBytes: unknown MIME type audio/opus`
— the UI tries to render it as an image instead of detecting it as audio.

## Root Cause
The message rendering pipeline doesn't check `MessageType.Audio` or `MediaType == "audio/opus"`
to route to an audio player component. All media falls through to the image handler.

## What Works
- Recording and encoding (PCM → Opus) works
- Blossom upload works
- MIP-04 encryption and imeta tags work
- Web client can play the voice message fine
- OpenChat can play its OWN recordings (before send)

## What's Broken
- Received voice messages (from relay) are not rendered as audio
- The imeta tag has `m audio/opus` but the UI doesn't check for audio MIME types

## Fix
- In the message bubble/renderer component, check if `MediaType` starts with `audio/`
  or `MessageType == Audio`, and render an audio player instead of an image
- The audio player component already exists (used for recording playback)
- Just needs wiring for received messages
