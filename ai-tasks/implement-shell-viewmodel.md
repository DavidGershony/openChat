# Implement ShellViewModel (Alternative 8)

## Steps
1. [ ] ProfileConfiguration: add DeriveProfileName, registry read/write, WasExplicitlySet
2. [ ] Program.cs: validate --profile not npub
3. [ ] Create ShellViewModel in OpenChat.Presentation
4. [ ] Modify LoginViewModel: remove StorageService.Save dependency, return User via LoggedInUser
5. [ ] Modify MainViewModel: remove login logic, add logout callback
6. [ ] Modify App.axaml.cs: create ShellViewModel, move service creation
7. [ ] Modify MainWindow.axaml: ContentControl bound to ShellViewModel
8. [ ] Update Android MainActivity
9. [ ] Build and test
10. [ ] Run existing tests
