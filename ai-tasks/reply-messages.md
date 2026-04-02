# Task: Implement message reply on Desktop and Mobile

## Status: Not Started

## Problem

Users cannot reply to specific messages in chat. There is no quote-reply or thread UI. This is a basic messaging feature expected in any chat app.

## Goal

### Desktop (Avalonia)
1. Add a reply action to each message bubble (e.g. right-click context menu or hover button)
2. When replying, show a preview bar above the message input with the quoted message content and sender
3. Allow dismissing the reply preview (cancel reply)
4. When sent, include the replied-to message ID in the event tags (NIP standard `e` tag with `reply` marker)
5. Display replied-to messages inline in the chat (quoted bubble above the reply)

### Android
1. Add a reply action (long-press context menu or swipe gesture)
2. Show reply preview bar above the message input
3. Same send behavior (include reply `e` tag)
4. Display quoted messages inline in chat bubbles

### Shared (Core/Presentation)
1. Add `ReplyToMessageId` field to the Message model
2. Update `SendMessageAsync` to include reply tags in the MLS-encrypted rumor
3. Parse reply tags on incoming messages and populate `ReplyToMessageId`
4. Load the replied-to message content for display
