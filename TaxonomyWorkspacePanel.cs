using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClosedXML.Excel;
using Microsoft.Win32;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public sealed partial class TaxonomyWorkspacePanel : Grid
{
 readonly TaxonomyStore store;readonly CatalogStore catalog;readonly TaxonomyContentStore templates;readonly TaxonomyKind kind;readonly Action changed;
 readonly string? workspaceDirectory;
 readonly DataGrid list=new(){Name="TaxonomyList",AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,CanUserAddRows=false,CanUserDeleteRows=false,RowHeight=32,HeadersVisibility=DataGridHeadersVisibility.Column}; readonly TabControl tabs=new();readonly TextBox search=new(){Width=220};readonly TextBox name=new();readonly ComboBox parent=new(){IsEditable=false,DisplayMemberPath="Name"};readonly CheckBox active=new(){Content="Aktif",IsChecked=true};
 readonly ComboBox channel=new(){Name="TaxonomyChannel",IsEditable=true,ItemsSource=XmlCategoryRules.Channels.Concat(new[]{"N11","AliExpress","local"}).Distinct().ToArray(),SelectedItem="Trendyol",Width=160};
 readonly TextBox shop=new(){Text="default",Width=150};readonly TextBox external=new();readonly DataGrid mappings=new(){Name="TaxonomyMappings",IsReadOnly=true,AutoGenerateColumns=false,Height=160};
 readonly TextBox title=new(){Text="{Name}"};readonly TextBox description=new(){Text="{Description}",AcceptsReturn=true,Height=105,TextWrapping=TextWrapping.Wrap};
 readonly TextBox brand=new();readonly TextBox attributes=new(){AcceptsReturn=true,Height=95};readonly TextBox required=new();readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap};
 readonly DataGrid preview=new(){IsReadOnly=true,AutoGenerateColumns=true,MinHeight=140};IReadOnlyList<TaxonomyContentPreview> rendered=Array.Empty<TaxonomyContentPreview>();
 TaxonomyEntry? selected;IReadOnlyList<TaxonomyEntry> entries=Array.Empty<TaxonomyEntry>();IReadOnlyList<CatalogProduct> products=Array.Empty<CatalogProduct>();int version;
 string Channel=>(channel.SelectedItem as string??channel.Text).Trim();string Shop=>shop.Text.Trim();
 public TaxonomyWorkspacePanel(string? directory,TaxonomyKind kind,Action changed)
 {
  this.kind=kind;this.changed=changed;workspaceDirectory=directory;catalog=new(directory);store=new(directory);templates=new(directory);Margin=new(12);
  RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});RowDefinitions.Add(new(){Height=GridLength.Auto});
  Children.Add(BuildToolbar());
  SetRow(tabs,1);Children.Add(tabs);tabs.Items.Add(new TabItem{Header="Liste",Content=list});
  var edit=new StackPanel{Margin=new(12)};edit.Children.Add(T(kind==TaxonomyKind.Category?"Kategori adı":"Marka adı"));edit.Children.Add(name);if(kind==TaxonomyKind.Category){edit.Children.Add(T("Üst kategori"));edit.Children.Add(parent);}
  edit.Children.Add(active);var actions=new WrapPanel();actions.Children.Add(B("Kaydet",SaveEntry));actions.Children.Add(B("Sil",DeleteEntry));edit.Children.Add(actions);edit.Children.Add(T("Ad değişikliği bu kaydı kullanan ürünlere de uygulanır. Alt kategoriler ve eşleme kimlikleri korunur."));
  tabs.Items.Add(new TabItem{Header="Genel",Content=edit});
  var mapping=new StackPanel{Margin=new(12)};var scope=new WrapPanel();scope.Children.Add(T("Pazaryeri"));scope.Children.Add(channel);scope.Children.Add(T("Mağaza"));scope.Children.Add(shop);mapping.Children.Add(scope);
  mapping.Children.Add(T(kind==TaxonomyKind.Category?"Pazaryeri kategori ID / yolu":"Pazaryeri marka ID / adı"));mapping.Children.Add(external);var mapActions=new WrapPanel();mapActions.Children.Add(B("Eşle",Map));mapActions.Children.Add(B("Seçili eşlemeyi kaldır",Unmap));mapping.Children.Add(mapActions);
  foreach(var (label,path) in new[]{("Pazaryeri","Marketplace"),("Mağaza","ShopId"),("Karşılık","ExternalKey"),("Durum","Status")})mappings.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(path),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
  mapping.Children.Add(mappings);mapping.Children.Add(T("Bu alanlar içe aktarma eşleştirmeleridir. Trendyol API listelerini almak ve gönderim için otomatik ID eşleştirmek üzere sol menüdeki Trendyol → Kategori ve marka sekmesini kullanın. API ID'si Liste sekmesinde ayrıca görünür."));
  tabs.Items.Add(new TabItem{Header="Pazaryeri eşleştirmeleri",Content=mapping});
  var template=new StackPanel{Margin=new(12)};template.Children.Add(T("Pazaryeri ve mağaza: Eşleştirmeler sekmesindeki seçim kullanılır."));
  template.Children.Add(B("Bu kanalın şablonunu yükle",LoadTemplate));
  template.Children.Add(T("Ürün başlığı ({Name}, {Sku}, {Brand})"));template.Children.Add(title);template.Children.Add(T("Açıklama: {Description}, {Description2} veya {Description3}"));template.Children.Add(description);
  template.Children.Add(T("Pazaryerinde görünecek marka (boş: mevcut marka)"));template.Children.Add(brand);
  template.Children.Add(T("Kategori özellikleri — satır başına Ad=Değer"));template.Children.Add(attributes);template.Children.Add(T("Zorunlu özellik adları — virgülle ayırın"));template.Children.Add(required);
  var templateActions=new WrapPanel();templateActions.Children.Add(B("Şablonu kaydet",()=>{var t=Capture();version=templates.Save(t).Version;status.Text="Kanal şablonu kaydedildi.";InvalidatePreview();}));if(kind==TaxonomyKind.Category)templateActions.Children.Add(B("Tüm kategorilere kopyala",CopyTemplateToAll));templateActions.Children.Add(B("Seçilenlere kopyala",CopyTemplateToSelected));templateActions.Children.Add(B("Ürünlerde önizle",Render));templateActions.Children.Add(B("Önizlemeyi Excel'e aktar",Export));template.Children.Add(templateActions);
  template.Children.Add(T("Şablonlar kanala özeldir; ana ürün başlığını/açıklamasını değiştirmez. XML adı/açıklaması kilitliyse özgün içerik korunur."));template.Children.Add(preview);
  tabs.Items.Add(new TabItem{Header="İçerik / özellik şablonu",Content=new ScrollViewer{Content=template,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
  if(kind==TaxonomyKind.Category)tabs.Items.Add(new TabItem{Header="Kategori Excel",Content=new ExcelWorkspacePanel(catalog,directory,"categories",()=>{Reload();changed();})});
  SetRow(status,2);status.Margin=new(4,8,4,0);Children.Add(status);
  list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is TaxonomyListRow row)Edit(row.Entry);};
  list.MouseDoubleClick+=(_,_)=>{if(list.IsReadOnly && list.SelectedItem!=null)tabs.SelectedIndex=1;};
  channel.SelectionChanged+=(_,_)=>ScopeChanged();channel.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>ScopeChanged()));shop.TextChanged+=(_,_)=>ScopeChanged();
  foreach(var box in new[]{title,description,brand,attributes,required})box.TextChanged+=(_,_)=>InvalidatePreview();
  Loaded+=(_,_)=>Reload();Reload();
 }
 static TextBlock T(string text)=>new(){Text=text,Margin=new(3,6,3,4),TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkSlateGray};
 Button B(string text,Action action){var b=new Button{Content=text,Margin=new(3)};b.Click+=(_,_)=>{try{action();}catch(Exception ex){status.Text=ex.Message;}};return b;}
 void InvalidatePreview(){rendered=Array.Empty<TaxonomyContentPreview>();preview.ItemsSource=null;}
 void Reload()
 {
  var keep=selected?.Id;products=catalog.Products();store.EnsureCatalogEntries(products);entries=store.List(kind);ReloadRows(keep);
  status.Text=$"{entries.Count} kayıt · {products.Count} ürün. Çoklu seçim: Ctrl / Shift; filtredeki tüm kayıtlar için Tümünü seç.";

 }
 void Edit(TaxonomyEntry? entry)
 {
  selected=entry;name.Text=entry==null?"":kind==TaxonomyKind.Category?entry.Name.Split(" > ").Last():entry.Name;active.IsChecked=entry?.Active??true;
  var choices=new[]{new TaxonomyEntry{Id="",Name="(Ana kategori)"}}.Concat(entries.Where(e=>entry==null||!TaxonomyStore.InCategory(e.Name,entry.Name))).ToList();parent.ItemsSource=choices;
  var split=entry?.Name.LastIndexOf(" > ",StringComparison.Ordinal)??-1;parent.SelectedItem=split>0?choices.FirstOrDefault(e=>e.Name==entry!.Name[..split]):choices[0];
  external.Clear();LoadMappings();LoadTemplate();
 }
 void SaveEntry()
 {
  var label=name.Text.Trim();if(label.Contains('>'))throw new InvalidOperationException("Üst kategoriyi listeden seçin; ad içinde > kullanmayın.");
  var full=kind==TaxonomyKind.Category&&parent.SelectedItem is TaxonomyEntry up&&up.Id.Length>0?up.Name+" > "+label:label;
  var item=new TaxonomyEntry{Id=selected?.Id??Guid.NewGuid().ToString("N"),Kind=kind,Name=full,Active=active.IsChecked==true,UpdatedUtc=selected?.UpdatedUtc??DateTime.UtcNow};
  selected=store.SaveWorkspaceEntry(item,selected!=null);changed();Reload();status.Text="Kayıt ve ilişkili ürün adları güncellendi.";
 }
 void DeleteEntry()
 {
  if(selected==null)throw new InvalidOperationException("Bir kayıt seçin.");
  if(MessageBox.Show(Window.GetWindow(this),selected.Name+" silinsin mi? Kullanımdaki kayıtlar silinmez.","Kaydı sil",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
  store.Delete(kind,selected.Id);Edit(null);Reload();changed();
 }
 void ScopeChanged(){mappings.SelectedItem=null;external.Clear();LoadTemplate();}
 void LoadMappings(){mappings.ItemsSource=selected==null?null:store.MappingViews(kind,Channel,Shop).Where(m=>m.LocalId==selected.Id).ToList();}
 void Map(){if(selected==null)throw new InvalidOperationException("Bir kayıt seçin.");store.Map(kind,external.Text,selected.Id,Channel,Shop,store.GetMappingVersion(kind,external.Text,Channel,Shop));LoadMappings();status.Text="Pazaryeri karşılığı kaydedildi.";}
 void Unmap(){if(mappings.SelectedItem is not TaxonomyMappingView m)throw new InvalidOperationException("Eşleme seçin.");store.Unmap(kind,m.ExternalKey,m.Marketplace,m.ShopId);LoadMappings();}
 void LoadTemplate()
 {
  InvalidatePreview();var t=selected==null?null:templates.Get(selected.Id,Channel,Shop);version=t?.Version??0;title.Text=t?.NamePattern??"{Name}";description.Text=t?.DescriptionPattern??"{Description}";brand.Text=t?.BrandName??"";attributes.Text=t==null?"":string.Join(Environment.NewLine,t.Attributes.Select(a=>a.Key+"="+a.Value));required.Text=t==null?"":string.Join(", ",t.RequiredAttributes);LoadMappings();
 }
 TaxonomyContentTemplate Capture()
 {
  if(selected==null)throw new InvalidOperationException("Bir kayıt seçin.");var values=new Dictionary<string,string>();foreach(var line in attributes.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){var i=line.IndexOf('=');if(i<=0)throw new InvalidOperationException("Özellik biçimi: Ad=Değer");if(!values.TryAdd(line[..i].Trim(),line[(i+1)..].Trim()))throw new InvalidOperationException("Özellik yineleniyor.");}
  return new(){EntryId=selected.Id,Channel=Channel,ShopId=Shop,NamePattern=title.Text,DescriptionPattern=description.Text,BrandName=brand.Text.Trim(),Attributes=values,RequiredAttributes=required.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList(),Version=version};
 }
 void CopyTemplateToAll()
 {
  var original=Capture();templates.Preview(original,new CatalogProduct());
  if(MessageBox.Show(Window.GetWindow(this),$"{entries.Count} kategorinin {Channel}/{Shop} içerik şablonu bu ayarlarla değişsin mi?","Şablonu kopyala",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
  var copies=new List<TaxonomyContentTemplate>();foreach(var entry in entries){var t=System.Text.Json.JsonSerializer.Deserialize<TaxonomyContentTemplate>(System.Text.Json.JsonSerializer.Serialize(original))!;t.EntryId=entry.Id;t.Version=templates.Get(entry.Id,Channel,Shop)?.Version??0;copies.Add(t);}templates.SaveBatch(copies);
  LoadTemplate();status.Text="Şablon tüm kategorilere kopyalandı.";
 }
 void Render(){var t=Capture();rendered=catalog.Products().Where(p=>kind==TaxonomyKind.Category?TaxonomyStore.SameCategory(p.Category,selected!.Name):p.Brand.Equals(selected!.Name,StringComparison.OrdinalIgnoreCase)).Select(p=>templates.Preview(t,p)).ToList();preview.ItemsSource=rendered;status.Text=$"{rendered.Count} ürünün kanal içeriği önizlendi. Ana ürün değişmedi.";}
 void Export(){if(rendered.Count==0)throw new InvalidOperationException("Önce ürünlerde önizleyin.");var save=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName="kanal-icerik.xlsx"};if(save.ShowDialog(Window.GetWindow(this))!=true)return;using var book=new XLWorkbook();var sheet=book.AddWorksheet("İçerik");var heads=new[]{"SKU","Başlık","Açıklama","Marka","Özellikler"};for(var i=0;i<heads.Length;i++)sheet.Cell(1,i+1).Value=heads[i];var n=2;foreach(var row in rendered){sheet.Cell(n,1).Value=row.Sku;sheet.Cell(n,2).Value=row.Name;sheet.Cell(n,3).Value=row.Description;sheet.Cell(n,4).Value=row.Brand;sheet.Cell(n,5).Value=string.Join("; ",row.Attributes.Select(a=>a.Key+"="+a.Value));n++;}book.SaveAs(save.FileName);}
}
