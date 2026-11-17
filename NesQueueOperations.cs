using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrMarketplaceHubDesktop;

public interface INesQueueOperations
{
    Task<NesPreparedInvoice> PrepareAsync(NesQueueItem item, OrderSnapshot order, CancellationToken token);
    Task<NesUploadResult> SendAsync(NesQueueItem item, OrderSnapshot order, NesPreparedInvoice prepared, CancellationToken token);
    Task<NesMatchedInvoice?> FindRemoteAsync(NesQueueItem item, CancellationToken token);
    Task<NesDownloadedInvoice> DownloadAsync(NesQueueItem item, CancellationToken token);
    Task UploadMarketplaceAsync(NesQueueItem item, NesDownloadedInvoice pdf, CancellationToken token);
}

public sealed record NesPreparedInvoice(string DraftId, byte[] Xml, string LocalDocumentNumber);
public sealed record NesMatchedInvoice(string Uuid, string Number, string Status, DateTimeOffset? IssuedAt);
public sealed record NesDownloadedInvoice(string Path, string Sha256, byte[] Bytes);
public sealed class NesUnknownResultException(string message) : Exception(message);
public sealed class NesQueueValidationException(string message) : Exception(message);
public sealed class NesQueueHttpException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class NesQueueOperations : INesQueueOperations, IDisposable
{
    readonly string directory;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly NesDocumentStore documents;
    readonly NesSettingsStore settingsStore;
    readonly TrendyolSettingsStore trendyolSettings;

    public NesQueueOperations(string? directory = null, HttpClient? httpClient = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(this.directory);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        ownsHttp = httpClient is null;
        documents = new NesDocumentStore(this.directory);
        settingsStore = new NesSettingsStore(Path.Combine(this.directory, "nes.bin"));
        trendyolSettings = new TrendyolSettingsStore(Path.Combine(this.directory, "trendyol.bin"));
    }

    public Task<NesPreparedInvoice> PrepareAsync(NesQueueItem item, OrderSnapshot order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = Settings();
        ValidateFingerprint(item, order, settings);
        var series = item.Kind == NesDocumentKind.EInvoice ? settings.EInvoiceSeries : settings.EArchiveSeries;
        var draft = documents.Prepare(item.Marketplace, item.ShopId, item.OrderId, item.Kind, series, item.OrderFingerprint);
        var ubl = NesUblBuilder.Build(settings, order, item.Kind, draft.DocumentNumber, Guid.Parse(draft.LocalUuid), draft.IssueAt);
        return Task.FromResult(new NesPreparedInvoice(draft.LocalUuid, ubl.Xml, draft.DocumentNumber));
    }

