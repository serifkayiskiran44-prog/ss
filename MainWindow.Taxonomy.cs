using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;
public partial class MainWindow
{
 FrameworkElement BuildTaxonomy()
 {
  var taxonomy=new TaxonomyStore(dataDirectory);var panel=new StackPanel{Margin=new Thickness(20),MaxWidth=1050};panel.Children.Add(Heading("Kategori, marka ve özellik yönetimi"));panel.Children.Add(Hint("Bu ekran yerel katalog sözlüğünü ve harici anahtar eşlemelerini yönetir. Canlı pazaryeri kategorisi indirme/uygulama connector sözleşmesi olmadan yapılmaz."));
  var kind=new ComboBox{ItemsSource=Enum.GetValues<TaxonomyKind>(),SelectedItem=TaxonomyKind.Category,Width=180};var name=new TextBox{Width=220};var value=new TextBox{Width=220};var external=new TextBox{Width=220};var status=Hint("");var grid=new DataGrid{AutoGenerateColumns=true,IsReadOnly=true,Height=300};
  void Refresh(){var selected=(TaxonomyKind)kind.SelectedItem!;grid.ItemsSource=taxonomy.List(selected);status.Text=$"{taxonomy.List(selected).Count} kayıt";}
  kind.SelectionChanged+=(_,_)=>Refresh();
  var save=Button("Kaydet",()=>{taxonomy.Save(new TaxonomyEntry{Kind=(TaxonomyKind)kind.SelectedItem!,Name=name.Text,Value=value.Text});name.Clear();value.Clear();Refresh();});
  var map=Button("Harici anahtarı seçili yerel kayda eşle",()=>{if(grid.SelectedItem is not TaxonomyEntry entry)throw new InvalidOperationException("Önce listeden yerel kayıt seçin.");taxonomy.Map((TaxonomyKind)kind.SelectedItem!,external.Text,entry.Id);external.Clear();status.Text="Harici anahtar eşlendi.";});
  var row=new WrapPanel();row.Children.Add(new TextBlock{Text="Tür",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});row.Children.Add(kind);row.Children.Add(new TextBlock{Text="Ad",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});row.Children.Add(name);row.Children.Add(new TextBlock{Text="Değer",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});row.Children.Add(value);row.Children.Add(save);panel.Children.Add(row);var mappingRow=new WrapPanel();mappingRow.Children.Add(new TextBlock{Text="Harici anahtar",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});mappingRow.Children.Add(external);mappingRow.Children.Add(map);panel.Children.Add(mappingRow);panel.Children.Add(grid);panel.Children.Add(status);Refresh();return Scroll(panel);
 }
}
