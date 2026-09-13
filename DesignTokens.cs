using System.Windows;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The design tokens (#857): one resource dictionary, <c>Themes/DesignTokens.xaml</c>, holds the semantic
/// spacing, padding, height, border and radius values every screen shares. App.xaml merges it for the running
/// application, MainWindow.xaml merges it for the window (so a window built by a test or a tool resolves the same
/// values), and the code-built panels read it through this accessor -- there is no second copy of a value in C#.
/// <see cref="Verify"/> runs at startup: a token that is missing or of the wrong type fails the start with a
/// message that names the key, instead of a control quietly falling back to a default. Every value is in
/// device-independent pixels, so 100–200 % DPI is the same rhythm.
/// </summary>
public static class DesignTokens
{
    public static readonly Uri Source = new("/TrMarketplaceHubDesktop;component/Themes/DesignTokens.xaml", UriKind.Relative);

    /// <summary>Every token the screens depend on, with the type the dictionary must carry it as.</summary>
    public static IReadOnlyList<(string Key, Type Type)> Required { get; } = new (string, Type)[]
    {
        ("SpaceHairline", typeof(double)), ("SpaceInline", typeof(double)), ("SpaceControl", typeof(double)), ("SpaceSection", typeof(double)), ("SpacePage", typeof(double)),
        ("ControlMargin", typeof(Thickness)), ("InputPadding", typeof(Thickness)), ("ButtonPadding", typeof(Thickness)), ("CompactButtonPadding", typeof(Thickness)),
        ("CardPadding", typeof(Thickness)), ("HeaderPadding", typeof(Thickness)), ("TabPadding", typeof(Thickness)), ("NavigationItemPadding", typeof(Thickness)), ("NavigationItemMargin", typeof(Thickness)),
        ("ControlMinHeight", typeof(double)), ("RowHeight", typeof(double)),
        ("BorderHairline", typeof(double)), ("BorderEmphasis", typeof(double)), ("CardRadius", typeof(CornerRadius)), ("ShellRadius", typeof(CornerRadius)),
        // #858: typography
        ("FontFamilyBody", typeof(System.Windows.Media.FontFamily)), ("FontFamilyMono", typeof(System.Windows.Media.FontFamily)), ("FontWeightTitle", typeof(FontWeight)), ("FontWeightKpi", typeof(FontWeight)),
        ("TextPageTitleSize", typeof(double)), ("TextSectionTitleSize", typeof(double)), ("TextSubsectionTitleSize", typeof(double)), ("TextBodySize", typeof(double)), ("TextCaptionSize", typeof(double)), ("TextKpiSize", typeof(double)), ("TextMonoSize", typeof(double)),
    };

    static ResourceDictionary? component;
    static readonly object gate = new();

    /// <summary>The dictionary the tokens are read from: the running application's resources when they carry them, otherwise the token file itself.</summary>
    public static ResourceDictionary Resources
    {
        get
        {
            var app = Application.Current?.Resources;
            if (app is not null && app.Contains("SpacePage")) return app;
            lock (gate) return component ??= (ResourceDictionary)Application.LoadComponent(Source);
        }
    }

    /// <summary>Fails fast when a required token is missing or not of its type; the message names every problem.</summary>
    public static void Verify(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var problems = new List<string>();
        foreach (var (key, type) in Required)
        {
            if (!resources.Contains(key)) { problems.Add($"{key} (eksik)"); continue; }
            var value = resources[key];
            if (value is null || !type.IsInstanceOfType(value)) problems.Add($"{key} ({type.Name} bekleniyor, {value?.GetType().Name ?? "null"} bulundu)");
        }
        if (problems.Count > 0) throw new InvalidOperationException("Tasarım kaynakları eksik veya hatalı; uygulama başlatılamaz: " + string.Join("; ", problems));
    }

    static double D(string key) => (double)Resources[key];
    static Thickness T(string key) => (Thickness)Resources[key];
    static CornerRadius R(string key) => (CornerRadius)Resources[key];

    public static double SpaceHairline => D("SpaceHairline");
    public static double SpaceInline => D("SpaceInline");
    public static double SpaceControl => D("SpaceControl");
    public static double SpaceSection => D("SpaceSection");
    public static double SpacePage => D("SpacePage");
    public static Thickness ControlMargin => T("ControlMargin");
    public static Thickness InputPadding => T("InputPadding");
    public static Thickness ButtonPadding => T("ButtonPadding");
    public static Thickness CompactButtonPadding => T("CompactButtonPadding");
    public static Thickness CardPadding => T("CardPadding");
    public static Thickness HeaderPadding => T("HeaderPadding");
    public static Thickness TabPadding => T("TabPadding");
    public static Thickness NavigationItemPadding => T("NavigationItemPadding");
    public static Thickness NavigationItemMargin => T("NavigationItemMargin");
    public static double ControlMinHeight => D("ControlMinHeight");
    public static double RowHeight => D("RowHeight");
    public static double BorderHairline => D("BorderHairline");
    public static double BorderEmphasis => D("BorderEmphasis");
    public static CornerRadius CardRadius => R("CardRadius");
    public static CornerRadius ShellRadius => R("ShellRadius");

    // #858: typography
    public static System.Windows.Media.FontFamily FontFamilyBody => (System.Windows.Media.FontFamily)Resources["FontFamilyBody"];
    public static System.Windows.Media.FontFamily FontFamilyMono => (System.Windows.Media.FontFamily)Resources["FontFamilyMono"];
    public static FontWeight FontWeightTitle => (FontWeight)Resources["FontWeightTitle"];
    public static FontWeight FontWeightKpi => (FontWeight)Resources["FontWeightKpi"];
    public static double TextPageTitleSize => D("TextPageTitleSize");
    public static double TextSectionTitleSize => D("TextSectionTitleSize");
    public static double TextSubsectionTitleSize => D("TextSubsectionTitleSize");
    public static double TextBodySize => D("TextBodySize");
    public static double TextCaptionSize => D("TextCaptionSize");
    public static double TextKpiSize => D("TextKpiSize");
    public static double TextMonoSize => D("TextMonoSize");
}
