using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;
public partial class MainWindow
{
 FrameworkElement BuildExcel()
 {
  var panel=new StackPanel{Margin=new Thickness(20),MaxWidth=900};
  panel.Children.Add(Heading("Excel ürün işlemleri"));
  panel.Children.Add(Hint("Kolonlar başlık adına göre eşlenir. İstersen kolonları elle eşleyebilir, hataları Excel olarak dışa aktarabilir ve yalnızca seçili satırları tek atomik işlemde uygulayabilirsin."));
  var status=Hint("");
  var grid=new DataGrid{AutoGenerateColumns=true,Height=300,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Extended};
  ExcelPreview? preview=null; string? selectedPath=null; ExcelColumnMapping? manualMapping=null; CatalogUndoReceipt? undo=null;
  var export=Button("Ürünleri Excel'e aktar",()=>{var d=new SaveFileDialog{Filter="Excel dosyası (*.xlsx)|*.xlsx",FileName="urunler.xlsx"};if(d.ShowDialog(this)!=true)return;var products=store.Products();CatalogExcel.Export(d.FileName,products);status.Text=$"{products.Count} ürün dışa aktarıldı.";});
  var choose=Button("Excel seç ve önizle",()=>{var d=new OpenFileDialog{Filter="Excel dosyası (*.xlsx)|*.xlsx"};if(d.ShowDialog(this)!=true)return;selectedPath=d.FileName;manualMapping=null;preview=CatalogExcel.Preview(d.FileName);grid.ItemsSource=preview.Rows;status.Text=preview.Errors.Count==0?$"{preview.Rows.Count} satır hazır; kolonları elle eşlemek veya satır seçip uygulamak mümkün.":$"{preview.Rows.Count} satır hazır; {preview.Errors.Count} hata var; uygulama engellendi.";});
  var map=Button("Kolonları elle eşle",()=>{if(selectedPath==null){status.Text="Önce Excel dosyasını seçin.";return;}var headers=CatalogExcel.Headers(selectedPath);var result=ShowMappingDialog(headers,manualMapping);if(result==null)return;manualMapping=result;preview=CatalogExcel.Preview(selectedPath,manualMapping);grid.ItemsSource=preview.Rows;status.Text=preview.Errors.Count==0?$"{preview.Rows.Count} satır hazır; manuel eşleme kullanıldı.":$"{preview.Rows.Count} satır hazır; {preview.Errors.Count} hata var; uygulama engellendi.";});
  var errors=Button("Hataları Excel'e aktar",()=>{if(preview==null||preview.Errors.Count==0){status.Text="Dışa aktarılacak önizleme hatası yok.";return;}var d=new SaveFileDialog{Filter="Excel dosyası (*.xlsx)|*.xlsx",FileName="excel-hatalari.xlsx"};if(d.ShowDialog(this)!=true)return;CatalogExcel.ExportErrors(d.FileName,preview);status.Text=$"{preview.Errors.Count} hata dışa aktarıldı.";});
  var apply=Button("Seçili önizleme satırlarını uygula",()=>{if(preview==null||selectedPath==null){status.Text="Önce bir Excel dosyasını önizleyin.";return;}var source=new XmlSource{Id="excel-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(selectedPath))).ToLowerInvariant()[..16],Name=Path.GetFileName(selectedPath)};var selected=grid.SelectedItems.Cast<CatalogProduct>().Select(p=>preview.Rows.IndexOf(p)).ToArray();undo=CatalogExcel.ApplyWithUndo(store,source,preview,selected);status.Text="Katalog güncellendi ve geri alma kaydı oluşturuldu.";RefreshProducts();});
  var rollback=Button("Son Excel uygulamasını geri al",()=>{if(undo==null){status.Text="Geri alınacak Excel işlemi yok.";return;}store.Undo(undo);undo=null;status.Text="Son Excel uygulaması geri alındı.";RefreshProducts();});
  var bar=new WrapPanel();bar.Children.Add(export);bar.Children.Add(choose);bar.Children.Add(map);bar.Children.Add(errors);bar.Children.Add(apply);bar.Children.Add(rollback);panel.Children.Add(bar);panel.Children.Add(grid);panel.Children.Add(status);return Scroll(panel);
 }
 static ExcelColumnMapping? ShowMappingDialog(IReadOnlyList<string> headers,ExcelColumnMapping? existing)
 {
  var fields=new[]{("Sku","SKU"),("Name","Ürün adı"),("Cost","Alış"),("Price","Satış"),("Stock","Stok"),("Barcode","Barkod"),("Brand","Marka"),("Category","Kategori"),("Description","Açıklama"),("Currency","Döviz"),("Active","Aktif"),("Gtin","GTIN")};
  var window=new Window{Title="Excel kolon eşleme",Width=500,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.CanResize};
  var panel=new StackPanel{Margin=new Thickness(16)};var combos=new Dictionary<string,ComboBox>();
  foreach(var field in fields){var row=new DockPanel{Margin=new Thickness(0,3,0,3)};row.Children.Add(new TextBlock{Text=field.Item2,Width=120,VerticalAlignment=VerticalAlignment.Center});var combo=new ComboBox{ItemsSource=new[]{"(eşlenmemiş)"}.Concat(headers).ToList(),SelectedItem=existing?.Columns.TryGetValue(field.Item1,out var c)==true&&c<=headers.Count?headers[c-1]:"(eşlenmemiş)"};DockPanel.SetDock(combo,System.Windows.Controls.Dock.Right);row.Children.Add(combo);panel.Children.Add(row);combos[field.Item1]=combo;}
  var result=new ExcelColumnMapping?[] {null};var ok=new Button{Content="Eşlemeyi kullan",Margin=new Thickness(0,12,0,0)};ok.Click+=(_,_)=>{var map=new Dictionary<string,int>();foreach(var field in fields){var value=combos[field.Item1].SelectedItem?.ToString();if(value is null or "(eşlenmemiş)")continue;var index=headers.IndexOf(value);if(index>=0)map[field.Item1]=index+1;}result[0]=new ExcelColumnMapping(map);window.DialogResult=true;};panel.Children.Add(ok);window.Content=new ScrollViewer{Content=panel};window.ShowDialog();return result[0];
 }
}
