# Move diagnostic tests to a separate project

## Status: Not Started

## Problem

4 test files in `tests/OpenChat.Core.Tests/` are not real tests — they're manual investigation/debugging tools with no assertions. They clutter the test suite and inflate the test count without providing automated verification.

## Files to move

| File | What it does |
|------|-------------|
| `RelayEventDumpTests.cs` | Fetches kind 445 events from relay, dumps structure. Zero assertions. |
| `WebAppInteropInvestigationTests.cs` | 3-user web app interop investigation. Manual inspection only. |
| `ExporterSecretDiagnosticTests.cs` | Dumps key schedule for manual cross-impl comparison. |
| `TreeHashDiagnosticTest.cs` | Dumps ratchet tree/epoch data for cross-impl debugging. |

## Plan

1. Create `tests/OpenChat.Diagnostics/` project (xUnit, same references as Core.Tests)
2. Move the 4 files into it
3. Update any shared helpers/base classes as needed
4. Verify `dotnet test tests/OpenChat.Core.Tests` still passes without them
5. Add a note in the diagnostic project README that these are manual tools, not CI tests
