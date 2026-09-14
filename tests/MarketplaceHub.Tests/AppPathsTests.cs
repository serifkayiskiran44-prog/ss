using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #2563 (START PATHS: never break resource/template paths when the EXE is launched from a different working
// folder). TemplatesDirectory and DefaultDataRoot must resolve the same way no matter what the process's current
// working directory is -- a Desktop shortcut, the Start Menu (an empty WorkingDirectory), a cmd opened anywhere, a
// path containing spaces or Unicode. A missing templates folder, a missing template file and an oversized one are
// each a named, typed result -- never a silently created empty file in whatever folder happened to be current.
[TestClass]
public sealed class AppPathsTests
{
    [TestMethod]
    public void TemplatesDirectoryAndDefaultDataRootNeverChangeWithTheCurrentWorkingDirectory()
    {
        var originalCwd = Environment.CurrentDirectory;
        var expectedTemplates = AppPaths.TemplatesDirectory; var expectedDataRoot = AppPaths.DefaultDataRoot;
        Assert.IsTrue(Path.IsPathRooted(expectedTemplates)); Assert.IsTrue(Path.IsPathRooted(expectedDataRoot));
        Assert.IsFalse(expectedDataRoot.Contains("MonoBridgeDesktop" + Path.DirectorySeparatorChar + "MonoBridgeDesktop", StringComparison.Ordinal), "sanity: not doubled");

        // A fixture directory whose name has both a space and a non-ASCII character -- the classic path that breaks naive relative resolution.
        var fixture = Path.Combine(Path.GetTempPath(), "start path ünïcode " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixture);
            foreach (var candidate in new[] { Path.GetTempPath(), fixture, @"C:\Windows\System32" })
            {
                if (!Directory.Exists(candidate)) continue; // System32 may be inaccessible as a CWD in some sandboxes; skip rather than fail on an environment quirk
                try { Environment.CurrentDirectory = candidate; }
                catch (Exception) { continue; } // some environments refuse System32 as a working directory outright; that refusal is not this test's concern
                Assert.AreEqual(expectedTemplates, AppPaths.TemplatesDirectory, $"TemplatesDirectory changed when CWD became {candidate}");
                Assert.AreEqual(expectedDataRoot, AppPaths.DefaultDataRoot, $"DefaultDataRoot changed when CWD became {candidate}");
            }
        }
        finally { Environment.CurrentDirectory = originalCwd; Cleanup(fixture); }
    }

    [TestMethod]
    public void AMissingTemplatesFolderAMissingFileAndAnOversizedOneAreEachATypedResultNeverASilentEmptyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "templates-resolve-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The templates folder itself does not exist yet.
            var missingDirectory = AppPaths.ResolveTemplate("entegra-standart-etsy.json", root);
            Assert.AreEqual((TemplateResolution.DirectoryMissing, (string?)null), (missingDirectory.State, missingDirectory.Path)); StringAssert.Contains(missingDirectory.Words, "Şablon klasörü bulunamadı");
            Assert.IsFalse(Directory.Exists(root), "resolving a missing folder must never create it");

            Directory.CreateDirectory(root);

            // The folder exists but the named file does not.
            var missingFile = AppPaths.ResolveTemplate("entegra-standart-etsy.json", root);
            Assert.AreEqual((TemplateResolution.FileMissing, (string?)null), (missingFile.State, missingFile.Path));
            Assert.AreEqual(0, Directory.GetFiles(root).Length, "resolving a missing file must never create one");

            // An oversized file.
            var oversized = Path.Combine(root, "too-large.json"); File.WriteAllText(oversized, new string('a', (int)AppPaths.MaxTemplateBytes + 10));
            var tooLarge = AppPaths.ResolveTemplate("too-large.json", root); Assert.AreEqual(TemplateResolution.TooLarge, tooLarge.State);

            // A real, small file is found.
            var real = Path.Combine(root, "real.json"); File.WriteAllText(real, "{}");
            var found = AppPaths.ResolveTemplate("real.json", root); Assert.AreEqual((TemplateResolution.Found, real), (found.State, found.Path));
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
