using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public enum NesDocumentKind { EInvoice, EArchive }

public sealed record NesReceiverIdentity(string Value, string Scheme, bool UsedEArchiveFallback);

public static class NesReceiverIdentityResolver
{
    public const string IndividualEArchiveFallback = "11111111111";

    public static NesReceiverIdentity Resolve(OrderSnapshot order, NesDocumentKind kind)
    {
        var value = (order.TaxNumber ?? "").Trim();
        if (value.Length > 0)
        {
            if (!value.All(char.IsDigit) || value.Length is not 10 and not 11)
                throw new InvalidOperationException("Alıcının vergi / kimlik numarası 10 haneli VKN veya 11 haneli TCKN olmalı.");
            return new(value, value.Length == 10 ? "VKN" : "TCKN", false);
        }

        var corporate = order.EInvoiceAvailable == true || order.CommercialInvoice || !string.IsNullOrWhiteSpace(order.CustomerTaxOffice);
        if (kind == NesDocumentKind.EInvoice || corporate)
            throw new InvalidOperationException("Kurumsal veya e-Fatura alıcısı için Trendyol'dan VKN / TCKN gelmeli. Bilgi eksikken fatura hazırlanmadı.");

        return new(IndividualEArchiveFallback, "TCKN", true);
    }
}

