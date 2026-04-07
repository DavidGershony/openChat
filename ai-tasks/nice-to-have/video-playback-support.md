# Video playback support

## Status: Not Started

## Problem

The app can send files including videos (via MIP-04 Blossom upload), but has no video player for playback. Videos show as file attachments with a download button.

## What's needed

### Sending
- Already works — same as file/image upload via Blossom + MIP-04 encryption
- imeta tags already support video MIME types

### Playback — Desktop (Avalonia)
- Need a video player control — Avalonia has no built-in one
- Options: LibVLCSharp.Avalonia, Avalonia.FFmpeg, or a custom FFmpeg wrapper
- Must support common formats (mp4, webm, mov)
- Inline playback in the message bubble with play/pause/seek

### Playback — Android
- ExoPlayer available natively via AndroidX.Media3
- Inline playback or full-screen on tap

## Technology choice needed
- User must choose the video player library before implementation starts
