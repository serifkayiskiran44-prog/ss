using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// #2565 (INSTALLER ENTRYPOINTS: Start Menu/Desktop shortcuts must point at the real release EXE, use the canonical
// product name, and never leave a stale duplicate behind on upgrade or uninstall). The installer is a set of
// PowerShell scripts under installer/ (Install-MonoBridge.ps1, Uninstall-MonoBridge.ps1, Smoke-Test.ps1) -- there is
// no packaged MSI/EXE setup in this repository yet, so these scripts ARE the production installer path this issue
// owns. Every scenario here drives the real scripts against temp fixture directories standing in for the Start Menu
// Programs folder and the Desktop folder, never the real Windows Start Menu or the real
// %LocalAppData%\MonoBridgeDesktop user-data folder.
[TestClass]
public sealed class InstallerShortcutTests
{
    const string CanonicalShortcutName = "MarketplaceHub Desktop.lnk";
    const string LegacyShortcutName = "MonoBridge Desktop.lnk";
    const string ExeName = "TrMarketplaceHubDesktop.exe";

    static string RepoFile(string relative)
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, relative);
            if (File.Exists(candidate)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException($"Repo file not found by walking up from the test output directory: {relative}");
    }

    static string Fixture(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "installer-fixture-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void MakeFakePackage(string packageDirectory)
    {
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllBytes(Path.Combine(packageDirectory, ExeName), new byte[] { 0x4D, 0x5A, 1, 2, 3, 4 });
        File.WriteAllText(Path.Combine(packageDirectory, "TrMarketplaceHubDesktop.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(packageDirectory, "TrMarketplaceHubDesktop.deps.json"), "{}");
    }

    static (int ExitCode, string Output) RunScript(string scriptRelativePath, params string[] args)
    {
        var script = RepoFile(scriptRelativePath);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, stdout + stderr);
    }

    static string ShortcutCreatedLine(string output, string shortcutPath)
    {
        var line = output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("shortcut-created=" + shortcutPath + "|", StringComparison.Ordinal));
        Assert.IsNotNull(line, $"No shortcut-created line found for {shortcutPath} in output:\n{output}");
        return line!;
    }

    static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void CleanInstallCreatesOneCanonicallyNamedStartMenuShortcutPointingAtTheInstalledExeWithTheInstallDirectoryAsWorkingDirectory()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            var (exitCode, output) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);
            Assert.AreEqual(0, exitCode, "install script must succeed: " + output);

            var shortcutPath = Path.Combine(startMenu, CanonicalShortcutName);
            Assert.IsTrue(File.Exists(shortcutPath), "canonical Start Menu shortcut must be created: " + output);
            Assert.IsFalse(File.Exists(Path.Combine(startMenu, LegacyShortcutName)), "no stale MonoBridge-branded shortcut should ever be written by a fresh install");
            Assert.IsFalse(File.Exists(Path.Combine(desktop, CanonicalShortcutName)), "no desktop shortcut without the explicit opt-in switch -- never a silent duplicate");

            var installedExe = Path.Combine(install, ExeName);
            var line = ShortcutCreatedLine(output, shortcutPath);
            StringAssert.Contains(line, "|target=" + installedExe + "|", "shortcut must target the canonical installed EXE, never a developer bin/obj or legacy Windows-* path");
            StringAssert.Contains(line, "|workingdirectory=" + install + "|", "working directory must be the install directory, matching the #2563 CWD-independent path contract");
            StringAssert.Contains(line, "icon=" + installedExe, "icon must come from the real installed EXE -- never an invented icon file that doesn't exist in the package");
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void ReinstallingOverAnExistingLegacyBrandedShortcutRemovesItAndLeavesExactlyOneCanonicalShortcutBehind()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            var legacyPath = Path.Combine(startMenu, LegacyShortcutName);
            File.WriteAllText(legacyPath, "stale link placeholder from a prior MonoBridge-branded install");

            var (exitCode1, output1) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);
            Assert.AreEqual(0, exitCode1, output1);
            StringAssert.Contains(output1, "legacy-shortcut-removed=" + legacyPath, "the old-branded shortcut must be atomically replaced on upgrade, not left as an orphaned duplicate: " + output1);

            var (exitCode2, output2) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);
            Assert.AreEqual(0, exitCode2, output2);

            Assert.IsFalse(File.Exists(legacyPath), "the old-branded shortcut must not reappear");
            var entries = Directory.GetFiles(startMenu, "*.lnk");
            Assert.AreEqual(1, entries.Length, "upgrade must never leave more than one shortcut for this product: " + string.Join(", ", entries));
            Assert.AreEqual(CanonicalShortcutName, Path.GetFileName(entries[0]));
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void OptingIntoADesktopShortcutCreatesExactlyOneCanonicalDesktopEntryTargetingTheInstalledExe()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            var (exitCode, output) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop, "-CreateDesktopShortcut");
            Assert.AreEqual(0, exitCode, output);

            var desktopShortcut = Path.Combine(desktop, CanonicalShortcutName);
            Assert.IsTrue(File.Exists(desktopShortcut), "the explicit opt-in switch must create the desktop shortcut: " + output);
            var line = ShortcutCreatedLine(output, desktopShortcut);
            StringAssert.Contains(line, "|target=" + Path.Combine(install, ExeName) + "|");
            Assert.AreEqual(1, Directory.GetFiles(desktop, "*.lnk").Length, "opting in must not create more than one desktop entry");
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void UninstallRemovesEveryProductShortcutAndTheInstallDirectoryButNeverTouchesTheUserDataFolder()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        var userData = Fixture("userdata-stand-in");
        try
        {
            MakeFakePackage(package);
            var credentialsFile = Path.Combine(userData, "credentials.bin");
            File.WriteAllText(credentialsFile, "stand-in for real user data -- must survive uninstall");
            var (installExit, installOutput) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop, "-CreateDesktopShortcut");
            Assert.AreEqual(0, installExit, installOutput);

            var (exitCode, output) = RunScript("installer/Uninstall-MonoBridge.ps1",
                "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);
            Assert.AreEqual(0, exitCode, output);

            Assert.IsFalse(File.Exists(Path.Combine(startMenu, CanonicalShortcutName)), "uninstall must remove the Start Menu shortcut: " + output);
            Assert.IsFalse(File.Exists(Path.Combine(desktop, CanonicalShortcutName)), "uninstall must remove the desktop shortcut too: " + output);
            Assert.IsFalse(Directory.Exists(install), "uninstall must remove the installed program files");
            Assert.IsTrue(File.Exists(credentialsFile), "uninstall must never touch the user-data folder, only the program install directory");
        }
        finally { Cleanup(package, install, startMenu, desktop, userData); }
    }

    [TestMethod]
    public void SmokeTestFailsWhenTheShortcutTargetIsMissingInsteadOfTreatingShortcutExistenceAsProofTheAppWorks()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            var (installExit, installOutput) = RunScript("installer/Install-MonoBridge.ps1",
                "-PackageDirectory", package, "-InstallDirectory", install,
                "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);
            Assert.AreEqual(0, installExit, installOutput);

            var shortcutPath = Path.Combine(startMenu, CanonicalShortcutName);
            File.Delete(Path.Combine(install, ExeName)); // simulate a broken/missing target after install

            var (exitCode, output) = RunScript("installer/Smoke-Test.ps1",
                "-PackageDirectory", package, "-StartMenuShortcutPath", shortcutPath);
            Assert.AreNotEqual(0, exitCode, "smoke test must fail when the shortcut's real target file is missing, not merely because a .lnk file exists: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }
}
