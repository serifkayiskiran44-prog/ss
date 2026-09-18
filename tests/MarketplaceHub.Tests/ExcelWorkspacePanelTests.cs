using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
namespace MarketplaceHub.Tests;
[TestClass]
public sealed class ExcelWorkspacePanelTests
{
    [TestMethod]public void ApplyStartsDisabledAndAllThreeWorkflowsExposeMappingAndPreview()
    {
        Exception failure=null;var thread=new Thread(()=>{var dir=Path.Combine(Path.GetTempPath(),"excel-ui-"+Guid.NewGuid().ToString("N"));try{
            foreach(var kind in new[]{"products","categories","orders"}){var panel=new ExcelWorkspacePanel(new CatalogStore(dir),dir,kind,()=>{});var apply=Walk(panel).OfType<Button>().Single(b=>b.Name=="ApplyExcel");Assert.IsFalse(apply.IsEnabled);Assert.IsFalse(Walk(panel).OfType<CheckBox>().Any(c=>c.ToolTip as string=="Bu alanı işle" || c.ToolTip as string=="Kimlik eşleşmesi ve tutarlılık kontrolü"));Assert.IsTrue(Walk(panel).OfType<Button>().Any(b=>b.Name=="PreviewExcel"));Assert.IsTrue(Walk(panel).OfType<ComboBox>().Any(b=>b.Name.StartsWith("Map_")));}
        }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    [TestMethod]public void LegacyProfileSelectedAfterWorkbookResolvesLettersAndOrdersReloadOnNavigation()
    {
        Exception failure=null;var thread=new Thread(()=>{var dir=Path.Combine(Path.GetTempPath(),"excel-ui-"+Guid.NewGuid().ToString("N"));try{
            Directory.CreateDirectory(dir);var path=Path.Combine(dir,"book.xlsx");using(var book=new ClosedXML.Excel.XLWorkbook()){var sheet=book.AddWorksheet("Data");sheet.Cell(1,1).Value="CustomCode";sheet.Cell(1,2).Value="CustomQuantity";sheet.Cell(2,1).Value="001";sheet.Cell(2,2).Value=4;book.SaveAs(path);}
            var profile=new ExcelImportProfile{Name="Legacy",SelectedFields=new(){"Sku"},ColumnMappings=new(){{"Sku","CustomCode"},{"Stock","CustomQuantity"}}};new ExcelProfileStore(dir).Save(profile);
            var panel=new ExcelWorkspacePanel(new CatalogStore(dir),dir,"products",()=>{});panel.LoadWorkbook(path);
            var selector=Walk(panel).OfType<ComboBox>().Single(c=>c.DisplayMemberPath=="Name");selector.SelectedIndex=0;
            var mapping=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="Map_Sku");Assert.AreEqual("A",((ExcelSheetColumn)mapping.SelectedItem).Letter);
            Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Profili kaydet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved=new ExcelProfileStore(dir).List().Single();Assert.IsNull(saved.SelectedFields);Assert.AreEqual("B",saved.ColumnLetters["Stock"]);
            var ordersPanel=OrdersPanel.Create(dir);new OrdersStore(dir).SaveManual(new OrderSnapshot{Marketplace="manual",ShopId="test",OrderId="NEW",Currency="TRY"});
            ordersPanel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));Assert.AreEqual(1,Walk(ordersPanel).OfType<DataGrid>().First().Items.Count);
        }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }

    [TestMethod]public void TaxonomyChannelChangeLoadsOnlyCurrentScopeMappings()
    {
        Exception failure=null;var thread=new Thread(()=>{var dir=Path.Combine(Path.GetTempPath(),"taxonomy-ui-"+Guid.NewGuid().ToString("N"));try{
            var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="A",Name="A",Brand="Brand",Currency="TRY"});var taxonomy=new TaxonomyStore(dir);taxonomy.EnsureCatalogEntries(catalog.Products());var entry=taxonomy.List(TaxonomyKind.Brand).Single();taxonomy.Map(TaxonomyKind.Brand,"TREND",entry.Id,"Trendyol","default");taxonomy.Map(TaxonomyKind.Brand,"N11KEY",entry.Id,"N11","default");
            var panel=new TaxonomyWorkspacePanel(dir,TaxonomyKind.Brand,()=>{});var list=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TaxonomyList");list.SelectedIndex=0;Assert.AreEqual(DataGridSelectionMode.Extended,list.SelectionMode);Assert.IsTrue(Walk(panel).OfType<Button>().Any(b=>b.Content as string=="Seçilenleri sil"));Assert.IsTrue(list.Columns.Any(c=>c.Header as string=="Trendyol"));
            var grid=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TaxonomyMappings");Assert.AreEqual("TREND",((TaxonomyMappingView)grid.Items[0]).ExternalKey);grid.SelectedIndex=0;
            var channel=Walk(panel).OfType<ComboBox>().Single(c=>c.Name=="TaxonomyChannel");channel.SelectedItem="N11";
            Assert.IsNull(grid.SelectedItem);Assert.AreEqual("N11KEY",((TaxonomyMappingView)grid.Items[0]).ExternalKey);
        }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }

    [TestMethod]public void CategoryListSupportsMultipleSelectionAndShowsCanonicalProductCounts()
    {
        Exception failure=null;var thread=new Thread(()=>{var dir=Path.Combine(Path.GetTempPath(),"taxonomy-list-"+Guid.NewGuid().ToString("N"));try{
            var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="A",Name="A",Category="Parent>Child",Brand="Brand",Currency="TRY"});
            var panel=new TaxonomyWorkspacePanel(dir,TaxonomyKind.Category,()=>{});var list=Walk(panel).OfType<DataGrid>().Single(g=>g.Name=="TaxonomyList");Assert.AreEqual(2,list.Items.Count);list.SelectAll();Assert.AreEqual(2,list.SelectedItems.Count);
            Assert.IsTrue(list.Items.Cast<TaxonomyListRow>().All(r=>r.Products==1));
            Walk(panel).OfType<Button>().Single(b=>b.Content as string=="Seçilendeki ürün sayısı").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(Walk(panel).OfType<TextBlock>().Any(t=>t.Text.Contains("1 farklı ürün")));
            Assert.IsTrue(Walk(panel).OfType<Button>().Any(b=>b.Content as string=="Seçilenleri kategoriye bağla"));
        }catch(Exception ex){failure=ex;}finally{SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
    static System.Collections.Generic.IEnumerable<DependencyObject> Walk(DependencyObject node){yield return node;foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())foreach(var descendant in Walk(child))yield return descendant;}
}
