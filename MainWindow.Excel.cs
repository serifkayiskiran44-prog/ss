using System.Windows;
using System.Windows.Controls;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow
{
    FrameworkElement BuildExcel()
    {
        var tabs=new TabControl();
        tabs.Items.Add(new TabItem{Header="Ürün / Fiyat / Stok",Content=new ExcelWorkspacePanel(store,dataDirectory,"products",RefreshProducts)});
        tabs.Items.Add(new TabItem{Header="Kategori Excel",Content=new ExcelWorkspacePanel(store,dataDirectory,"categories",RefreshProducts)});
        tabs.Items.Add(new TabItem{Header="Sipariş Excel",Content=new ExcelWorkspacePanel(store,dataDirectory,"orders",RefreshProducts)});
        return tabs;
    }
}
