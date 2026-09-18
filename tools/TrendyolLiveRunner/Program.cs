using System.Text.Json;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

// Manual diagnostic utility. No key/secret is accepted in arguments or printed.
// Sending is deliberately limited to one already-persisted, explicitly approved preview.
try
{
    if(args.Length<2)throw new ArgumentException("Usage: <seller-id> inspect|refresh|attributes|profile|preview|send-one|batch [argument]. send-one requires a reviewed plan ID and --approved.");
    var account=new TrendyolSettingsStore().Load()??throw new InvalidOperationException("Use the application's encrypted connection settings first.");
    if(account.SupplierId!=args[0])throw new InvalidOperationException("Seller does not match the saved account.");
    var store=new TrendyolWorkspaceStore();var catalog=new CatalogStore();using var client=new TrendyolApiClient(account);using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var jsonOptions=new JsonSerializerOptions{WriteIndented=true};
    switch(args[1])
    {
        case "inspect":
            var query=args.Length>2?args[2]:"";
            Console.WriteLine(JsonSerializer.Serialize(catalog.Products(query).Where(p=>p.Active&&p.Stock>0).Take(20).Select(p=>new{p.Id,p.Sku,p.Barcode,p.Name,p.Brand,p.Category,p.Stock,p.Price,p.Currency,p.VatRate,p.ChannelPrices,p.XmlAttributes,p.ImageUrls,p.Description}),jsonOptions));break;
        case "refresh":
            var state=store.Load(account.SupplierId);
            state.Categories=(await client.GetCategoriesAsync(cancel.Token)).ToList();Console.WriteLine($"Categories={state.Categories.Count}");
            var names=catalog.Products().Select(p=>p.Brand).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct().ToArray();var brands=new Dictionary<long,TrendyolBrand>();
            foreach(var name in names){foreach(var brand in await client.GetBrandsByNameAsync(name,cancel.Token)){if(brands.TryGetValue(brand.Id,out var old)&&old!=brand)throw new InvalidOperationException("Brand changed while refreshing.");brands[brand.Id]=brand;}}
            state.Brands=brands.Values.ToList();Console.WriteLine($"Brands={state.Brands.Count}; Queries={names.Length}");state.DictionaryUpdatedUtc=DateTime.UtcNow;
            store.Save(state);state=store.Load(account.SupplierId);
            state.Products=(await client.GetProductsAsync(true,cancel.Token)).Concat(await client.GetProductsAsync(false,cancel.Token)).ToList();state.ProductsUpdatedUtc=DateTime.UtcNow;Console.WriteLine($"RemoteProducts={state.Products.Count}");
            state.Addresses=(await client.GetAddressesAsync(cancel.Token)).ToList();state.Carriers=(await client.GetCarriersAsync(cancel.Token)).ToList();state.AddressesUpdatedUtc=DateTime.UtcNow;
            store.Save(state);Console.WriteLine($"Addresses={state.Addresses.Count}; Carriers={state.Carriers.Count}; CacheSaved=true");break;
        case "products":
            var productState=store.Load(account.SupplierId);productState.Products=(await client.GetProductsAsync(true,cancel.Token)).Concat(await client.GetProductsAsync(false,cancel.Token)).ToList();productState.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(productState);Console.WriteLine(JsonSerializer.Serialize(new{Count=productState.Products.Count,Matches=args.Length>2?productState.Products.Where(p=>p.Barcode==args[2]||p.StockCode==args[2]).ToArray():[]},jsonOptions));break;
        case "map-product":
            var localProduct=catalog.Products().Single(p=>p.Id==args[2]);var mapState=store.Load(account.SupplierId);var categoryId=long.Parse(args[3]);var brandId=long.Parse(args[4]);
            if(!mapState.Categories.Any(c=>c.Id==categoryId&&c.IsLeaf)||!mapState.Brands.Any(b=>b.Id==brandId))throw new InvalidOperationException("Unknown remote category or brand.");
            var taxonomy=new TaxonomyStore();taxonomy.EnsureCatalogEntries(catalog.Products());
            foreach(var pair in new[]{(Kind:TaxonomyKind.Category,Name:localProduct.Category,RemoteId:categoryId),(Kind:TaxonomyKind.Brand,Name:localProduct.Brand,RemoteId:brandId)})
            {var entry=taxonomy.List(pair.Kind).Single(e=>e.Active&&(pair.Kind==TaxonomyKind.Category?TaxonomyStore.SameCategory(e.Name,pair.Name):TrendyolMatching.Normalize(e.Name)==TrendyolMatching.Normalize(pair.Name)));mapState.Mappings.RemoveAll(m=>m.Kind==pair.Kind&&m.LocalId==entry.Id);mapState.Mappings.Add(new(pair.Kind,entry.Id,entry.Name,pair.RemoteId));}
            store.Save(mapState);Console.WriteLine("Local category and brand mappings saved; no remote write.");break;
        case "search":
            var cached=store.Load(account.SupplierId);var term=args[2];Console.WriteLine(JsonSerializer.Serialize(new{Categories=cached.Categories.Where(c=>c.IsLeaf&&c.Path.Contains(term,StringComparison.CurrentCultureIgnoreCase)),Brands=cached.Brands.Where(b=>b.Name.Contains(term,StringComparison.CurrentCultureIgnoreCase)).Take(30)},jsonOptions));break;
        case "attributes":
            var category=long.Parse(args[2]);var definitions=await client.GetAttributesAsync(category,cancel.Token);var fresh=store.Load(account.SupplierId);fresh.Attributes[category]=definitions.ToList();fresh.AttributesUpdatedUtc[category]=DateTime.UtcNow;store.Save(fresh);Console.WriteLine(JsonSerializer.Serialize(definitions,jsonOptions));break;
        case "profile":
            var profile=JsonSerializer.Deserialize<TrendyolProductProfile>(File.ReadAllText(args[2]))??throw new InvalidOperationException("Missing profile.");if(!catalog.Products().Any(p=>p.Id==profile.ProductId))throw new InvalidOperationException("Local product does not exist.");var current=store.Load(account.SupplierId);current.Profiles.RemoveAll(p=>p.ProductId==profile.ProductId);current.Profiles.Add(profile);store.Save(current);Console.WriteLine("One local Trendyol profile saved; no remote write.");break;
        case "prepare-values":
            if(args.Length!=6)throw new ArgumentException("prepare-values requires local product ID, exact SKU, stock-or-keep and TRY price-or-keep.");
            var edit=catalog.Products().Single(p=>p.Id==args[2]&&p.Sku==args[3]);var editState=store.Load(account.SupplierId);var editProfile=editState.Profiles.Single(p=>p.ProductId==edit.Id);
            if(!editState.Products.Any(p=>p.Barcode==editProfile.IntegrationCode&&p.Approved))throw new InvalidOperationException("Only an approved, matched product may be prepared.");
            var oldStock=edit.Stock;var oldPrice=editProfile.SalePriceTry;
            int? requestedStock=args[4]=="keep"?null:int.Parse(args[4],System.Globalization.CultureInfo.InvariantCulture);
            decimal? requestedPrice=args[5] is "keep" or "clear"?null:decimal.Parse(args[5],System.Globalization.CultureInfo.InvariantCulture);
            if(requestedStock.HasValue&&(edit.LockStock||requestedStock<0||requestedStock>20000))throw new ArgumentException("Stock is locked or invalid.");
            if(args[5]!="keep"&&(edit.LockPrice||requestedPrice.HasValue&&(requestedPrice<=0||decimal.Round(requestedPrice.Value,2)!=requestedPrice)))throw new ArgumentException("Price is locked or invalid.");
            if(requestedStock.HasValue){edit.Stock=requestedStock.Value;catalog.SaveProduct(edit);}
            if(args[5]!="keep"){editProfile.SalePriceTry=requestedPrice;editProfile.ListPriceTry=requestedPrice;store.Save(editState);}
            Console.WriteLine(JsonSerializer.Serialize(new{edit.Id,edit.Sku,editProfile.IntegrationCode,OldStock=oldStock,NewStock=edit.Stock,OldProfilePrice=oldPrice,NewProfilePrice=editProfile.SalePriceTry,RemoteWrite=false},jsonOptions));break;
        case "preview":
            var mode=args.Length>3?Enum.Parse<TrendyolOperation>(args[3]):TrendyolOperation.Create;var preview=store.Preview(account,[args[2]],mode);Console.WriteLine(JsonSerializer.Serialize(preview,jsonOptions));break;
        case "send-one":
            if(args.Length is <4 or >5||args[3]!="--approved")throw new InvalidOperationException("A reviewed plan ID and explicit --approved are required. Inventory writes also require the exact integration barcode.");
            // Verify count before Claim or any network request; the store still revalidates persisted state.
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop","catalog.db")))
            {db.Open();using var command=db.CreateCommand();command.CommandText="SELECT Json FROM TrendyolPlans WHERE Id=$id AND SellerId=$seller";command.Parameters.AddWithValue("$id",args[2]);command.Parameters.AddWithValue("$seller",account.SupplierId);var reviewed=JsonSerializer.Deserialize<TrendyolPlan>(command.ExecuteScalar() as string??throw new InvalidOperationException("Plan not found."))!;if(reviewed.Rows.Count!=1||reviewed.Rows.Count(r=>r.ItemJson!=null)!=1)throw new InvalidOperationException("Only one product can be sent.");if(reviewed.Operation!=TrendyolOperation.Create&&(reviewed.Operation is not (TrendyolOperation.Price or TrendyolOperation.Stock or TrendyolOperation.PriceAndStock)||args.Length!=5||reviewed.Rows.Single().Barcode!=args[4]))throw new InvalidOperationException("Inventory writes require a matching single-product barcode and an inventory operation.");}
            var receipt=await store.SendAsync(args[2],account,true,client,cancel.Token);Console.WriteLine(JsonSerializer.Serialize(receipt,jsonOptions));break;
        case "batch":
            var previous=store.Receipts(account.SupplierId).Single(r=>r.PlanId==args[2]);var result=await client.GetBatchAsync(previous.BatchId,cancel.Token);Console.WriteLine(JsonSerializer.Serialize(store.UpdateBatch(previous.PlanId,account.SupplierId,result),jsonOptions));break;
        case "receipts":
            Console.WriteLine(JsonSerializer.Serialize(store.Receipts(account.SupplierId).Take(10),jsonOptions));break;
        default:throw new ArgumentException("Unknown command.");
    }
}
catch(Exception error){Console.Error.WriteLine(error is InvalidOperationException or ArgumentException ? error.Message : $"Operation failed: {error.GetType().Name}");if(error is InvalidDataException)Console.Error.WriteLine(error.StackTrace);Environment.ExitCode=1;}