public sealed class NesSettings
{
    public string ApiKey { get; set; } = "";
    public string EInvoiceBaseUrl { get; set; } = "https://apitest.nes.com.tr/einvoice/";
    public string EArchiveBaseUrl { get; set; } = "https://apitest.nes.com.tr/earchive/";
    public string SenderAlias { get; set; } = "";
    public string SourceApp { get; set; } = "MonoBridge";
    public string CompanyTitle { get; set; } = "";
    public string TaxNumber { get; set; } = "";
    public string TaxOffice { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string District { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string EInvoiceSeries { get; set; } = "MBF";
    public string EArchiveSeries { get; set; } = "MBA";
}

public static class NesSettingsValidator
{
    public static void Validate(NesSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Length > 4096 || settings.ApiKey.Any(char.IsControl))
            throw new ArgumentException("Geçerli NES API anahtarı gerekli.");
        ValidateBase(settings.EInvoiceBaseUrl, "einvoice");
        ValidateBase(settings.EArchiveBaseUrl, "earchive");
        if (settings.SourceApp.Length is < 1 or > 80 || settings.SourceApp.Any(char.IsControl)) throw new ArgumentException("NES kaynak uygulama adı geçersiz.");
        if (settings.CompanyTitle.Length is < 1 or > 300 || settings.TaxNumber is not { Length: 10 } and not { Length: 11 } || !settings.TaxNumber.All(char.IsDigit))
            throw new ArgumentException("Firma adı ile 10 haneli VKN veya 11 haneli TCKN gerekli.");
        foreach (var value in new[] { settings.TaxOffice, settings.Address, settings.City, settings.District, settings.Email, settings.Phone })
            if (value.Length > 500 || value.Any(char.IsControl)) throw new ArgumentException("NES firma alanlarından biri geçersiz.");
        ValidateSeries(settings.EInvoiceSeries); ValidateSeries(settings.EArchiveSeries);
    }

    public static Uri Base(NesSettings settings, NesDocumentKind kind)
    {
        Validate(settings);
        return new Uri(kind == NesDocumentKind.EInvoice ? settings.EInvoiceBaseUrl : settings.EArchiveBaseUrl, UriKind.Absolute);
    }

    static void ValidateBase(string value, string product)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("NES servis adresi kullanıcı bilgisi içermeyen HTTPS adresi olmalı.");
        if (!(uri.Host.Equals("nes.com.tr", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nes.com.tr", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("API anahtarı yalnız nes.com.tr servislerine gönderilebilir.");
        if (!uri.AbsolutePath.Trim('/').EndsWith(product, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NES {product} servis kökü yanlış.");
    }

    public static string ValidateSeries(string value)
    {
        value = (value ?? "").Trim().ToUpperInvariant();
        if (value.Length != 3 || !value.All(c => c is >= 'A' and <= 'Z')) throw new ArgumentException("Fatura serisi üç İngilizce harften oluşmalı (ör. MBF). MonoBridge için ayrı seri kullanın.");
        return value;
    }
}

public sealed record NesRemoteDocument(string Uuid, string DocumentNumber, DateTimeOffset? IssueDate, decimal PayableAmount, string Currency, string Status, NesDocumentKind Kind, string SourceAppRecordId, string MarketplaceOrderNumber, string MarketplaceSupplierId, string MarketplaceProvider)
{
    public string DocumentKindLabel => Kind == NesDocumentKind.EInvoice ? "e-Fatura" : "e-Arşiv";
}
public sealed record NesUploadResult(string Uuid, string DocumentNumber, byte[]? Preview);

public sealed record NesOrderReconciliationResult(int Matched,int AlreadyLinked,int Ambiguous,int Unmatched,int Conflicts)
{
    public string Label=>$"{Matched} fatura numarası siparişe bağlandı · {AlreadyLinked} zaten bağlı · {Unmatched} eşleşmeyen · {Ambiguous} belirsiz · {Conflicts} çakışma";
}

public static class NesOrderReconciler
{
    public static NesOrderReconciliationResult Reconcile(OrdersStore store,IEnumerable<NesRemoteDocument> documents)
    {
        var orders=store.ReadAll();var matched=0;var already=0;var ambiguous=0;var unmatched=0;var conflicts=0;
        foreach(var document in documents.Where(x=>x.DocumentNumber.Length>0&&Guid.TryParse(x.Uuid,out _)))
        {
            var candidates=orders.Where(order=>Matches(order,document)).ToList();
            if(candidates.Count==0){unmatched++;continue;}if(candidates.Count>1){ambiguous++;continue;}
            var order=candidates[0];
            if(order.InvoiceUuid.Length>0)
            {
                if(order.InvoiceUuid.Equals(document.Uuid,StringComparison.OrdinalIgnoreCase)&&order.InvoiceNumber.Equals(document.DocumentNumber,StringComparison.OrdinalIgnoreCase)){already++;continue;}
                conflicts++;continue;
            }
            store.AttachNesInvoice(order.Marketplace,order.ShopId,order.OrderId,document.DocumentKindLabel,document.Uuid,document.DocumentNumber,document.Status,document.IssueDate);
            order.InvoiceProvider="NES";order.InvoiceType=document.DocumentKindLabel;order.InvoiceUuid=document.Uuid;order.InvoiceNumber=document.DocumentNumber;order.InvoiceStatus=document.Status;order.InvoiceIssuedAt=document.IssueDate??DateTimeOffset.UtcNow;matched++;
        }
        return new(matched,already,ambiguous,unmatched,conflicts);
    }

    static bool Matches(OrderSnapshot order,NesRemoteDocument document)
    {
        if(document.SourceAppRecordId.Length>0&&document.SourceAppRecordId.Equals(NesConnection.SourceRecordId(order),StringComparison.OrdinalIgnoreCase))return true;
        if(document.MarketplaceOrderNumber.Length==0||!document.MarketplaceOrderNumber.Equals(order.OrderId,StringComparison.OrdinalIgnoreCase))return false;
        if(document.MarketplaceSupplierId.Length>0&&!document.MarketplaceSupplierId.Equals(order.ShopId,StringComparison.OrdinalIgnoreCase))return false;
        var provider=Normalize(document.MarketplaceProvider);return provider.Length==0||provider==Normalize(order.Marketplace);
    }

    static string Normalize(string value)=>new((value??"").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed class NesConnection(HttpClient http)
{
    const int MaxJsonBytes = 4 * 1024 * 1024;
    const int MaxPdfBytes = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<NesRemoteDocument>> ReadOutgoingAsync(NesSettings settings, NesDocumentKind kind, CancellationToken cancellationToken = default)
    {
        var path = kind == NesDocumentKind.EInvoice ? "v1/outgoing/invoices?sort=CreatedAt%20desc&pageSize=100&page=1" : "v1/invoices?sort=CreatedAt%20desc&pageSize=100&page=1";
        using var request = Request(settings, kind, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("NES fatura listesi beklenen biçimde değil.");
        var rows = new List<NesRemoteDocument>();
        foreach (var item in data.EnumerateArray())
        {
            var uuid = Text(item, "id", "uuid"); var number = Text(item, "documentNumber");
            if (uuid.Length == 0 || number.Length == 0) continue;
            DateTimeOffset? date = DateTimeOffset.TryParse(Text(item, "issueDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            var marketplace = Object(item, "marketPlaceOrderSummary");
            rows.Add(new(uuid, number, date, Decimal(item, "payableAmount"), Text(item, "documentCurrencyCode"), Value(item, kind == NesDocumentKind.EInvoice ? "outgoingStatus" : "archiveDocumentStatus"), kind,
                Text(item, "sourceAppRecordId"), Text(marketplace, "orderNumber"), Text(marketplace, "marketPlaceSupplierId"), Value(marketplace, "marketPlaceProvider")));
        }
        return rows;
    }

    public async Task<byte[]> PreviewAsync(NesSettings settings, NesDocumentKind kind, byte[] xml, CancellationToken cancellationToken = default)
    {
        using var request = Request(settings, kind, HttpMethod.Post, "v1/uploads/document/preview");
        request.Content = Multipart(xml, ("DocumentTemplateTitle", ""));
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var error = Text(json.RootElement, "errorMessage"); if (error.Length > 0) throw new InvalidOperationException("NES önizleme hatası: " + Limit(error));
        var content = Text(json.RootElement, "content");
        try { return content.Length == 0 ? [] : Convert.FromBase64String(content); }
        catch (FormatException) { throw new InvalidOperationException("NES önizleme içeriği beklenen biçimde değil."); }
    }

    public async Task<NesUploadResult> UploadAsync(NesSettings settings, NesDocumentKind kind, byte[] xml, OrderSnapshot order, string receiverAlias, bool directSend, CancellationToken cancellationToken = default)
    {
        using var request = Request(settings, kind, HttpMethod.Post, "v1/uploads/document");
        var fields = new List<(string, string)>
        {
            ("IsDirectSend", directSend ? "true" : "false"), ("PreviewType", "Html"), ("SourceApp", settings.SourceApp.Trim()),
            ("SourceAppRecordId", SourceRecordId(order)), ("AutoSaveCompany", "true"),
            ("MarketPlaceOrderDate", order.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)), ("MarketPlaceOrderNumber", order.OrderId),
            ("MarketPlaceProvider", Marketplace(order.Marketplace)), ("MarketPlaceSupplierId", order.ShopId)
        };
        if (kind == NesDocumentKind.EInvoice)
        {
            fields.Add(("SenderAlias", settings.SenderAlias.Trim()));
            if (!string.IsNullOrWhiteSpace(receiverAlias)) fields.Add(("ReceiverAlias", receiverAlias.Trim()));
        }
        request.Content = Multipart(xml, fields.ToArray());
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var uuid = Text(json.RootElement, "uuid");
        if (!Guid.TryParse(uuid, out _)) throw new InvalidOperationException("NES belge UUID değeri dönmedi; fatura gönderildi sayılmadı.");
        byte[]? preview = null;
        if (json.RootElement.TryGetProperty("preview", out var p))
        {
            var error = Text(p, "errorMessage"); if (error.Length > 0) throw new InvalidOperationException("NES belge önizleme hatası: " + Limit(error));
            var content = Text(p, "content"); if (content.Length > 0) try { preview = Convert.FromBase64String(content); } catch (FormatException) { }
        }
        return new(uuid, Text(json.RootElement, "documentNumber"), preview);
    }

    public async Task<byte[]> DownloadPdfAsync(NesSettings settings, NesDocumentKind kind, string uuid, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(uuid, out _)) throw new ArgumentException("Geçerli NES fatura UUID değeri gerekli.");
        var path = kind == NesDocumentKind.EInvoice ? $"v1/outgoing/invoices/{uuid}/pdf" : $"v1/invoices/{uuid}/pdf";
        using var request = Request(settings, kind, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        if (response.Content.Headers.ContentLength > MaxPdfBytes) throw new InvalidOperationException("NES PDF dosyası 10 MB sınırını aşıyor.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream();
        var buffer = new byte[81920]; int read; while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) { if (output.Length + read > MaxPdfBytes) throw new InvalidOperationException("NES PDF dosyası 10 MB sınırını aşıyor."); output.Write(buffer, 0, read); }
        var bytes = output.ToArray(); if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidOperationException("NES geçerli bir PDF faturası döndürmedi."); return bytes;
    }

    static HttpRequestMessage Request(NesSettings settings, NesDocumentKind kind, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(NesSettingsValidator.Base(settings, kind), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    static MultipartFormDataContent Multipart(byte[] xml, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(xml); file.Headers.ContentType = new MediaTypeHeaderValue("application/xml"); form.Add(file, "File", "invoice.xml");
        foreach (var (name, value) in fields) if (value.Length > 0) form.Add(new StringContent(value, Encoding.UTF8), name);
        return form;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        { response.Dispose(); throw new InvalidOperationException("NES yönlendirme yanıtı verdi; API anahtarı başka adrese gönderilmedi."); }
        if (!response.IsSuccessStatusCode)
        { var code = (int)response.StatusCode; response.Dispose(); throw new InvalidOperationException($"NES HTTP {code}: işlem tamamlanamadı. API yetkisini ve belge alanlarını kontrol edin."); }
        return response;
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new InvalidOperationException("NES yanıtı güvenli boyut sınırını aşıyor.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream();
        var buffer = new byte[81920]; int read; while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) { if (output.Length + read > MaxJsonBytes) throw new InvalidOperationException("NES yanıtı güvenli boyut sınırını aşıyor."); output.Write(buffer, 0, read); }
        try { return JsonDocument.Parse(output.ToArray()); } catch (JsonException) { throw new InvalidOperationException("NES yanıtı beklenen JSON biçiminde değil."); }
    }

    public static string SourceRecordId(OrderSnapshot order) => $"{order.Marketplace}:{order.ShopId}:{order.OrderId}";
    static string Marketplace(string value) => value.Trim().ToLowerInvariant() switch { "trendyol" => "Trendyol", "hepsiburada" or "hb" => "Hepsiburada", "amazon" => "Amazon", "n11" => "N11", "pazarama" => "Pazarama", "pttavm" => "PttAvm", "idefix" => "Idefix", _ => "None" };
    static string Text(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; return ""; }
    static JsonElement Object(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    static string Value(JsonElement row, string name) => !row.TryGetProperty(name, out var value) ? "" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    static decimal Decimal(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0;
    static string Limit(string value) => value.Length <= 500 ? value : value[..500];
}

public sealed record NesUblDraft(byte[] Xml, decimal TaxExclusiveAmount, decimal TaxAmount, decimal PayableAmount, string Hash);

public static class NesUblBuilder
{
    public static NesUblDraft Build(NesSettings settings, OrderSnapshot order, NesDocumentKind kind, string documentNumber, Guid uuid, DateTimeOffset issueAt)
    {
        NesSettingsValidator.Validate(settings); OrdersRules.Validate(order);
        if (order.Items.Count == 0 || order.Items.Any(x => !x.UnitPrice.HasValue || x.UnitPrice < 0 || x.VatRate is < 0 or > 100))
            throw new InvalidOperationException("NES faturası için her sipariş satırında fiyat, adet ve KDV oranı gerekli.");
        var receiverIdentity = NesReceiverIdentityResolver.Resolve(order, kind);
        if (string.IsNullOrWhiteSpace(order.BillingName) || string.IsNullOrWhiteSpace(order.BillingAddress) || string.IsNullOrWhiteSpace(order.BillingCity))
            throw new InvalidOperationException("Müşteri fatura adı, adresi ve ili gerekli.");
        if (kind == NesDocumentKind.EInvoice && string.IsNullOrWhiteSpace(settings.SenderAlias)) throw new InvalidOperationException("e-Fatura için NES gönderici etiketi gerekli.");
        if (documentNumber.Length != 16) throw new InvalidOperationException("Fatura numarası 3 harf seri + 4 haneli yıl + 9 haneli sıra olmalı.");

        XNamespace inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "TRY" : order.Currency.Trim().ToUpperInvariant();
        var lines = new List<(OrderItem Item, decimal Net, decimal Tax, decimal Gross, decimal UnitNet)>();
        foreach (var item in order.Items)
        {
            var gross = Round(item.UnitPrice!.Value * item.Quantity); var net = Round(gross / (1m + item.VatRate / 100m)); var tax = gross - net; var unitNet = Round(net / item.Quantity, 6);
            lines.Add((item, net, tax, gross, unitNet));
        }
        var netTotal = lines.Sum(x => x.Net); var taxTotal = lines.Sum(x => x.Tax); var grossTotal = lines.Sum(x => x.Gross);
        if (order.Total.HasValue && Math.Abs(order.Total.Value - grossTotal) > 0.02m) throw new InvalidOperationException("Sipariş toplamı ile fatura satırları uyuşmuyor; NES belgesi hazırlanmadı.");

        XElement Party(string title, string taxNumber, string identityScheme, string taxOffice, string address, string district, string city, string email, string phone) =>
            new(cac + "Party",
                new XElement(cac + "PartyIdentification", new XElement(cbc + "ID", new XAttribute("schemeID", identityScheme), taxNumber)),
                new XElement(cac + "PartyName", new XElement(cbc + "Name", title)),
                new XElement(cac + "PostalAddress", new XElement(cbc + "StreetName", address), new XElement(cbc + "CitySubdivisionName", district), new XElement(cbc + "CityName", city), new XElement(cac + "Country", new XElement(cbc + "Name", "Türkiye"))),
                taxOffice.Length == 0 ? null : new XElement(cac + "PartyTaxScheme", new XElement(cac + "TaxScheme", new XElement(cbc + "Name", taxOffice))),
                new XElement(cac + "Contact", phone.Length == 0 ? null : new XElement(cbc + "Telephone", phone), email.Length == 0 ? null : new XElement(cbc + "ElectronicMail", email)));

        var taxSubtotals = lines.GroupBy(x => x.Item.VatRate).Select(group =>
            new XElement(cac + "TaxSubtotal",
                Amount(cbc + "TaxableAmount", group.Sum(x => x.Net), currency),
                Amount(cbc + "TaxAmount", group.Sum(x => x.Tax), currency),
                new XElement(cbc + "Percent", F(group.Key)),
                new XElement(cac + "TaxCategory", new XElement(cac + "TaxScheme", new XElement(cbc + "Name", "KDV"), new XElement(cbc + "TaxTypeCode", "0015"))))).ToArray();
        var invoiceLines = lines.Select((line, index) =>
            new XElement(cac + "InvoiceLine",
                new XElement(cbc + "ID", index + 1),
                new XElement(cbc + "InvoicedQuantity", new XAttribute("unitCode", "C62"), line.Item.Quantity),
                Amount(cbc + "LineExtensionAmount", line.Net, currency),
                new XElement(cac + "TaxTotal",
                    Amount(cbc + "TaxAmount", line.Tax, currency),
                    new XElement(cac + "TaxSubtotal",
                        Amount(cbc + "TaxableAmount", line.Net, currency),
                        Amount(cbc + "TaxAmount", line.Tax, currency),
                        new XElement(cbc + "Percent", F(line.Item.VatRate)),
                        new XElement(cac + "TaxCategory", new XElement(cac + "TaxScheme", new XElement(cbc + "Name", "KDV"), new XElement(cbc + "TaxTypeCode", "0015"))))),
                new XElement(cac + "Item", new XElement(cbc + "Name", line.Item.Title), line.Item.Sku.Length == 0 ? null : new XElement(cac + "SellersItemIdentification", new XElement(cbc + "ID", line.Item.Sku))),
                new XElement(cac + "Price", Amount(cbc + "PriceAmount", line.UnitNet, currency)))).ToArray();

        var root = new XElement(inv + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", cac), new XAttribute(XNamespace.Xmlns + "cbc", cbc),
            new XElement(cbc + "UBLVersionID", "2.1"), new XElement(cbc + "CustomizationID", "TR1.2.1"),
            new XElement(cbc + "ProfileID", kind == NesDocumentKind.EInvoice ? "TEMELFATURA" : "EARSIVFATURA"),
            new XElement(cbc + "ID", documentNumber), new XElement(cbc + "CopyIndicator", "false"), new XElement(cbc + "UUID", uuid),
            new XElement(cbc + "IssueDate", issueAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new XElement(cbc + "IssueTime", issueAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(cbc + "InvoiceTypeCode", "SATIS"), new XElement(cbc + "DocumentCurrencyCode", currency), new XElement(cbc + "LineCountNumeric", order.Items.Count),
            new XElement(cac + "OrderReference", new XElement(cbc + "ID", order.OrderId), new XElement(cbc + "IssueDate", order.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            new XElement(cac + "AccountingSupplierParty", Party(settings.CompanyTitle.Trim(), settings.TaxNumber, settings.TaxNumber.Length == 10 ? "VKN" : "TCKN", settings.TaxOffice.Trim(), settings.Address.Trim(), settings.District.Trim(), settings.City.Trim(), settings.Email.Trim(), settings.Phone.Trim())),
            new XElement(cac + "AccountingCustomerParty", Party(order.BillingName.Trim(), receiverIdentity.Value, receiverIdentity.Scheme, order.CustomerTaxOffice.Trim(), order.BillingAddress.Trim(), order.BillingDistrict.Trim(), order.BillingCity.Trim(), order.BillingEmail.Trim(), order.CustomerPhone.Trim())),
            new XElement(cac + "TaxTotal", Amount(cbc + "TaxAmount", taxTotal, currency), taxSubtotals),
            new XElement(cac + "LegalMonetaryTotal", Amount(cbc + "LineExtensionAmount", netTotal, currency), Amount(cbc + "TaxExclusiveAmount", netTotal, currency), Amount(cbc + "TaxInclusiveAmount", grossTotal, currency), Amount(cbc + "PayableAmount", grossTotal, currency)),
            invoiceLines);
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
        var bytes = Encoding.UTF8.GetBytes(xml); return new(bytes, netTotal, taxTotal, grossTotal, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    static XElement Amount(XName name, decimal value, string currency) => new(name, new XAttribute("currencyID", currency), F(value));
    static string F(decimal value, int decimals = 2) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    static decimal Round(decimal value, int decimals = 2) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}

public sealed class NesDraft
{
    public string Marketplace { get; set; } = ""; public string ShopId { get; set; } = ""; public string OrderId { get; set; } = "";
    public NesDocumentKind Kind { get; set; } public string DocumentNumber { get; set; } = ""; public string LocalUuid { get; set; } = "";
    public string ContentHash { get; set; } = ""; public string State { get; set; } = "Prepared"; public string NesUuid { get; set; } = ""; public string NesDocumentNumber { get; set; } = "";
    public DateTimeOffset IssueAt { get; set; } public DateTimeOffset UpdatedUtc { get; set; } public int Revision { get; set; }
}

public sealed class NesDocumentStore
{
    readonly string connectionString;
    public NesDocumentStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "nes.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS documents(marketplace TEXT NOT NULL,shop TEXT NOT NULL,order_id TEXT NOT NULL,kind INTEGER NOT NULL,document_number TEXT NOT NULL,local_uuid TEXT NOT NULL,content_hash TEXT NOT NULL,state TEXT NOT NULL,nes_uuid TEXT NOT NULL,nes_number TEXT NOT NULL,issue_at TEXT NOT NULL,updated_utc TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(marketplace,shop,order_id));CREATE UNIQUE INDEX IF NOT EXISTS IX_nes_document_number ON documents(document_number);CREATE UNIQUE INDEX IF NOT EXISTS IX_nes_uuid ON documents(nes_uuid) WHERE nes_uuid<>'';CREATE TABLE IF NOT EXISTS sequences(series TEXT NOT NULL,year INTEGER NOT NULL,next_value INTEGER NOT NULL,PRIMARY KEY(series,year));"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public NesDraft Prepare(string marketplace, string shop, string orderId, NesDocumentKind kind, string series, string contentHash)
    {
        series = NesSettingsValidator.ValidateSeries(series); if (new[] { marketplace, shop, orderId, contentHash }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("NES taslağı için sipariş kimliği ve içerik özeti gerekli.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); var existing = Find(connection, transaction, marketplace, shop, orderId);
        if (existing is not null)
        {
            if (existing.State is "Sent" or "Unknown") throw new InvalidOperationException(existing.State == "Sent" ? "Bu sipariş için NES faturası zaten gönderildi." : "Önceki NES gönderiminin sonucu belirsiz; giden faturalarla mutabakat yapmadan tekrar göndermeyin.");
            if (existing.Kind != kind) throw new InvalidOperationException("Bu sipariş için hazırlanmış belge türü değiştirilemez; taslağı açıkça iptal edin.");
            if (existing.ContentHash == contentHash) { transaction.Commit(); return existing; }
            existing.ContentHash = contentHash; existing.UpdatedUtc = DateTimeOffset.UtcNow; existing.Revision++; Put(connection, transaction, existing); transaction.Commit(); return existing;
        }
        var year = DateTime.Now.Year; long next;
        using (var sequence = connection.CreateCommand()) { sequence.Transaction = transaction; sequence.CommandText = "INSERT INTO sequences(series,year,next_value) VALUES($s,$y,2) ON CONFLICT(series,year) DO UPDATE SET next_value=next_value+1 RETURNING next_value-1"; sequence.Parameters.AddWithValue("$s", series); sequence.Parameters.AddWithValue("$y", year); next = (long)sequence.ExecuteScalar()!; }
        var now = DateTimeOffset.Now;
        var draft = new NesDraft { Marketplace = marketplace, ShopId = shop, OrderId = orderId, Kind = kind, DocumentNumber = series + year.ToString("0000") + next.ToString("000000000"), LocalUuid = Guid.NewGuid().ToString(), ContentHash = contentHash, IssueAt = now, UpdatedUtc = now.ToUniversalTime(), Revision = 1 };
        Put(connection, transaction, draft); transaction.Commit(); return draft;
    }

    public void MarkPreviewed(NesDraft draft) => Transition(draft, "Previewed", "", "");
    public void MarkSent(NesDraft draft, string nesUuid, string nesNumber)
    {
        if (!Guid.TryParse(nesUuid, out _)) throw new ArgumentException("Geçerli NES UUID gerekli.");
        Transition(draft, "Sent", nesUuid, nesNumber);
    }
    public void MarkUnknown(NesDraft draft) => Transition(draft, "Unknown", "", "");
    void Transition(NesDraft draft, string state, string uuid, string number)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); var current = Find(connection, transaction, draft.Marketplace, draft.ShopId, draft.OrderId) ?? throw new InvalidOperationException("NES taslağı bulunamadı.");
        if (current.Revision != draft.Revision || current.ContentHash != draft.ContentHash) throw new InvalidOperationException("NES taslağı değişti; ekranı yenileyin.");
        if (current.State == "Sent") throw new InvalidOperationException("Fatura zaten gönderildi; ikinci kez gönderilemez.");
        current.State = state; current.NesUuid = uuid; current.NesDocumentNumber = number; current.UpdatedUtc = DateTimeOffset.UtcNow; current.Revision++; Put(connection, transaction, current); transaction.Commit();
        draft.State = current.State; draft.NesUuid = current.NesUuid; draft.NesDocumentNumber = current.NesDocumentNumber; draft.UpdatedUtc = current.UpdatedUtc; draft.Revision = current.Revision;
    }
    public IReadOnlyList<NesDraft> List() { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT marketplace,shop,order_id,kind,document_number,local_uuid,content_hash,state,nes_uuid,nes_number,issue_at,updated_utc,revision FROM documents ORDER BY updated_utc DESC"; using var reader = command.ExecuteReader(); var rows = new List<NesDraft>(); while (reader.Read()) rows.Add(Read(reader)); return rows; }
    static NesDraft? Find(SqliteConnection connection, SqliteTransaction transaction, string marketplace, string shop, string orderId) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT marketplace,shop,order_id,kind,document_number,local_uuid,content_hash,state,nes_uuid,nes_number,issue_at,updated_utc,revision FROM documents WHERE marketplace=$m AND shop=$s AND order_id=$o"; command.Parameters.AddWithValue("$m", marketplace); command.Parameters.AddWithValue("$s", shop); command.Parameters.AddWithValue("$o", orderId); using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null; }
    static NesDraft Read(SqliteDataReader reader) => new() { Marketplace = reader.GetString(0), ShopId = reader.GetString(1), OrderId = reader.GetString(2), Kind = (NesDocumentKind)reader.GetInt32(3), DocumentNumber = reader.GetString(4), LocalUuid = reader.GetString(5), ContentHash = reader.GetString(6), State = reader.GetString(7), NesUuid = reader.GetString(8), NesDocumentNumber = reader.GetString(9), IssueAt = DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), UpdatedUtc = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Revision = reader.GetInt32(12) };
    static void Put(SqliteConnection connection, SqliteTransaction transaction, NesDraft d) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO documents VALUES($m,$s,$o,$k,$n,$u,$h,$state,$nu,$nn,$issue,$at,$r) ON CONFLICT(marketplace,shop,order_id) DO UPDATE SET kind=excluded.kind,document_number=excluded.document_number,local_uuid=excluded.local_uuid,content_hash=excluded.content_hash,state=excluded.state,nes_uuid=excluded.nes_uuid,nes_number=excluded.nes_number,issue_at=excluded.issue_at,updated_utc=excluded.updated_utc,revision=excluded.revision"; command.Parameters.AddWithValue("$m", d.Marketplace); command.Parameters.AddWithValue("$s", d.ShopId); command.Parameters.AddWithValue("$o", d.OrderId); command.Parameters.AddWithValue("$k", (int)d.Kind); command.Parameters.AddWithValue("$n", d.DocumentNumber); command.Parameters.AddWithValue("$u", d.LocalUuid); command.Parameters.AddWithValue("$h", d.ContentHash); command.Parameters.AddWithValue("$state", d.State); command.Parameters.AddWithValue("$nu", d.NesUuid); command.Parameters.AddWithValue("$nn", d.NesDocumentNumber); command.Parameters.AddWithValue("$issue", d.IssueAt.ToString("O")); command.Parameters.AddWithValue("$at", d.UpdatedUtc.ToString("O")); command.Parameters.AddWithValue("$r", d.Revision); command.ExecuteNonQuery(); }
}
