using System.Windows;

namespace TrMarketplaceHubDesktop;
public static class TrendyolPanel
{
    public static FrameworkElement Create(string? directory=null)=>new TrendyolWorkspacePanel(directory);
}
