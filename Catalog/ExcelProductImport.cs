using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public enum ExcelImportMode { AddAndUpdate, AddOnly, UpdateOnly, PriceOnly, StockOnly }
public sealed record ExcelProductField(string Key, string Label, string Group);
public sealed record ExcelProductRow(int RowNumber, string Action, string Sku, string Name, string Changes, string Note, decimal? OldPrice, decimal? NewPrice, int? OldStock, int? NewStock)
{
    public string Status => Action switch { "CREATE" => "Eklenecek", "UPDATE" => "Güncellenecek", "SKIP" => "Atlanacak", _ => "Hatalı" };
    public bool CanApply => Action is "CREATE" or "UPDATE";
}
internal sealed record ExcelPlannedProduct(int RowNumber, string Action, string Json);
public sealed class ExcelProductPlan
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<ExcelProductRow> Rows { get; }
    public IReadOnlyList<string> Errors { get; }
    internal string FileHash { get; }
    internal string ProfileHash { get; }
    internal string CatalogHash { get; }
    internal string StoreIdentity { get; }
    internal IReadOnlyList<ExcelPlannedProduct> Products { get; }
    internal ExcelProductPlan(List<ExcelProductRow> rows, List<ExcelPlannedProduct> products, string fileHash, string profileHash, string catalogHash, string storeIdentity)
    {
        Rows = rows.AsReadOnly(); Products = products.AsReadOnly(); FileHash = fileHash; ProfileHash = profileHash; CatalogHash = catalogHash; StoreIdentity = storeIdentity;
        Errors = rows.Where(r => r.Action == "ERROR").Select(r => $"Satır {r.RowNumber}: {r.Note}").ToList().AsReadOnly();
    }
}

