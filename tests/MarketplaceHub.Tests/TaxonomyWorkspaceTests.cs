using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;
namespace MarketplaceHub.Tests;
[TestClass] public class TaxonomyWorkspaceTests
{
 string dir=null!;
 [TestInitialize]public void Setup(){dir=Path.Combine(Path.GetTempPath(),"taxonomy-workspace-"+Guid.NewGuid().ToString("N"));new CatalogStore(dir);}
 [TestCleanup]public void Cleanup(){SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
 [TestMethod]public void CategoryRenameMovesChildrenAndProductsAtomically()
 {
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="ONE",Name="One",Category="Clothes > Men > Shirts",Brand="Brand",Currency="TRY"});
  var store=new TaxonomyStore(dir);store.EnsureCatalogEntries(catalog.Products());
  var parent=store.List(TaxonomyKind.Category).Single(e=>e.Name=="Clothes > Men");
  store.SaveWorkspaceEntry(new(){Id=parent.Id,Kind=parent.Kind,Name="Clothes > Women",UpdatedUtc=parent.UpdatedUtc});
  Assert.AreEqual("Clothes > Women > Shirts",catalog.Products().Single().Category);
  Assert.IsTrue(store.List(TaxonomyKind.Category).Any(e=>e.Name=="Clothes > Women > Shirts"));
  Assert.ThrowsException<InvalidOperationException>(()=>store.Delete(TaxonomyKind.Category,parent.Id));
  Assert.ThrowsException<InvalidOperationException>(()=>store.SaveWorkspaceEntry(new(){Id=parent.Id,Kind=parent.Kind,Name="Other",UpdatedUtc=parent.UpdatedUtc}));
 }
 [TestMethod]public void BrandRenamePreservesIdAndUpdatesProducts()
 {
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="ONE",Name="One",Brand="Old",Currency="TRY"});
  var store=new TaxonomyStore(dir);store.EnsureCatalogEntries(catalog.Products());var old=store.List(TaxonomyKind.Brand).Single();
  store.Map(TaxonomyKind.Brand,"external",old.Id,"trendyol","shop");
  old.Name="New";store.SaveWorkspaceEntry(old);Assert.AreEqual("New",catalog.Products().Single().Brand);Assert.AreEqual(old.Id,store.Resolve(TaxonomyKind.Brand,"external","trendyol","shop"));
 }
 [TestMethod]public void ChannelTemplatesKeepProductsAndOtherChannelsUnchanged()
 {
  var catalog=new CatalogStore(dir);var p=catalog.CreateManual(new(){Sku="ONE",Name="Shirt",Description="Cotton",Category="Clothes",Currency="TRY"});
  var taxonomy=new TaxonomyStore(dir);taxonomy.EnsureCatalogEntries(catalog.Products());var entry=taxonomy.List(TaxonomyKind.Category).Single();
  var templates=new TaxonomyContentStore(dir);var t=new TaxonomyContentTemplate{EntryId=entry.Id,Channel="trendyol",ShopId="shop",NamePattern="Sport {Name}",DescriptionPattern="{Description} — Free shipping",RequiredAttributes=new(){"Color"}};
  Assert.ThrowsException<InvalidOperationException>(()=>templates.Save(t));t.Attributes["Color"]="Mixed";templates.Save(t);
  var preview=templates.Preview(t,p);Assert.AreEqual("Sport Shirt",preview.Name);Assert.AreEqual("Cotton — Free shipping",preview.Description);Assert.AreEqual("Shirt",catalog.Products().Single().Name);Assert.IsNull(templates.Get(entry.Id,"n11","shop"));
  p.XmlAttributes["Description2"]="Extra";t.DescriptionPattern="{Description2} — Free shipping";Assert.AreEqual("Extra — Free shipping",templates.Preview(t,p).Description);p.LockName=true;p.LockDescription=true;preview=templates.Preview(t,p);Assert.AreEqual("Shirt",preview.Name);Assert.AreEqual("Cotton",preview.Description);
 }

 [TestMethod]public void CategoryIdentityIgnoresSeparatorWhitespace()
 {
  Assert.IsTrue(TaxonomyStore.InCategory("Clothes> Men >Shirts","Clothes > Men"));Assert.IsTrue(TaxonomyStore.SameCategory("Clothes>Men","Clothes > Men"));
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="SPACE",Name="Space",Category="Clothes>Men>Shirts",Currency="TRY"});var taxonomy=new TaxonomyStore(dir);taxonomy.EnsureCatalogEntries(catalog.Products());
  var leaf=taxonomy.List(TaxonomyKind.Category).Single(e=>e.Name=="Clothes > Men > Shirts");Assert.AreEqual(1,taxonomy.Usage(TaxonomyKind.Category,leaf).Products);
  var parent=taxonomy.List(TaxonomyKind.Category).Single(e=>e.Name=="Clothes > Men");parent.Name="Clothes > Women";taxonomy.SaveWorkspaceEntry(parent);Assert.AreEqual("Clothes > Women > Shirts",catalog.Products().Single().Category);
 }

 [TestMethod]public void BulkAttachMovesSelectedRootsAndChildrenOnlyOnce()
 {
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="A",Name="A",Category="Clothes > Men > Shirts",Currency="TRY"});
  var store=new TaxonomyStore(dir);store.EnsureCatalogEntries(catalog.Products());var target=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="Store"});
  var selection=store.List(TaxonomyKind.Category).Where(e=>e.Name.StartsWith("Clothes")).ToList();
  store.ApplyWorkspaceBatch(TaxonomyKind.Category,selection,TaxonomyBatchAction.Attach,target.Id);
  Assert.AreEqual("Store > Clothes > Men > Shirts",catalog.Products().Single().Category);
  Assert.AreEqual(4,store.List(TaxonomyKind.Category).Count);
 }
 [TestMethod]public void BulkDeleteRollsBackWhenAnySelectedRecordIsUsed()
 {
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="A",Name="A",Brand="Used",Currency="TRY"});
  var store=new TaxonomyStore(dir);store.EnsureCatalogEntries(catalog.Products());var free=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Brand,Name="Free"});
  Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyWorkspaceBatch(TaxonomyKind.Brand,store.List(TaxonomyKind.Brand),TaxonomyBatchAction.Delete));
  Assert.AreEqual(2,store.List(TaxonomyKind.Brand).Count);
  store.ApplyWorkspaceBatch(TaxonomyKind.Brand,store.List(TaxonomyKind.Brand),TaxonomyBatchAction.Deactivate);
  Assert.IsTrue(store.List(TaxonomyKind.Brand).All(e=>!e.Active));Assert.AreEqual("Used",catalog.Products().Single().Brand);
 }
 [TestMethod]public void BulkAttachRejectsCyclesConflictsAndStaleSelections()
 {
  var store=new TaxonomyStore(dir);store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="A > Child"});store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="B > Child"});
  var rows=store.List(TaxonomyKind.Category);var a=rows.Single(e=>e.Name=="A");var child=rows.Single(e=>e.Name=="A > Child");var b=rows.Single(e=>e.Name=="B");
  Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyWorkspaceBatch(TaxonomyKind.Category,new[]{a},TaxonomyBatchAction.Attach,child.Id));
  Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyWorkspaceBatch(TaxonomyKind.Category,new[]{child},TaxonomyBatchAction.Attach,b.Id));
  store.ApplyWorkspaceBatch(TaxonomyKind.Category,new[]{a},TaxonomyBatchAction.Deactivate);
  Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyWorkspaceBatch(TaxonomyKind.Category,new[]{a,b},TaxonomyBatchAction.Deactivate));
  Assert.IsTrue(store.List(TaxonomyKind.Category).Single(e=>e.Id==b.Id).Active);
 }
 [TestMethod]public void BulkDeleteCanRemoveAnEntireUnusedSelectedTree()
 {
  var store=new TaxonomyStore(dir);store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="A > Child"});
  store.ApplyWorkspaceBatch(TaxonomyKind.Category,store.List(TaxonomyKind.Category),TaxonomyBatchAction.Delete);Assert.AreEqual(0,store.List(TaxonomyKind.Category).Count);
 }

 [TestMethod]public void QuickMappingEditRejectsStealingAndStaleMappingsAtomically()
 {
  var store=new TaxonomyStore(dir);var one=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Brand,Name="One"});var two=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Brand,Name="Two"});store.Map(TaxonomyKind.Brand,"100",one.Id,"Trendyol");store.Map(TaxonomyKind.Brand,"200",two.Id,"Trendyol");
  var expected=store.Mappings(TaxonomyKind.Brand).Where(m=>m.LocalId==one.Id).ToList();
  var edit=new TaxonomyWorkspaceEdit(one,new[]{new TaxonomyWorkspaceMappingEdit("Trendyol","default","200",expected)});
  one.Name="Changed";Assert.ThrowsException<InvalidOperationException>(()=>store.SaveWorkspaceEdits(new[]{edit}));Assert.AreEqual("One",store.List(TaxonomyKind.Brand).Single(e=>e.Id==one.Id).Name);
  store.Map(TaxonomyKind.Brand,"100",one.Id,"Trendyol");
  Assert.ThrowsException<InvalidOperationException>(()=>store.SaveWorkspaceEdits(new[]{new TaxonomyWorkspaceEdit(one,new[]{new TaxonomyWorkspaceMappingEdit("Trendyol","default","300",expected)})}));
  expected=store.Mappings(TaxonomyKind.Brand).Where(m=>m.LocalId==one.Id).ToList();
  store.SaveWorkspaceEdits(new[]{new TaxonomyWorkspaceEdit(one,new[]{new TaxonomyWorkspaceMappingEdit("Trendyol","default","300",expected)})});
  Assert.IsNull(store.Resolve(TaxonomyKind.Brand,"100","Trendyol"));Assert.AreEqual(one.Id,store.Resolve(TaxonomyKind.Brand,"300","Trendyol"));Assert.AreEqual(two.Id,store.Resolve(TaxonomyKind.Brand,"200","Trendyol"));
 }

 [TestMethod]public void BulkTemplateSaveRollsBackEveryTemplateOnStaleRow()
 {
  var store=new TaxonomyStore(dir);var one=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="One"});var two=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Category,Name="Two"});var templates=new TaxonomyContentStore(dir);
  var first=new TaxonomyContentTemplate{EntryId=one.Id,Channel="Trendyol",NamePattern="First {Name}"};var second=new TaxonomyContentTemplate{EntryId=two.Id,Channel="Trendyol",Version=9};
  Assert.ThrowsException<InvalidOperationException>(()=>templates.SaveBatch(new[]{first,second}));Assert.IsNull(templates.Get(one.Id,"Trendyol","default"));Assert.AreEqual(0,first.Version);
  second.Version=0;templates.SaveBatch(new[]{first,second});Assert.AreEqual("First {Name}",templates.Get(one.Id,"Trendyol","default")!.NamePattern);Assert.IsNotNull(templates.Get(two.Id,"Trendyol","default"));
 }

 [TestMethod]public void QuickEditCannotCollapseMultipleExternalKeysIntoOneCell()
 {
  var store=new TaxonomyStore(dir);var entry=store.SaveWorkspaceEntry(new(){Kind=TaxonomyKind.Brand,Name="Brand"});store.Map(TaxonomyKind.Brand,"100",entry.Id,"Trendyol");store.Map(TaxonomyKind.Brand,"200",entry.Id,"Trendyol");var expected=store.Mappings(TaxonomyKind.Brand);
  Assert.ThrowsException<InvalidOperationException>(()=>store.SaveWorkspaceEdits(new[]{new TaxonomyWorkspaceEdit(entry,new[]{new TaxonomyWorkspaceMappingEdit("Trendyol","default","300, 200",expected)})}));Assert.AreEqual(2,store.Mappings(TaxonomyKind.Brand).Count);Assert.AreEqual(entry.Id,store.Resolve(TaxonomyKind.Brand,"100","Trendyol"));
 }

 [TestMethod]public void QuickEditRenamesParentAndChildInOneBatch()
 {
  var catalog=new CatalogStore(dir);catalog.CreateManual(new(){Sku="A",Name="A",Category="Parent > Child",Currency="TRY"});var store=new TaxonomyStore(dir);store.EnsureCatalogEntries(catalog.Products());var rows=store.List(TaxonomyKind.Category);var parent=rows.Single(e=>e.Name=="Parent");var child=rows.Single(e=>e.Name=="Parent > Child");parent.Name="Renamed";child.Name="Parent > New child";child.Active=false;
  store.SaveWorkspaceEdits(new[]{new TaxonomyWorkspaceEdit(parent,Array.Empty<TaxonomyWorkspaceMappingEdit>()),new TaxonomyWorkspaceEdit(child,Array.Empty<TaxonomyWorkspaceMappingEdit>())});
  Assert.AreEqual("Renamed > New child",catalog.Products().Single().Category);Assert.IsFalse(store.List(TaxonomyKind.Category).Single(e=>e.Id==child.Id).Active);
 }
}
