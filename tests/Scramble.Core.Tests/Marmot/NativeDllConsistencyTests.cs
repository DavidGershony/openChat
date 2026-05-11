using Xunit;

namespace Scramble.Core.Tests.Marmot;

/// <summary>
/// File-level DLL consistency checks — detects stale/mismatched DLL files.
/// </summary>
public class NativeDllConsistencyTests
{
    /// <summary>
    /// Detects stale DLLs by comparing the Desktop copy against the Rust build output.
    /// If you rebuild the Rust lib but forget to copy it to the Desktop project, this fails.
    /// </summary>
    [Fact]
    public void DesktopDll_MatchesRustBuildOutput_WhenBothExist()
    {
        var repoRoot = FindRepoRoot();
        Assert.SkipWhen(repoRoot == null, "Could not determine repository root");

        var desktopDll = Path.Combine(repoRoot!, "src", "Scramble.Desktop", "scramble_native.dll");
        var rustDll = Path.Combine(repoRoot!, "src", "Scramble.Native", "target", "release", "scramble_native.dll");

        Assert.SkipUnless(File.Exists(desktopDll) && File.Exists(rustDll),
            "Both DLL files must exist to compare (Desktop and Rust build output)");

        var desktopInfo = new FileInfo(desktopDll);
        var rustInfo = new FileInfo(rustDll);

        Assert.True(rustInfo.Length == desktopInfo.Length,
            $"DLL size mismatch! Desktop ({desktopInfo.Length} bytes) != Rust build ({rustInfo.Length} bytes). " +
            "The Desktop DLL is likely stale. Copy the freshly built DLL: " +
            "copy src\\Scramble.Native\\target\\release\\scramble_native.dll src\\Scramble.Desktop\\");
    }

    [Fact]
    public void NativeDll_ExistsInTestOutput()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "scramble_native.dll");
        Assert.True(File.Exists(dllPath),
            $"scramble_native.dll not found in test output ({AppContext.BaseDirectory}). " +
            "Build the Rust project and copy the DLL to src/Scramble.Desktop/");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
