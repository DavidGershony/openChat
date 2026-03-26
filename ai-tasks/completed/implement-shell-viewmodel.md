# Implement ShellViewModel (Alternative 8)

## Steps
1. [x] ProfileConfiguration: add DeriveProfileName, registry read/write, WasExplicitlySet
2. [x] Program.cs: validate --profile not npub
3. [x] Create ShellViewModel in OpenChat.Presentation
4. [x] Modify LoginViewModel: remove StorageService.Save dependency, return User via LoggedInUser
5. [x] Modify MainViewModel: remove login logic, add logout callback
6. [x] Modify App.axaml.cs: create ShellViewModel, move service creation
7. [x] Modify MainWindow.axaml: ContentControl bound to ShellViewModel
8. [x] Update Android MainActivity
9. [x] Build and test
10. [x] Run existing tests
