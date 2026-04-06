# Bug: Android shows file picker twice when sending a file

## Problem
On mobile, when the user clicks to send a file:
1. Permission prompt appears (normal)
2. Image gallery picker appears — user selects an image
3. A SECOND file picker appears (different UI, looks like a system file browser)
4. User must select the file again in the second picker
5. Only then does the file get attached

## Expected Behavior
After selecting an image in the first picker, the file should be attached immediately
without showing a second picker.

## Investigation Notes
- The two different UIs suggest two separate picker intents are being fired:
  - First: likely an image gallery intent (ACTION_PICK or MediaStore)
  - Second: likely a file chooser intent (ACTION_GET_CONTENT or ACTION_OPEN_DOCUMENT)
- Check ChatFragment.cs file picker launcher setup and the FilePickerFunc callback
- Check if the permission callback re-triggers the picker
- Check if there's a race condition between permission grant and picker launch

## Key Files to Check
- src/OpenChat.Android/Fragments/ChatFragment.cs — file picker launcher, permission handling
- src/OpenChat.Presentation/ViewModels/ChatViewModel.cs — AttachFileCommand, FilePickerFunc

## Steps
- [ ] Investigate Android file picker code flow
- [ ] Identify why two pickers are launched
- [ ] Write fix
- [ ] Test
- [ ] Commit
