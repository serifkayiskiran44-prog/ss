using System.Windows;

namespace TrMarketplaceHubDesktop;

public static class HepsiburadaPanel
{
    public static FrameworkElement Create(string? directory = null) => new System.Windows.Controls.TextBlock
    {
        Text = "Hepsiburada bağlantısını Pazaryeri hesapları bölümünden mağaza bazında ekleyin.",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(18)
    };

    public static HepsiburadaWorkspacePanel CreateForConnection(string connectionId, string? directory = null) => new(connectionId, directory);
}
