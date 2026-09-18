using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;
[TestClass]
public class TrendyolPanelTests
{
    [TestMethod] public void EnteredBarcodeRoutesExistingToMatchAndMissingToCreatePreview()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-barcode-route-"+Guid.NewGuid().ToString("N"));
            try {
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","key","secret","123 - Self Integration"));
                var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir);catalog.CreateManual(new(){Sku="SKU-REMOTE",Gtin="REMOTE",Name="Blank barcode",Currency="TRY"});catalog.CreateManual(new(){Sku="LOCAL-NEW",Name="New product",Currency="TRY"});
                var store=new TrendyolWorkspaceStore(dir);var state=store.Load("123");state.Products.Add(new("REMOTE","SKU-REMOTE","Existing remote",1,1,10,10,true));state.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(state);
                var panel=new TrendyolWorkspacePanel(dir);var products=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");products.SelectAll();
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolMatchProducts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var review=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolMatchReview");var save=Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSaveMatches");Assert.IsFalse(save.IsEnabled,"Blank barcode must not fall back to SKU or GTIN.");
                var barcode=Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolMatchBarcode");var check=Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolCheckBarcode");
                review.SelectedItem=review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="SKU-REMOTE");barcode.Text="REMOTE";check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("Hazır",review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="SKU-REMOTE").Status);
                review.SelectedItem=review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="LOCAL-NEW");barcode.Text="USER-NEW";Assert.IsFalse(save.IsEnabled,"An unchecked barcode draft must prevent saving the previous selection.");
                review.SelectedItem=review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="SKU-REMOTE");Assert.IsFalse(save.IsEnabled,"Changing rows must not allow saving an older barcode while another draft is unchecked.");
                review.SelectedItem=review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="LOCAL-NEW");Assert.AreEqual("USER-NEW",barcode.Text,"Unchecked barcode must survive row changes.");check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("Yeni açılacak",review.Items.Cast<TrendyolMatchReviewRow>().Single(r=>r.Sku=="LOCAL-NEW").Status);Assert.AreEqual(0,store.Load("123").Profiles.Count);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(2,store.Load("123").Profiles.Count);Assert.AreEqual(0,store.Receipts("123").Count);
                var preview=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolPreview");Assert.AreEqual(1,preview.Items.Count);Assert.AreEqual("USER-NEW",((TrendyolPreviewRow)preview.Items[0]).Barcode);
                Assert.AreEqual("Hatalı",((TrendyolPreviewRow)preview.Items[0]).Status);Assert.IsFalse(Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSend").IsEnabled);Assert.IsTrue(catalog.Products().All(p=>p.Barcode==""));
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void ExpandedControlsKeepAtLeastEightProductRowsAtDesktopPanelSize()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-layout-"+Guid.NewGuid().ToString("N"));
            try {
                var panel=new TrendyolWorkspacePanel(dir);Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolToggleBulk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                panel.Measure(new Size(1140,600));panel.Arrange(new Rect(0,0,1140,600));panel.UpdateLayout();
                var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");Assert.IsTrue(grid.ActualHeight>=29+8*27,$"Only {grid.ActualHeight} px remain for the product list.");
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolToggleFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.UpdateLayout();
                Assert.IsTrue(grid.ActualHeight>=29+8*27,"Opening both action panels must preserve the product list.");
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void MatchingReviewShowsEveryRowAndManualChoiceCannotDuplicateBarcode()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-matching-ui-"+Guid.NewGuid().ToString("N"));
            try {
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","test-key","test-secret","123 - Self Integration"));var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir);
                for(var i=0;i<20;i++)catalog.CreateManual(new(){Sku="LOCAL-"+i,Name="Cup "+i,Currency="TRY"});
                var store=new TrendyolWorkspaceStore(dir);var state=store.Load("123");state.Products.Add(new("REMOTE","OTHER","Remote cup",1,2,10m,10m,true));state.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(state);
                var panel=new TrendyolWorkspacePanel(dir);var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");grid.SelectAll();
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolMatchProducts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var review=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolMatchReview");Assert.AreEqual(20,review.Items.Count,"The old message box truncated to 15 rows.");
                var save=Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSaveMatches");Assert.IsFalse(save.IsEnabled,"No matches must not offer to save zero rows.");
                var search=Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolMatchSearch");search.Text="REMOTE";Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSearchMatches").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var candidates=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolMatchCandidates");Assert.AreEqual(1,candidates.Items.Count);candidates.SelectedItem=candidates.Items[0];
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolChooseMatch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsTrue(save.IsEnabled);Assert.AreEqual(0,store.Load("123").Profiles.Count,"A manual choice is only a draft until explicitly saved.");
                review.SelectedItem=review.Items[1];search.Text="REMOTE";Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSearchMatches").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));candidates.SelectedItem=candidates.Items[0];
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolChooseMatch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsFalse(save.IsEnabled,"Duplicate targets must be resolved before saving.");
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSkipMatch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsTrue(save.IsEnabled);save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1,store.Load("123").Profiles.Count);Assert.AreEqual(20,grid.SelectedItems.Count,"Review completion should retain the product selection.");
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void DeliverySaveRetainsIdentityProductSelectionAndReadableChoices()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-delivery-"+Guid.NewGuid().ToString("N"));
            try {
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","test-key","test-secret","123 - Self Integration"));
                var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir);catalog.CreateManual(new(){Sku="CUP",Name="Cup",Currency="TRY"});
                var workspace=new TrendyolWorkspaceStore(dir);var cache=workspace.Load("123");cache.Carriers.Add(new("TEST","Test Kargo"));cache.Addresses.Add(new(10,"Ana depo",true,true));workspace.Save(cache);
                var panel=new TrendyolWorkspacePanel(dir);var products=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");products.SelectedItem=products.Items[0];
                var name=Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolTemplateName");name.Text="Standart";
                var save=Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Şablonu kaydet");save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var original=workspace.Load("123").Templates.Single();
                Assert.AreEqual(1,products.SelectedItems.Count,"Saving a template must retain the products waiting for assignment.");
                name.Text="Standart düzenlendi";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var updated=workspace.Load("123").Templates.Single();Assert.AreEqual(original.Id,updated.Id);Assert.AreEqual("Standart düzenlendi",updated.Name);
                var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolTemplates");Assert.IsNotNull(grid.SelectedItem,"Saved template must remain selected for editing.");
                var cargo=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolTemplateCarrier");cargo.SelectedIndex=1;
                var days=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolTemplateDuration");days.SelectedIndex=3;
                var address=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolTemplateShipment");address.SelectedIndex=1;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var row=grid.SelectedItem;Assert.AreEqual("Test Kargo",row.GetType().GetProperty("CarrierName").GetValue(row));Assert.AreEqual("Ana depo",row.GetType().GetProperty("ShipmentName").GetValue(row));
                Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Yeni şablon").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual("",name.Text);Assert.AreEqual(0,days.SelectedIndex);Assert.IsNull(grid.SelectedItem);
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void DeliveryKeepsMissingRemoteChoicesAndRejectsStaleSave()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-delivery-stale-"+Guid.NewGuid().ToString("N"));
            try {
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","test-key","test-secret","123 - Self Integration"));
                var workspace=new TrendyolWorkspaceStore(dir);var cache=workspace.Load("123");cache.Templates.Add(new("old","Existing","OLD",2,42,43));workspace.Save(cache);
                var panel=new TrendyolWorkspacePanel(dir);var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolTemplates");grid.SelectedItem=grid.Items[0];
                var cargo=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolTemplateCarrier");Assert.AreEqual("OLD",((TrendyolCarrier)cargo.SelectedItem).Code);
                var save=Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Şablonu kaydet");save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(42L,workspace.Load("123").Templates.Single().ShipmentAddressId);Assert.AreEqual(43L,workspace.Load("123").Templates.Single().ReturningAddressId);
                cache=workspace.Load("123");cache.Templates[0]=cache.Templates[0] with{Name="Changed elsewhere"};workspace.Save(cache);
                var name=Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolTemplateName");name.Text="Unsaved draft";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("Changed elsewhere",workspace.Load("123").Templates.Single().Name);Assert.AreEqual("Unsaved draft",name.Text);
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void WorkspaceStartsOfflineWithWorkflowTabsAndSendDisabled()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-ui-"+Guid.NewGuid().ToString("N"));
            try {
                var panel=new TrendyolWorkspacePanel(dir);
                var workspaceTabs=Walk(panel).OfType<TabControl>().Single(t=>t.Name=="TrendyolSections");
                CollectionAssert.AreEqual(new[]{"Trendyol kontrol","Ayarlar","Rekabet analizi","İşlem geçmişi"},workspaceTabs.Items.Cast<TabItem>().Select(t=>t.Header.ToString()).ToArray());
                Assert.AreEqual(0,workspaceTabs.SelectedIndex);
                Assert.AreEqual(Visibility.Collapsed,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolDetailedFilters").Visibility);
                Assert.AreEqual(Visibility.Collapsed,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolBulkActions").Visibility);
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolToggleFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(Visibility.Visible,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolDetailedFilters").Visibility);
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolToggleBulk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(Visibility.Visible,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolBulkActions").Visibility);
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolOpenSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1,workspaceTabs.SelectedIndex);
                Assert.IsFalse(Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSend").IsEnabled);
                Assert.AreEqual(DataGridSelectionMode.Extended,Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts").SelectionMode);
                Assert.AreEqual(0,Walk(panel).OfType<CheckBox>().Count(),"Alan seçimi tikleri yerine alan ve işlem modu kullanılır.");
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","test-key","test-secret","123 - Self Integration"));
                var taxonomy=new TrMarketplaceHubDesktop.Catalog.TaxonomyStore(dir);taxonomy.Save(new(){Kind=TrMarketplaceHubDesktop.Catalog.TaxonomyKind.Category,Name="Ev > Fincan"});
                var workspace=new TrendyolWorkspaceStore(dir);var cache=workspace.Load("123");cache.Categories.Add(new(42,"Fincan","Ev > Fincan",true));workspace.Save(cache);
                panel=new TrendyolWorkspacePanel(dir);
                Walk(panel).OfType<Button>().Single(b=>b.Content as string=="2. Otomatik eşleştirme öner").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var mapping=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolMappings");
                Assert.AreEqual(42L,mapping.Items.Cast<TrendyolMappingRow>().Single().RemoteId,"Category suggestions must work when no brands exist.");
                var save=Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Şablonu kaydet");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolTemplateName").Text="Düzeltilmiş şablon";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("Düzeltilmiş şablon",new TrendyolWorkspaceStore(dir).Load("123").Templates.Single().Name,"Invalid save must not poison the next edit.");
                new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir).CreateManual(new(){Sku="CUP",Name="Cup",Category="Ev>Fincan",Currency="TRY"});
                cache=workspace.Load("123");var localCategory=taxonomy.List(TrMarketplaceHubDesktop.Catalog.TaxonomyKind.Category).Single(e=>e.Name=="Ev > Fincan");
                cache.Mappings.Add(new(TrMarketplaceHubDesktop.Catalog.TaxonomyKind.Category,localCategory.Id,localCategory.Name,42));cache.Attributes[42]=[new(7,"Renk",true,true,false,[])];cache.AttributesUpdatedUtc[42]=DateTime.UtcNow;workspace.Save(cache);
                panel=new TrendyolWorkspacePanel(dir);var productGrid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");productGrid.SelectedItem=productGrid.Items[0];
                Assert.IsTrue(Walk(panel).OfType<TextBlock>().Any(t=>t.Text=="* Renk"),"Mapped category attributes must load when XML category separators omit spaces.");
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod] public void ControlFiltersWholeCatalogAndSelectionInvalidatesPreview()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-control-"+Guid.NewGuid().ToString("N"));
            try {
                new TrendyolSettingsStore(Path.Combine(dir,"trendyol.bin")).Save(new("123","test-key","test-secret","123 - Self Integration"));
                var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir);
                catalog.CreateManual(new(){Sku="LOCAL",Name="Local only",Currency="TRY",Brand="Local brand",Category="Ev"});
                catalog.CreateManual(new(){Sku="APPROVED",Name="Approved cup",Currency="TRY",Price=15,Stock=3,Brand="Cup brand",Category="Ev > Fincan"});
                var product=catalog.Products().Single(p=>p.Sku=="APPROVED");
                var workspace=new TrendyolWorkspaceStore(dir);var cache=workspace.Load("123");
                cache.Profiles.Add(new(){ProductId=product.Id,IntegrationCode="BARCODE"});
                cache.Products.Add(new("BARCODE","APPROVED","Approved cup",100,2,10,10,true));cache.ProductsUpdatedUtc=DateTime.UtcNow;workspace.Save(cache);
                var panel=new TrendyolWorkspacePanel(dir);var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");
                var filter=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolStatusFilter");
                filter.SelectedItem="Onaylı";
                Assert.AreEqual(1,grid.Items.Count);Assert.AreEqual("APPROVED",((TrendyolProductRow)grid.Items[0]).Sku);
                grid.SelectedItem=grid.Items[0];
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolBuildPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var send=Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolSend");Assert.IsTrue(send.IsEnabled,string.Join(" | ",Walk(panel).OfType<TextBlock>().Select(t=>t.Text)));
                filter.SelectedItem="Tümü";
                Assert.AreEqual(2,grid.Items.Count);Assert.IsFalse(send.IsEnabled,"Changing the filter/selection must invalidate the prior send plan.");
                Assert.AreEqual(0,Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolPreview").Items.Count);
                var brandFilter=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolBrandFilter");brandFilter.SelectedItem="Cup brand";
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolApplyFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1,grid.Items.Count);Assert.AreEqual("APPROVED",((TrendyolProductRow)grid.Items[0]).Sku);
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolResetFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(2,grid.Items.Count);
                Assert.AreEqual(Visibility.Collapsed,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolProductEditorView").Visibility);
                grid.SelectedItem=grid.Items.Cast<TrendyolProductRow>().Single(r=>r.Sku=="APPROVED");
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolOpenProduct").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(Visibility.Visible,Walk(panel).OfType<FrameworkElement>().Single(e=>e.Name=="TrendyolProductEditorView").Visibility);
                var integration=Walk(panel).OfType<TextBox>().Single(b=>b.Name=="TrendyolIntegrationCode");integration.Text="UNSAVED";
                Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Şablonu seçili ürünlere ata").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("UNSAVED",integration.Text,"Template assignment must preserve unsaved product edits.");
                Assert.AreEqual("BARCODE",workspace.Load("123").Profiles.Single().IntegrationCode);
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    static IEnumerable<DependencyObject> Walk(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Walk(child))yield return item;}
    [TestMethod] public void CategoryDescendantsAndBrandFilterApplyBeforePaging()
    {
        Exception failure=null;var thread=new Thread(()=>{
            var dir=Path.Combine(Path.GetTempPath(),"trendyol-filter-"+Guid.NewGuid().ToString("N"));
            try {
                var catalog=new TrMarketplaceHubDesktop.Catalog.CatalogStore(dir);
                using(var connection=new SqliteConnection("Data Source="+Path.Combine(dir,"catalog.db"))) {
                    connection.Open();using var transaction=connection.BeginTransaction();
                    for(var i=0;i<205;i++){var product=new TrMarketplaceHubDesktop.Catalog.CatalogProduct{Sku="SKU"+i,Name=i==204?"ZZZ Target":"A Product "+i,Brand=i==204?"Target brand":"Other",Category=i==204?"Ev>Fincan":"Bahçe",Currency="TRY"};using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO CatalogProducts(Id,Json) VALUES($id,$json)";command.Parameters.AddWithValue("$id",product.Id);command.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(product));command.ExecuteNonQuery();}
                    transaction.Commit();
                }
                var panel=new TrendyolWorkspacePanel(dir);var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TrendyolProducts");Assert.AreEqual(100,grid.Items.Count);
                var tree=Walk(panel).OfType<TreeView>().Single();tree.Items.Cast<TreeViewItem>().Single(n=>(string)n.Header=="Ev").IsSelected=true;
                Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolBrandFilter").SelectedItem="Target brand";
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolApplyFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1,grid.Items.Count);Assert.AreEqual("SKU204",((TrendyolProductRow)grid.Items[0]).Sku);
                var child=tree.Items.Cast<TreeViewItem>().Single(n=>(string)n.Header=="Ev").Items.Cast<TreeViewItem>().Single();child.IsSelected=true;
                Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Tüm kategoriler").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsFalse(child.IsSelected);child.IsSelected=true;
                Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TrendyolBrandFilter").SelectedItem="Other";
                Walk(panel).OfType<Button>().Single(b=>b.Name=="TrendyolApplyFilters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(0,grid.Items.Count,"Category and brand must combine with AND.");
            }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
}
