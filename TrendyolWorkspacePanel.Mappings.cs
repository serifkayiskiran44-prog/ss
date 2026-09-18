using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;
public sealed record TrendyolMappingRow(string LocalId,string LocalName,long? RemoteId,string RemoteName,string Reason);
public sealed partial class TrendyolWorkspacePanel
{
    readonly ComboBox mappingKind=new(){ItemsSource=new[]{"Kategori","Marka"},SelectedIndex=0,Width=120,Margin=new(3)};
    readonly TextBox mappingSearch=Box();
    readonly TextBox remoteSearch=Box();
    readonly ComboBox remoteTarget=Combo("Label");
    readonly DataGrid mappings=Grid("TrendyolMappings",("Yerel kategori / marka","LocalName",280),("Trendyol ID","RemoteId",110),("Trendyol karşılığı","RemoteName",320),("Durum","Reason",300));
    long mappingRevision;
    TaxonomyKind MappingKind=>mappingKind.SelectedIndex==0?TaxonomyKind.Category:TaxonomyKind.Brand;
    FrameworkElement BuildMappings()
    {
        var dock=new DockPanel();var top=new StackPanel();top.Children.Add(T("Kategori ve marka eşleştirmesi",false));top.Children.Add(T("Otomatik önerileri inceleyip uygulayın. Bulunamayan karşılığı Manuel eşleştirme bölümünden seçebilirsiniz.",true));
        var actions=new WrapPanel();actions.Children.Add(A("1. Kategori ve katalog markalarını al",RefreshDictionaries));
        actions.Children.Add(mappingKind);actions.Children.Add(mappingSearch);actions.Children.Add(B("Filtrele",RefreshMappings));actions.Children.Add(B("2. Otomatik eşleştirme öner",SuggestMappings));actions.Children.Add(B("3. Seçili önerileri uygula",ApplyMappings));actions.Children.Add(B("Tümünü seç",()=>mappings.SelectAll()));actions.Children.Add(B("Seçilinin eşleştirmesini kaldır",RemoveMappings));top.Children.Add(actions);
        var manual=new WrapPanel();manual.Children.Add(T("Trendyol listesinde ara"));manual.Children.Add(remoteSearch);manual.Children.Add(B("Listede ara",RefreshRemoteTargets));manual.Children.Add(A("Markayı API'de ara",SearchBrandApi));remoteTarget.Width=410;manual.Children.Add(remoteTarget);manual.Children.Add(B("Seçilenlere bu karşılığı öner",()=>{var selected=mappings.SelectedItems.Cast<TrendyolMappingRow>().Select(r=>r.LocalId).ToHashSet();if(selected.Count==0||remoteTarget.SelectedItem is not Target target)throw new InvalidOperationException("Yerel kayıt ve Trendyol karşılığı seçin.");mappings.ItemsSource=mappings.Items.Cast<TrendyolMappingRow>().Select(r=>selected.Contains(r.LocalId)?r with{RemoteId=target.Id,RemoteName=target.Label,Reason="Elle seçildi; uygulama bekliyor"}:r).ToList();foreach(var row in mappings.Items.Cast<TrendyolMappingRow>().Where(r=>selected.Contains(r.LocalId)))mappings.SelectedItems.Add(row);}));top.Children.Add(new Expander{Header="Manuel eşleştirme · bulunamayan kategori veya marka",Content=manual,Margin=new(5)});DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(mappings);
        mappingKind.SelectionChanged+=(_,_)=>{if(loaded)RefreshMappings();};return dock;
    }
    sealed record Target(long Id,string Label);
    async Task RefreshDictionaries()
    {
        var account=Account();var next=store.Load(account.SupplierId);using var client=new TrendyolApiClient(account);
        var categories=await client.GetCategoriesAsync(Token);
        var referenced=next.Mappings.Where(m=>m.Kind==TaxonomyKind.Brand).Select(m=>m.RemoteId).Concat(next.Profiles.Where(p=>p.BrandId.HasValue).Select(p=>p.BrandId!.Value)).ToHashSet();
        var names=taxonomy.List(TaxonomyKind.Brand).Where(e=>e.Active).Select(e=>e.Name).Concat(next.Brands.Where(b=>referenced.Contains(b.Id)).Select(b=>b.Name)).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
        var brands=new Dictionary<long,TrendyolBrand>();
        for(var i=0;i<names.Length;i++)
        {
            status.Text=$"Kategori listesi alındı. Katalog markaları aranıyor: {i+1}/{names.Length} · {names[i]}";
            foreach(var brand in await client.GetBrandsByNameAsync(names[i],Token))
            {if(brands.TryGetValue(brand.Id,out var old)&&old!=brand)throw new InvalidOperationException("Marka listesi istek sırasında değişti; yeniden alın.");brands[brand.Id]=brand;}
        }
        EnsureAccount(account);next.Categories=categories.ToList();next.Brands=brands.Values.ToList();next.DictionaryUpdatedUtc=DateTime.UtcNow;store.Save(next);Reload();
        status.Text=$"{categories.Count} kategori ve katalog markaları için {brands.Count} marka adayı alındı. Bulunamayan markayı adıyla API'de arayabilirsiniz.";
    }
    async Task SearchBrandApi()
    {
        var query=remoteSearch.Text.Trim();if(query.Length==0)throw new InvalidOperationException("Aranacak marka adını yazın.");
        var account=Account();var next=store.Load(account.SupplierId);using var client=new TrendyolApiClient(account);var matches=await client.GetBrandsByNameAsync(query,Token);EnsureAccount(account);
        var ids=matches.Select(b=>b.Id).ToHashSet();next.Brands.RemoveAll(b=>ids.Contains(b.Id));next.Brands.AddRange(matches);store.Save(next);mappingKind.SelectedIndex=1;Reload();
        remoteTarget.ItemsSource=matches.Select(b=>new Target(b.Id,$"{b.Name}  [{b.Id}]")).ToList();status.Text=$"{matches.Count} marka adayı bulundu. Yerel markayı ve doğru karşılığı seçip öneriyi uygulayın.";
    }
    void RefreshRemoteTargets(){var query=remoteSearch.Text.Trim();remoteTarget.ItemsSource=(MappingKind==TaxonomyKind.Category?state.Categories.Where(c=>c.IsLeaf).Select(c=>new Target(c.Id,$"{c.Path}  [{c.Id}]")):state.Brands.Select(b=>new Target(b.Id,$"{b.Name}  [{b.Id}]"))).Where(x=>query.Length==0||x.Label.Contains(query,StringComparison.CurrentCultureIgnoreCase)).ToList();}
    void RefreshMappings()
    {
        taxonomy.EnsureCatalogEntries(catalog.Products());mappingRevision=state.Revision;
        mappings.ItemsSource=taxonomy.List(MappingKind).Where(e=>e.Active&&(mappingSearch.Text.Length==0||e.Name.Contains(mappingSearch.Text,StringComparison.CurrentCultureIgnoreCase))).Select(e=>{var m=state.Mappings.SingleOrDefault(m=>m.Kind==MappingKind&&m.LocalId==e.Id);var name=m is null?"":MappingKind==TaxonomyKind.Category?state.Categories.SingleOrDefault(c=>c.Id==m.RemoteId)?.Path:state.Brands.SingleOrDefault(b=>b.Id==m.RemoteId)?.Name;return new TrendyolMappingRow(e.Id,e.Name,m?.RemoteId,name??"",m is null?"Eşleşmedi":name is null||m.LocalName!=e.Name?"Eski eşleştirme; yenileyin":"Kayıtlı");}).ToList();RefreshRemoteTargets();
    }
    void SuggestMappings()
    {
        if(MappingKind==TaxonomyKind.Category?state.Categories.Count==0:state.Brands.Count==0)throw new InvalidOperationException("Önce seçili türün kategori veya marka listesini alın.");
        mappings.ItemsSource=mappings.Items.Cast<TrendyolMappingRow>().Select(r=>{if(r.Reason=="Kayıtlı")return r;var suggestion=MappingKind==TaxonomyKind.Category?TrendyolMatching.Category(r.LocalName,state.Categories):TrendyolMatching.Brand(r.LocalName,state.Brands);return r with{RemoteId=suggestion.RemoteId,RemoteName=suggestion.Name,Reason=suggestion.Reason};}).ToList();
        mappings.UnselectAll();foreach(var row in mappings.Items.Cast<TrendyolMappingRow>().Where(r=>r.RemoteId.HasValue&&r.Reason!="Kayıtlı"))mappings.SelectedItems.Add(row);
        status.Text=$"{mappings.SelectedItems.Count} kesin öneri seçildi. İnceleyip Seçili önerileri uygula ile kaydedin.";
    }
    void ApplyMappings()
    {
        var selected=mappings.SelectedItems.Cast<TrendyolMappingRow>().ToArray();if(selected.Length==0||selected.Any(r=>!r.RemoteId.HasValue))throw new InvalidOperationException("Karşılığı belirlenmiş satırları seçin.");
        var account=Account();var next=store.Load(account.SupplierId);if(next.Revision!=mappingRevision)throw new InvalidOperationException("Eşleştirme listesi eskidi; yenileyin.");
        var entries=taxonomy.List(MappingKind);foreach(var row in selected)if(!entries.Any(e=>e.Id==row.LocalId&&e.Name==row.LocalName&&e.Active))throw new InvalidOperationException("Yerel kayıt değişti; listeyi yenileyin.");
        if(MessageBox.Show(Window.GetWindow(this),string.Join("\n",selected.Take(12).Select(r=>$"{r.LocalName} → {r.RemoteName} ({r.RemoteId})"))+$"\n{selected.Length} eşleştirme kaydedilsin mi?","Eşleştirme önizlemesi",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        foreach(var row in selected){next.Mappings.RemoveAll(m=>m.Kind==MappingKind&&m.LocalId==row.LocalId);next.Mappings.Add(new(MappingKind,row.LocalId,row.LocalName,row.RemoteId!.Value));}store.Save(next);Reload();
    }
    void RemoveMappings()
    {
        var ids=mappings.SelectedItems.Cast<TrendyolMappingRow>().Select(r=>r.LocalId).ToHashSet();if(ids.Count==0)throw new InvalidOperationException("Kayıt seçin.");
        if(MessageBox.Show(Window.GetWindow(this),$"{ids.Count} yerel kaydın Trendyol karşılığı kaldırılsın mı?","Eşleştirmeyi kaldır",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        state.Mappings.RemoveAll(m=>m.Kind==MappingKind&&ids.Contains(m.LocalId));Persist();
    }
}
