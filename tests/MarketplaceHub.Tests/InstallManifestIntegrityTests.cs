using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// #2566 (INSTALL INTEGRITY: verify publish-manifest.json hashes inside the installer before copying anything). The
// installer previously checked only that TrMarketplaceHubDesktop.exe existed, then copied the whole package -- a
// tampered DLL, a missing template, a manifest edited to match a tampered file, or a path-traversal entry would all
// install without complaint. installer/New-PublishManifest.ps1 (new) generates a per-file SHA256 manifest plus a
// separate sha256 sidecar covering the manifest itself; installer/Install-MonoBridge.ps1 now verifies the whole
// chain -- sidecar, JSON parse, path safety, duplicate/case collisions, per-file hash/length, and full 1:1
// correspondence between manifest entries and real files -- and fails closed, before touching the install
// directory at all, on any mismatch.
[TestClass]
public sealed class InstallManifestIntegrityTests
{
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
        var root = Path.Combine(Path.GetTempPath(), "manifest-fixture-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void MakeFakePackage(string packageDirectory)
    {
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllBytes(Path.Combine(packageDirectory, ExeName), new byte[] { 0x4D, 0x5A, 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(packageDirectory, "SomeLibrary.dll"), new byte[] { 9, 9, 9, 9 });
        File.WriteAllText(Path.Combine(packageDirectory, "TrMarketplaceHubDesktop.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(packageDirectory, "TrMarketplaceHubDesktop.deps.json"), "{}");
        Directory.CreateDirectory(Path.Combine(packageDirectory, "templates"));
        File.WriteAllText(Path.Combine(packageDirectory, "templates", "sample.xml"), "<root/>");
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

    static void WriteManifest(string packageDirectory)
    {
        var (exitCode, output) = RunScript("installer/New-PublishManifest.ps1", "-PackageDirectory", packageDirectory);
        Assert.AreEqual(0, exitCode, "manifest generation must succeed: " + output);
    }

    static (int ExitCode, string Output) RunInstall(string package, string install, string startMenu, string desktop) =>
        RunScript("installer/Install-MonoBridge.ps1",
            "-PackageDirectory", package, "-InstallDirectory", install,
            "-StartMenuProgramsDirectory", startMenu, "-DesktopDirectory", desktop);

    static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void ValidPackageInstallsSuccessfullyWhenEveryManifestedFileMatchesExactly()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreEqual(0, exitCode, "a package whose manifest matches its files exactly must install: " + output);
            Assert.IsTrue(File.Exists(Path.Combine(install, ExeName)));
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void NewPublishManifestNeverListsItselfOrItsOwnSidecarAsAManifestedFile()
    {
        var package = Fixture("package");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var json = File.ReadAllText(Path.Combine(package, "publish-manifest.json"));
            Assert.IsFalse(json.Contains("publish-manifest.json", StringComparison.OrdinalIgnoreCase), "the manifest must not hash itself -- a self-referential entry proves nothing");
            Assert.IsFalse(json.Contains("publish-manifest.sha256", StringComparison.OrdinalIgnoreCase), "the manifest must not list its own integrity sidecar either");
        }
        finally { Cleanup(package); }
    }

    static string InstallDirectoryUnchangedFixture(string install)
    {
        var sentinel = Path.Combine(install, "already-installed-marker.txt");
        File.WriteAllText(sentinel, "pre-existing install must survive a failed re-install attempt");
        return sentinel;
    }

    [TestMethod]
    public void InstallFailsClosedAndLeavesTheInstallDirectoryUntouchedWhenADllHasBeenModifiedAfterManifestGeneration()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            File.WriteAllBytes(Path.Combine(package, "SomeLibrary.dll"), new byte[] { 1, 2, 3, 4, 5 }); // tampered after manifest was written
            var sentinel = InstallDirectoryUnchangedFixture(install);

            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "a hash mismatch on any file must fail the install closed: " + output);
            Assert.IsFalse(File.Exists(Path.Combine(install, ExeName)), "nothing from the rejected package may be copied in");
            Assert.IsTrue(File.Exists(sentinel), "a pre-existing install directory must remain exactly as it was on a failed re-install");
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenTheMainExeHasBeenModifiedAfterManifestGeneration()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            File.WriteAllBytes(Path.Combine(package, ExeName), new byte[] { 0x4D, 0x5A, 9, 9, 9, 9 });
            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "a tampered EXE must fail the install closed even though it is the launch target: " + output);
            Assert.IsFalse(Directory.Exists(install) && File.Exists(Path.Combine(install, ExeName)));
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenAManifestedTemplateFileIsMissingFromThePackage()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            File.Delete(Path.Combine(package, "templates", "sample.xml"));
            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "a manifested file missing from disk must fail closed, not just skip that file: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenARealPackageFileHasNoManifestEntry()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            File.WriteAllBytes(Path.Combine(package, "unexpected-payload.dll"), new byte[] { 6, 6, 6 }); // added after manifest was written, never declared
            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "an extra file the manifest never declared must fail the install closed: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    static JsonObject LoadManifest(string package) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(package, "publish-manifest.json")))!;

    static JsonObject FindEntry(JsonObject manifest, string pathSuffix) =>
        (JsonObject)manifest["Files"]!.AsArray().First(n => n!["Path"]!.ToString().EndsWith(pathSuffix, StringComparison.Ordinal))!;

    static void SaveManifestAndSidecar(string package, JsonObject manifest)
    {
        File.WriteAllText(Path.Combine(package, "publish-manifest.json"), manifest.ToJsonString());
        RewriteSidecar(package);
    }

    [TestMethod]
    public void InstallFailsClosedWhenTheManifestContainsADuplicatePathEntry()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var manifest = LoadManifest(package);
            var original = FindEntry(manifest, "SomeLibrary.dll");
            var duplicate = new JsonObject { ["Path"] = original["Path"]!.ToString(), ["Length"] = original["Length"]!.GetValue<long>(), ["Sha256"] = original["Sha256"]!.ToString() };
            manifest["Files"]!.AsArray().Add(duplicate);
            SaveManifestAndSidecar(package, manifest);

            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "a duplicated manifest path must fail closed: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenAManifestPathAttemptsDirectoryTraversalOutsideThePackageRoot()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var manifest = LoadManifest(package);
            manifest["Files"]!.AsArray().Add(new JsonObject { ["Path"] = "../../evil.exe", ["Length"] = 6, ["Sha256"] = new string('0', 64) });
            SaveManifestAndSidecar(package, manifest);

            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "a '../' manifest path must never be allowed to escape the package root: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenTwoManifestEntriesCollideOnlyByCase()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var manifest = LoadManifest(package);
            var original = FindEntry(manifest, "SomeLibrary.dll");
            var caseVariant = new JsonObject { ["Path"] = original["Path"]!.ToString().ToUpperInvariant(), ["Length"] = original["Length"]!.GetValue<long>(), ["Sha256"] = original["Sha256"]!.ToString() };
            manifest["Files"]!.AsArray().Add(caseVariant);
            SaveManifestAndSidecar(package, manifest);

            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "Windows filesystems are case-insensitive -- two entries differing only by case is the same collision as an exact duplicate: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenThePublishManifestJsonIsCorrupt()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var manifestPath = Path.Combine(package, "publish-manifest.json");
            File.WriteAllText(manifestPath, "{ this is not valid json ][");
            RewriteSidecar(package);

            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "malformed manifest JSON must fail closed with a clear parse error, not proceed: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    [TestMethod]
    public void InstallFailsClosedWhenTheManifestSidecarNoLongerMatchesTheManifestFileItself()
    {
        var package = Fixture("package"); var install = Fixture("install"); var startMenu = Fixture("startmenu"); var desktop = Fixture("desktop");
        try
        {
            MakeFakePackage(package);
            WriteManifest(package);
            var manifestPath = Path.Combine(package, "publish-manifest.json");
            File.AppendAllText(manifestPath, " "); // manifest edited after its sidecar hash was computed
            var (exitCode, output) = RunInstall(package, install, startMenu, desktop);
            Assert.AreNotEqual(0, exitCode, "the manifest itself must be tamper-evident via its own sidecar hash, independent of any per-file hash it lists: " + output);
        }
        finally { Cleanup(package, install, startMenu, desktop); }
    }

    static void RewriteSidecar(string package)
    {
        var manifestPath = Path.Combine(package, "publish-manifest.json");
        var bytes = File.ReadAllBytes(manifestPath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        File.WriteAllText(Path.Combine(package, "publish-manifest.sha256"), hash);
    }
}
