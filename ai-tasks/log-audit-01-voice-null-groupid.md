# Task: Fix voice message crash when groupId is null

## Status: COMPLETED

## Epic: log-audit (2026-04-02 v0.2.4 log analysis)

## Priority: P0

## Problem

Sending a voice message in a bot/DM chat crashes with `ArgumentNullException` because `ManagedMlsService.GetMediaExporterSecret(Byte[] groupId)` receives a null `groupId`. The code path assumes an MLS group context, but DM/bot chats don't have one.

From log line 4736:
```
[ERR] [OpenChat.Presentation.ViewModels.ChatViewModel] Failed to send voice message
System.ArgumentNullException: ArgumentNull_Generic Arg_ParamName_Name, inArray
   at System.Convert.ToHexString(Byte[] inArray)
   at OpenChat.Core.Services.ManagedMlsService.GetMediaExporterSecret(Byte[] groupId)
   at OpenChat.Presentation.ViewModels.ChatViewModel.SendVoiceMessageAsync()
```

## Goal

1. Guard against null `groupId` in `GetMediaExporterSecret`
2. Handle the DM/bot chat case — either skip MIP-04 encryption for non-group chats or use a different code path
3. Add a failing test that reproduces the null groupId scenario before fixing
