using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TrMarketplaceHubDesktop;

public sealed class NesQueueProcessor
{
    readonly NesInvoiceQueueStore queue;
    readonly OrdersStore orders;
    readonly AuditStore audit;
    readonly INesQueueOperations operations;
    readonly TimeProvider clock;

    public NesQueueProcessor(NesInvoiceQueueStore queue, OrdersStore orders, AuditStore audit, INesQueueOperations operations, TimeProvider clock)
    { this.queue = queue; this.orders = orders; this.audit = audit; this.operations = operations; this.clock = clock; }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!queue.TryClaimNext(now, TimeSpan.FromMinutes(2), out var item)) return false;
        var sendInFlight = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = orders.Find(item!.Marketplace, item.ShopId, item.OrderId)
                ?? throw new NesQueueValidationException("Kuyruktaki sipariş artık bulunamıyor.");
            switch (item.Step)
            {
                case NesQueueStep.PrepareDocument:
                    await operations.PrepareAsync(item, order, cancellationToken);
                    queue.Advance(item.Id, item.LeaseToken, NesQueueStep.SendInvoice, NesQueueState.Ready, clock.GetUtcNow());
                    break;
                case NesQueueStep.SendInvoice:
                    cancellationToken.ThrowIfCancellationRequested();
                    sendInFlight = true;
                    var upload = await operations.SendAsync(item, order, new NesPreparedInvoice(item.Id, [], ""), cancellationToken);
                    sendInFlight = false;
                    queue.RecordRemoteAndAdvance(item.Id, item.LeaseToken, upload.Uuid, upload.DocumentNumber, NesQueueStep.AwaitNumber, clock.GetUtcNow());
                    break;
                case NesQueueStep.AwaitNumber:
                    var matched = await operations.FindRemoteAsync(item, cancellationToken);
                    if (matched is null) { queue.ScheduleRetry(item.Id, item.LeaseToken, TimeSpan.FromMinutes(1), "NES fatura numarası henüz hazır değil.", clock.GetUtcNow()); break; }
                    orders.AttachNesInvoice(item.Marketplace, item.ShopId, item.OrderId, item.Kind == NesDocumentKind.EInvoice ? "e-Fatura" : "e-Arşiv", matched.Uuid, matched.Number, matched.Status, matched.IssuedAt);
                    queue.RecordRemoteAndAdvance(item.Id, item.LeaseToken, matched.Uuid, matched.Number, NesQueueStep.DownloadPdf, clock.GetUtcNow());
                    break;
                case NesQueueStep.DownloadPdf:
                    var pdf = await operations.DownloadAsync(item, cancellationToken);
                    queue.RecordPdfAndAdvance(item.Id, item.LeaseToken, pdf.Path, pdf.Sha256, item.UploadMarketplace ? NesQueueStep.UploadMarketplace : NesQueueStep.Finish, clock.GetUtcNow());
                    break;
                case NesQueueStep.UploadMarketplace:
                    var downloaded = new NesDownloadedInvoice(item.PdfPath, item.PdfSha256, []);
                    await operations.UploadMarketplaceAsync(item, downloaded, cancellationToken);
                    queue.Advance(item.Id, item.LeaseToken, NesQueueStep.Finish, NesQueueState.Ready, clock.GetUtcNow());
                    break;
                case NesQueueStep.Finish:
                    queue.Complete(item.Id, item.LeaseToken, clock.GetUtcNow());
                    break;
                default: throw new NesQueueValidationException("Bilinmeyen NES kuyruk adımı.");
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled send may already have reached NES. Releasing it would re-send a legally binding invoice on the next launch,
            // so it is parked as Unknown (same as an expired send lease) and needs a remote check first.
            if (sendInFlight) queue.MarkUnknown(item!.Id, item.LeaseToken, "NES gönderimi sırasında uygulama durduruldu; faturanın gidip gitmediği bilinmiyor. Yeniden göndermeden önce NES'te kontrol edin.", clock.GetUtcNow());
            else queue.Release(item!.Id, item.LeaseToken, clock.GetUtcNow());
            throw;
        }
        catch (NesUnknownResultException ex)
        {
            queue.MarkUnknown(item!.Id, item.LeaseToken, ex.Message, clock.GetUtcNow()); return true;
        }
        catch (Exception ex) when (IsBlocked(ex))
        {
            queue.Block(item!.Id, item.LeaseToken, ex.Message, clock.GetUtcNow()); return true;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, item!.AttemptCount))));
            queue.ScheduleRetry(item.Id, item.LeaseToken, delay, ex.Message, clock.GetUtcNow()); return true;
        }
        catch (Exception ex)
        {
            queue.Block(item!.Id, item.LeaseToken, ex.Message, clock.GetUtcNow()); return true;
        }
    }

    static bool IsBlocked(Exception ex) => ex is NesQueueValidationException || ex is NesQueueHttpException http && http.StatusCode is 400 or 401 or 403 or 404 or 409 or 422;
    static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutException or TaskCanceledException || ex is NesQueueHttpException http && http.StatusCode is 429 or 500 or 502 or 503 or 504;
}