public static class ExcelProductImport
{
    public static IReadOnlyList<ExcelProductField> Fields { get; } = new List<ExcelProductField>
    {
        new("Sku","Stok kodu / SKU","Ürün"), new("Id","Ürün ID (isteğe bağlı kontrol)","Ürün"), new("Barcode","Barkod","Ürün"),
        new("Name","Ürün adı","Ürün"), new("Brand","Marka","Ürün"), new("Category","Kategori ağacı","Ürün"), new("Description","Açıklama","Ürün"),
        new("Stock","Stok / adet","Ürün"), new("Active","Aktif","Ürün"), new("Gtin","GTIN","Ürün"), new("Mpn","MPN","Ürün"), new("InvoiceName","Fatura adı","Ürün"), new("Subtitle","Alt başlık","Ürün"), new("Shelf","Raf","Ürün"), new("ImageUrls","Görsel URL'leri","Görseller"),
        new("CostCurrency","Alış para birimi","Fiyatlar"), new("ExpiresOn","Miad / son kullanma tarihi","Ürün"), new("Cost","Alış fiyatı","Fiyatlar"), new("Price","Satış fiyatı","Fiyatlar"), new("Currency","Para birimi","Fiyatlar"), new("VatRate","KDV oranı (%)","Fiyatlar")
    }.Concat(XmlFieldDefinitions.All.Where(f=>f.IsAttribute).Select(f=>new ExcelProductField(f.Key,f.Label,f.Key.StartsWith("Category")||f.Key=="BrandId"?"Kategori / marka":"Diğer bilgiler")))
    .Concat(Enumerable.Range(1,9).Select(n=>new ExcelProductField("Image"+n,"Görsel "+n,"Görseller")))
    .Concat(Enumerable.Range(2,4).Select(n=>new ExcelProductField("Category"+n,"Kategori seviyesi "+n,"Kategori / marka")))
    .Concat(XmlCategoryRules.Channels.Concat(new[]{"N11"}).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(c => new[]{new ExcelProductField($"Channel:{c}:Sale",$"{c} satış fiyatı","Pazaryeri fiyatları"),new ExcelProductField($"Channel:{c}:List",$"{c} liste fiyatı","Pazaryeri fiyatları")})).ToList().AsReadOnly();
    public static IReadOnlyList<ExcelSheetColumn> Columns(string path, ExcelImportProfile profile)
    {
        // Reading the workbook's columns must work even while the user is repairing an invalid mapping.
        var copy = JsonSerializer.Deserialize<ExcelImportProfile>(JsonSerializer.Serialize(profile))!;
        copy.ColumnLetters.Clear(); copy.ColumnMappings.Clear(); copy.UseColumnLetters = true;
        using var data = new ExcelWorkbookData(path, copy);
        var last = data.Sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        return Enumerable.Range(1,last).Select(n => new ExcelSheetColumn(n, ExcelWorkbookData.ColumnLetter(n), data.Sheet.Cell(profile.HeaderRow,n).GetString(), data.Sheet.Cell(profile.HeaderRow+1,n).GetFormattedString())).ToList();
    }
    public static bool Allowed(ExcelImportMode mode, string key) => key is "Sku" or "Id" or "Barcode" || mode switch
    {
        ExcelImportMode.StockOnly => key == "Stock",
        ExcelImportMode.PriceOnly => key is "Price" or "Cost" or "Currency" or "CostCurrency" or "VatRate" || key.StartsWith("Channel:",StringComparison.Ordinal),
        _ => true
    };
    static bool Locked(CatalogProduct p, string field) => field switch
    {
        "Name" => p.LockName, "Description" or "Description2" or "Description3" => p.LockDescription, "ImageUrls" => p.LockImages, "Stock" => p.LockStock,
        "Cost" or "Price" or "Currency" or "CostCurrency" or "VatRate" => p.LockPrice,
        _ => field.StartsWith("Image",StringComparison.Ordinal) ? p.LockImages : field.StartsWith("Channel:",StringComparison.Ordinal) && p.LockPrice
    };
    static string Key(string text) => CatalogStore.NormalizeIdentityKey(text);
    public static ExcelProductPlan Preview(CatalogStore store, string path, ExcelImportProfile profile)
    {
        if (!Enum.IsDefined(profile.ImportMode)) throw new InvalidOperationException("Excel işlem türü geçersiz.");
        using var data = new ExcelWorkbookData(path, profile);
        var snapshot = store.ExcelSnapshot();
        var bySku = snapshot.Products.Where(p=>Key(p.Sku).Length>0).GroupBy(p=>Key(p.Sku)).ToDictionary(g=>g.Key,g=>g.ToList());
        var byBarcode = snapshot.Products.Where(p=>Key(p.Barcode).Length>0).GroupBy(p=>Key(p.Barcode)).ToDictionary(g=>g.Key,g=>g.ToList());
        var rows = new List<ExcelProductRow>(); var products = new List<ExcelPlannedProduct>(); var seen = new HashSet<string>(); var seenBarcodes = new HashSet<string>();
        var known = Fields.Select(f=>f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (data.Columns.Keys.Concat(profile.Defaults.Keys).Any(k=>!known.Contains(k))) throw new InvalidOperationException("Eşlemede desteklenmeyen ürün alanı var.");
        if (!data.Mapped("Sku")) throw new InvalidOperationException("Stok kodu / SKU sütununu eşleyin.");
        var selected = profile.SelectedFields?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = Fields.Where(f=>f.Key is not ("Sku" or "Id" or "Barcode") && Allowed(profile.ImportMode,f.Key) && data.Mapped(f.Key) && (selected is null || selected.Contains(f.Key))).ToList();
        if (fields.Count==0) throw new InvalidOperationException("İşlenecek en az bir alanı sütunla eşleyin.");
        foreach (var row in data.Rows)
        {
            string sku="", name="";
            try
            {
                sku=data.Text(row,"Sku");
                if (string.IsNullOrWhiteSpace(sku) || sku.Any(char.IsControl)) throw new InvalidOperationException("Geçerli bir SKU zorunlu.");
                if (!seen.Add(Key(sku))) throw new InvalidOperationException("SKU dosyada yineleniyor.");
                var matches=bySku.GetValueOrDefault(Key(sku));
                if (matches?.Count>1) throw new InvalidOperationException("SKU katalogda birden fazla ürünle eşleşiyor.");
                var old=matches?.Single(); name=old?.Name ?? "";
                var barcode=data.Text(row,"Barcode"); var id=data.Text(row,"Id");
                if(barcode.Length>0 && !seenBarcodes.Add(Key(barcode)))throw new InvalidOperationException("Barkod dosyada yineleniyor.");
                if (id.Length>0 && (old is null || !old.Id.Equals(id,StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Ürün ID ve SKU eşleşmesi uyuşmuyor.");
                if (barcode.Length>0 && (byBarcode.GetValueOrDefault(Key(barcode))?.Any(p=>p.Id!=old?.Id)==true || old is not null && Key(old.Barcode)!=Key(barcode))) throw new InvalidOperationException("SKU ve barkod uyuşmuyor; ürün kimliği değiştirilemez.");
                if (old is null && profile.ImportMode is not (ExcelImportMode.AddOnly or ExcelImportMode.AddAndUpdate))
                { rows.Add(new(row.RowNumber(),"SKIP",sku,name,"","SKU katalogda yok; bu işlem yeni ürün eklemez.",null,null,null,null));continue; }
                if (old is not null && profile.ImportMode==ExcelImportMode.AddOnly)
                { rows.Add(new(row.RowNumber(),"SKIP",sku,name,"","SKU zaten kayıtlı; yalnız yeni ürün ekleme seçili.",old.Price,old.Price,old.Stock,old.Stock));continue; }
                var next=old is null ? new CatalogProduct{Sku=sku,Barcode=barcode,Currency="TRY",SourceKind="excel",PriceSource="excel",StockSource="excel",MediaSource="excel"} : JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(old))!;
                var notes=new List<string>(); var changes=new List<string>();
                var activeFields=fields.Where(f=>
                {
                    if(old is not null && Locked(old,f.Key)){notes.Add(f.Label+": XML kilidi korundu");return false;}
                    return data.Text(row,f.Key).Length>0;
                }).ToList();
                // Tax and currency are resolved before converting any monetary field.
                foreach(var f in activeFields.Where(f=>f.Key is "VatRate" or "Currency")) Set(f.Key);
                foreach(var f in activeFields.Where(f=>f.Key is not ("VatRate" or "Currency") && !f.Key.StartsWith("Channel:",StringComparison.Ordinal))) Set(f.Key);
                foreach(var channel in activeFields.Where(f=>f.Key.StartsWith("Channel:",StringComparison.Ordinal)).GroupBy(f=>f.Key.Split(':')[1]))
                {
                    var previous=next.ChannelPrices.GetValueOrDefault(channel.Key);
                    var sale=previous?.SalePrice; var list=previous?.ListPrice;
                    foreach(var f in channel){var amount=Amount(f.Key);if(f.Key.EndsWith(":Sale",StringComparison.Ordinal))sale=amount;else list=amount;}
                    sale ??= next.Price;
                    if(list.HasValue && list.Value<sale) throw new InvalidOperationException(channel.Key+": liste fiyatı satış fiyatından düşük olamaz.");
                    var channelCurrency=activeFields.Any(f=>f.Key=="Currency")?next.Currency:previous?.Currency??next.Currency;
                    var price=new XmlChannelPrice(sale.Value,list,channelCurrency);
                    if(previous!=price){next.ChannelPrices[channel.Key]=price;changes.Add(channel.Key+": "+(previous?.SalePrice.ToString("0.00")??"—")+" → "+sale.Value.ToString("0.00")+"; liste "+(list?.ToString("0.00")??"—"));}
                }
                CatalogStore.ValidateExcelProduct(next);
                if(old is null && string.IsNullOrWhiteSpace(next.Name)) throw new InvalidOperationException("Yeni ürün için ürün adı zorunlu.");
                name=next.Name;
                var action=old is null?"CREATE":changes.Count==0?"SKIP":"UPDATE";
                if(action is "CREATE" or "UPDATE")
                {
                    if(old is null || next.Price!=old.Price || next.Cost!=old.Cost || next.VatRate!=old.VatRate || next.Currency!=old.Currency || next.CostCurrency!=old.CostCurrency || !next.ChannelPrices.OrderBy(p=>p.Key).SequenceEqual(old.ChannelPrices.OrderBy(p=>p.Key))) next.PriceSource="excel";
                    if(old is not null && (next.Price!=old.Price || next.Currency!=old.Currency || next.Cost!=old.Cost || next.CostCurrency!=old.CostCurrency)){next.FormulaPriceTry=null;next.AppliedTryRate=null;next.FxRateDate=null;next.FxFetchedUtc=null;}
                    if(old is null || next.Stock!=old.Stock)next.StockSource="excel";
                    if(old is null || next.ImageUrls!=old.ImageUrls)next.MediaSource="excel";
                    products.Add(new(row.RowNumber(),action,JsonSerializer.Serialize(next)));
                }
                rows.Add(new(row.RowNumber(),action,sku,name,string.Join(" | ",changes),string.Join("; ",notes.DefaultIfEmpty(action=="SKIP"?"Seçili alanlarda değişiklik yok.":"")),old?.Price,next.Price,old?.Stock,next.Stock));
                decimal Amount(string key)
                {
                    var amount=data.Number(row,key);
                    if(amount<0)throw new InvalidOperationException(key+": fiyat negatif olamaz.");
                    return Math.Round(profile.PriceIncludesVat?amount:checked(amount*(1+next.VatRate/100)),2,MidpointRounding.AwayFromZero);
                }
                void Set(string field)
                {
                    if(XmlFieldDefinitions.All.Any(f=>f.Key==field && f.IsAttribute))
                    {
                        var text=data.Text(row,field);
                        if(field is "Desi" or "Weight" or "Width" or "Height" or "Depth"){var number=data.Number(row,field);if(number<0)throw new InvalidOperationException(field+": değer negatif olamaz.");text=number.ToString(CultureInfo.InvariantCulture);}
                        if(text.Length>(field.StartsWith("Description",StringComparison.Ordinal)?4000:2000) || text.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')))throw new InvalidOperationException(field+": geçersiz veya çok uzun metin.");
                        var previous=next.XmlAttributes.GetValueOrDefault(field,"");if(previous!=text){next.XmlAttributes[field]=text;changes.Add(Fields.First(f=>f.Key==field).Label+": "+Display(previous)+" → "+Display(text));}return;
                    }
                    if(field.StartsWith("Image",StringComparison.Ordinal) && field!="ImageUrls")
                    {
                        var index=int.Parse(field[5..],CultureInfo.InvariantCulture)-1;
                        var images=next.ImageUrls.Split('|',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
                        if(index>images.Count)throw new InvalidOperationException("Görselleri boş sıra bırakmadan eşleyin.");
                        var image=data.Text(row,field);if(!Uri.TryCreate(image,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))throw new InvalidOperationException("Görsel için HTTP/HTTPS adresi gerekli.");
                        var beforeImage=index<images.Count?images[index]:"";if(index==images.Count)images.Add(image);else images[index]=image;
                        if(beforeImage!=image){next.ImageUrls=string.Join(" | ",images);changes.Add(field+": "+Display(beforeImage)+" → "+Display(image));}return;
                    }
                    if(field.StartsWith("Category",StringComparison.Ordinal) && field!="Category")
                    {
                        var index=int.Parse(field[8..],CultureInfo.InvariantCulture)-1;var parts=next.Category.Split('>',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
                        if(index>parts.Count)throw new InvalidOperationException("Kategori seviyelerini boş bırakmadan eşleyin.");
                        var part=data.Text(row,field);if(part.Contains('>'))throw new InvalidOperationException("Tek kategori seviyesinde > kullanmayın.");
                        var beforeCategory=next.Category;if(index==parts.Count)parts.Add(part);else parts[index]=part;next.Category=NormalizeCategory(string.Join(" > ",parts));
                        if(beforeCategory!=next.Category)changes.Add(field+": "+Display(beforeCategory)+" → "+Display(next.Category));return;
                    }
                    var property=typeof(CatalogProduct).GetProperty(field)!; var before=property.GetValue(next); object value;
                    if(field is "Price" or "Cost")value=Amount(field);
                    else if(field=="VatRate"){var vat=data.Number(row,field);if(vat is <0 or >100)throw new InvalidOperationException("KDV oranı 0–100 arası olmalı.");value=vat;}
                    else if(field=="Stock") {var number=data.Number(row,field);if(number<0 || number>int.MaxValue || decimal.Truncate(number)!=number)throw new InvalidOperationException("Stok negatif olmayan tam sayı olmalı.");value=profile.AddStock && old is not null?checked(old.Stock+(int)number):(int)number;}
                    else if(field is "Currency" or "CostCurrency"){var currency=data.Text(row,field).ToUpperInvariant();if(currency=="TL")currency="TRY";if(!LocaleSettings.SupportedCurrencies.Contains(currency))throw new InvalidOperationException("Desteklenmeyen para birimi.");value=currency;}
                    else if(field=="ExpiresOn"){if(!DateTime.TryParse(data.Text(row,field),ExcelProfileStore.Culture(profile.CultureName),DateTimeStyles.None,out var date))throw new InvalidOperationException("Miad tarihi geçersiz.");value=date.Date;}
                    else if(field=="Active")value=data.Text(row,field).ToLowerInvariant() switch {"true" or "1" or "evet" or "aktif"=>true,"false" or "0" or "hayır" or "pasif"=>false,_=>throw new InvalidOperationException("Aktif değeri true/false veya 1/0 olmalı.")};
                    else if(field=="Category")value=NormalizeCategory(data.Text(row,field));
                    else value=data.Text(row,field);
                    if(!Equals(before,value)){property.SetValue(next,value);changes.Add(Fields.First(f=>f.Key==field).Label+": "+Display(before)+" → "+Display(value));}
                }
            }
            catch(Exception error) when(error is InvalidOperationException or ArgumentException or OverflowException or FormatException)
            {rows.Add(new(row.RowNumber(),"ERROR",sku,name,"",error.Message,null,null,null,null));}
        }
        return new(rows,products,data.FileHash,ExcelWorkbookData.Signature(profile),snapshot.Hash,snapshot.Identity);
    }
    static string Display(object? value) {var s=Convert.ToString(value,CultureInfo.CurrentCulture)??"";return s.Length>70?s[..67]+"…":s.Length==0?"—":s;}
    public static string NormalizeCategory(string path)
    {
        var parts=path.Split('>').Select(p=>p.Trim()).ToArray();
        if(parts.Any(p=>p.Length==0 || p.Any(char.IsControl)))throw new InvalidOperationException("Kategori ağacında boş veya geçersiz kırılım var.");
        var result=string.Join(" > ",parts);if(result.Length>200)throw new InvalidOperationException("Kategori yolu 200 karakteri aşamaz.");return result;
    }
    public static CatalogUndoReceipt Apply(CatalogStore store,string path,ExcelImportProfile profile,ExcelProductPlan? plan,IReadOnlyCollection<int> rowNumbers)
    {
        if(plan is null)throw new InvalidOperationException("Önce Excel önizlemesi oluşturun.");
        if(plan.Errors.Count>0)throw new InvalidOperationException("Hatalı satırlar düzeltilmeden işlem uygulanamaz.");
        if(ExcelWorkbookData.Signature(profile)!=plan.ProfileHash || ExcelWorkbookData.HashFile(path)!=plan.FileHash)throw new InvalidOperationException("Dosya veya ayarlar değişti; önizlemeyi yenileyin.");
        if(rowNumbers.Count==0 || rowNumbers.Distinct().Count()!=rowNumbers.Count || rowNumbers.Any(n=>!plan.Products.Any(p=>p.RowNumber==n)))throw new InvalidOperationException("Önizlemeden yalnız eklenecek/güncellenecek satırları seçin.");
        return store.ApplyExcelPlan(plan,rowNumbers);
    }
}
