using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #2562 (RELEASE IDENTITY: retire MonoBridge remnants for the MarketplaceHub canonical name/path convention). The
// user-visible product identity -- window title, onboarding, support summary, backup/restore dialogs, the startup
// runbook -- names the product MarketplaceHub in every place a user actually reads it; the internal app-data folder
// name is explicitly out of this issue's scope (it is real user data location, owned by the per-store migration
// issues) and is not touched here. The startup runbook no longer claims a separate .NET Desktop Runtime is required,
// which is false for this project's self-contained win-x64 publish.
[TestClass]
public sealed class ReleaseIdentityTests
{
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

    [TestMethod]
    public void TheProductNameIsMarketplaceHubEverywhereAUserActuallyReadsItAndNeverTheRetiredMonoBridgeBranding()
    {
        Assert.AreEqual("MarketplaceHub Desktop", AppVersion.Product); Assert.IsFalse(AppVersion.Display.Contains("MonoBridge", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("MarketplaceHub destek özeti", SupportSummary.Header);

        var mainWindowXaml = File.ReadAllText(RepoFile("MainWindow.xaml"));
        StringAssert.Contains(mainWindowXaml, "Title=\"MarketplaceHub — Yönetim Merkezi\""); Assert.IsFalse(mainWindowXaml.Contains("Title=\"MonoBridge", StringComparison.Ordinal));

        AssertNoUserFacingMonoBridgeBranding("OnboardingPanel.cs");
        AssertNoUserFacingMonoBridgeBranding("SupportSummary.cs");
        AssertNoUserFacingMonoBridgeBranding("DataBackupPanel.cs");
        AssertNoUserFacingMonoBridgeBranding("MigrationAssistantPanel.cs");
        AssertNoUserFacingMonoBridgeBranding("AppVersion.cs");
    }

    [TestMethod]
    public void TheStartupRunbookNamesMarketplaceHubDescribesTheSelfContainedPublishAndNeverClaimsARuntimePrerequisite()
    {
        var runbook = File.ReadAllText(RepoFile("BASLA.txt"));
        StringAssert.Contains(runbook, "MARKETPLACEHUB MASAÜSTÜ"); StringAssert.Contains(runbook, "self-contained"); StringAssert.Contains(runbook, "TrMarketplaceHubDesktop.exe");
        Assert.IsFalse(runbook.Contains("Runtime gerekir", StringComparison.OrdinalIgnoreCase), "a self-contained publish needs no separate .NET Desktop Runtime install -- this claim must not remain");
        StringAssert.Contains(runbook, "GEREKMEZ"); // the corrected, explicit statement that no runtime install is required
        StringAssert.Contains(runbook, "Yedekleme"); StringAssert.Contains(runbook, "Kaldırma"); // backup/recovery and uninstall behavior, per the issue's own runbook requirement
        Assert.IsFalse(runbook.Contains("MONOBRIDGE MASAÜSTÜ", StringComparison.Ordinal), "the retired header must not remain");
    }

    /// <summary>A file may still name the internal %LOCALAPPDATA%\MonoBridgeDesktop storage folder (out of this issue's scope) but must carry no other "MonoBridge" mention -- every remaining occurrence in the file's text, once every "MonoBridgeDesktop" folder-name token is removed, must be zero.</summary>
    static void AssertNoUserFacingMonoBridgeBranding(string relativePath)
    {
        var text = File.ReadAllText(RepoFile(relativePath));
        var withoutFolderToken = text.Replace("MonoBridgeDesktop", "");
        Assert.IsFalse(withoutFolderToken.Contains("MonoBridge", StringComparison.OrdinalIgnoreCase), $"{relativePath} still names the retired MonoBridge brand outside the internal storage folder token");
    }
}
