# Fix: Copy nsec button copies npub instead

## Goal
Fix the clipboard copy bug where clicking "Copy nsec" during new user creation copies the npub to the clipboard instead of the nsec.

## Steps
- [ ] Find the copy-to-clipboard handler for nsec in the login/registration flow
- [ ] Fix to copy the correct value (nsec, not npub)
- [ ] Test both copy buttons (nsec and npub) work correctly

## Status
- [ ] Not started
