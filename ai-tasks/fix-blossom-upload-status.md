# Fix: Blossom upload rejection and status banner not updating

## Problems
1. **Blossom rejection after signer approval**: The server's HTTP error body is discarded in
   `TryUploadAsync` — only logged at Warning level. The exception reaching the UI is generic
   "Blossom upload failed with both anonymous and authenticated attempts", hiding the actual
   rejection reason (e.g., "unauthorized", "pubkey not whitelisted", etc.).

2. **Status banner not updating properly**: File upload path missing "Waiting for signer
   approval..." status (voice path has it). During Amber approval, shows misleading
   "Uploading to Blossom..." text. The server's error reason never reaches `UploadStatus`.

3. **Wasteful anonymous upload for signer users**: Always tries anonymous upload first
   even when `privateKeyHex` is null (external signer mode).

## Fix
1. [x] `BlossomUploadService`: Capture and propagate the server error body in the exception message
2. [x] `BlossomUploadService`: Skip anonymous upload when no private key and external signer available
3. [x] `ChatViewModel.AttachAndSendFileAsync`: Add "Waiting for signer approval..." status
4. [x] Write tests (5 passing): error propagation, signer skip anonymous, interface access
5. [x] Run tests and verify — all 5 BlossomUploadServiceTests pass