    public async Task<NesUploadResult> SendAsync(NesQueueItem item, OrderSnapshot order, NesPreparedInvoice prepared, CancellationToken token)
    {
        var current = await PrepareAsync(item, order, token);
        var settings = Settings();
        try
        {
            var result = await new NesConnection(http).UploadAsync(settings, item.Kind, current.Xml, order, item.ReceiverAlias, true, token);
            var draft = documents.List().Single(x => x.Marketplace == item.Marketplace && x.ShopId == item.ShopId && x.OrderId == item.OrderId);
            documents.MarkSent(draft, result.Uuid, result.DocumentNumber);
            return result;
        }
        catch (HttpRequestException ex) { MarkDraftUnknown(item); throw new NesUnknownResultException("NES gönderiminin sonucu doğrulanamadı: " + MarketplaceConnectionStore.Redact(ex.Message)); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { MarkDraftUnknown(item); throw new NesUnknownResultException("NES gönderimi zaman aşımına uğradı; uzaktan mutabakat gerekli. " + MarketplaceConnectionStore.Redact(ex.Message)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { MarkDraftUnknown(item); throw; }
    }

    /// The request may have reached NES: the local draft must stop looking sendable, for the queue and for the manual invoice dialog alike.
    void MarkDraftUnknown(NesQueueItem item)
    {
        var draft = documents.List().SingleOrDefault(x => x.Marketplace == item.Marketplace && x.ShopId == item.ShopId && x.OrderId == item.OrderId);
        if (draft is not null) documents.MarkUnknown(draft);
    }

    public async Task<NesMatchedInvoice?> FindRemoteAsync(NesQueueItem item, CancellationToken token)
    {
        var rows = await new NesConnection(http).ReadOutgoingAsync(Settings(), item.Kind, token);
        var sourceId = item.Marketplace + ":" + item.ShopId + ":" + item.OrderId;
        var matches = rows.Where(x =>
            x.Kind == item.Kind &&
            (x.SourceAppRecordId.Equals(sourceId, StringComparison.OrdinalIgnoreCase) ||
             x.MarketplaceOrderNumber == item.OrderId && x.MarketplaceSupplierId == item.ShopId)).ToList();
        if (matches.Count > 1) throw new NesQueueValidationException("NES'te aynı siparişle eşleşen birden fazla fatura bulundu; manuel mutabakat gerekli.");
        return matches.Count == 0 ? null : new(matches[0].Uuid, matches[0].DocumentNumber, matches[0].Status, matches[0].IssueDate);
    }

    public async Task<NesDownloadedInvoice> DownloadAsync(NesQueueItem item, CancellationToken token)
    {
        if (!Guid.TryParse(item.NesUuid, out _)) throw new NesQueueValidationException("PDF için geçerli NES UUID bulunamadı.");
        var bytes = await new NesConnection(http).DownloadPdfAsync(Settings(), item.Kind, item.NesUuid, token);
        var folder = Path.Combine(directory, "nes-invoices"); Directory.CreateDirectory(folder);
        var raw = $"{item.Marketplace}-{item.ShopId}-{item.OrderId}-{item.InvoiceNumber}";
        var safe = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe.Length > 140) safe = safe[..140];
        var path = Path.Combine(folder, safe + ".pdf"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(path, Convert.ToHexString(SHA256.HashData(bytes)), bytes);
    }

    public async Task UploadMarketplaceAsync(NesQueueItem item, NesDownloadedInvoice pdf, CancellationToken token)
    {
        if (!item.Marketplace.Equals("Trendyol", StringComparison.OrdinalIgnoreCase)) throw new NesQueueValidationException("Bu pazaryeri için otomatik PDF yükleme desteklenmiyor.");
        if (!long.TryParse(item.PackageId, out var packageId) || packageId <= 0) throw new NesQueueValidationException("Trendyol paket kimliği geçersiz.");
        var settings = trendyolSettings.Load() ?? throw new NesQueueValidationException("Trendyol bağlantı ayarları kayıtlı değil.");
        var bytes = pdf.Bytes.Length > 0 ? pdf.Bytes : File.Exists(pdf.Path) ? await File.ReadAllBytesAsync(pdf.Path, token) : throw new NesQueueValidationException("NES PDF dosyası bulunamadı.");
        await new TrendyolInvoiceClient(settings, http).UploadPdfAsync(packageId, item.InvoiceNumber, DateTimeOffset.UtcNow, bytes, token);
    }

    NesSettings Settings() => settingsStore.Load() ?? throw new NesQueueValidationException("NES bağlantı ve firma ayarları kayıtlı değil.");

    static void ValidateFingerprint(NesQueueItem item, OrderSnapshot order, NesSettings settings)
    {
        var plan = NesQueuePlanner.Build([order], settings, _ => item.ReceiverAlias, item.UploadMarketplace);
        var request = plan.Rows.Single().Request;
        if (request is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(request.OrderFingerprint), Convert.FromHexString(item.OrderFingerprint)))
            throw new NesQueueValidationException("Sipariş, kuyruk onayından sonra değişti; yeniden önizleme ve onay gerekli.");
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
