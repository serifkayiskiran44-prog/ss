using ClosedXML.Excel;
using System.IO;

namespace TrMarketplaceHubDesktop;

public static class OrderExcelExport
{
    public static void WriteDetailed(string path,IReadOnlyCollection<OrderSnapshot> orders)
    {
        if(orders.Count==0)throw new InvalidOperationException("Excel'e aktarılacak sipariş yok.");
        var full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);var temporary=Path.Combine(Path.GetDirectoryName(full)!,Path.GetFileNameWithoutExtension(full)+"."+Guid.NewGuid().ToString("N")+Path.GetExtension(full));
        try
        {
            using var book=new XLWorkbook();var sheet=book.AddWorksheet("Siparişler");
            string[] headers=["Pazaryeri","Mağaza","Sipariş Numarası","Platform Referans No","Sipariş Tarihi","Sipariş Durumu","Müşteri","Müşteri ID","E-Posta","Telefon","Ödeme Durumu","Ödeme Tipi","Fatura No","Fatura Tipi","Fatura İsmi","Fatura Adresi","Fatura İli","Fatura İlçesi","Sevk İsmi","Sevk Adresi","Sevk İli","Sevk İlçesi","Kargo Firması","Takip No","Net Tutar","KDV Tutarı","İndirim Tutarı","Komisyon Tutarı","Genel Toplam","Para Birimi","SKU","Barkod","Ürün Adı","Adet","Net Birim Fiyat","KDV %","KDV Dahil Fiyat","Fatura Ülkesi","Fatura URL","Fatura Durumu","Vergi Dairesi","Sevk Ülkesi","Sevk Telefonu","Paket Kodu","Kargo Ödemesi","Teslimat Tipi","Ürün ID","Mağaza Ürün Kodu","Fatura Ürün İsmi","Alış Fiyatı","Ürün İndirimi","Ürün Komisyonu","İndirimsiz Genel Toplam","Satıcı Notu"];
            for(var col=0;col<headers.Length;col++)sheet.Cell(1,col+1).Value=headers[col];
            var row=2;
            foreach(var order in orders)
            {
                var totals=OrderTotalsCalculator.Calculate(order);var shipment=order.Shipments.FirstOrDefault();var items=order.Items.Count>0?order.Items:[new OrderItem()];
                foreach(var item in items)
                {
                    object?[] values=[order.Marketplace,order.ShopId,order.OrderId,order.PlatformReference,order.OrderDate?.ToLocalTime().DateTime,order.StatusLabel,order.CustomerName,order.CustomerId,order.CustomerEmail,order.CustomerPhone,order.PaymentStatus,order.PaymentType,order.InvoiceNumber,order.InvoiceType,order.BillingName,order.BillingAddress,order.BillingCity,order.BillingDistrict,order.ShippingName,order.ShippingAddress,order.ShippingCity,order.ShippingDistrict,shipment?.Carrier,shipment?.TrackingNumber,totals.Net,totals.Vat,totals.Discount,totals.Commission,totals.Gross,order.Currency,item.Sku,item.Barcode,item.Title,item.Quantity,item.NetUnitPrice,item.VatRate,item.UnitPrice,order.BillingCountry,order.InvoiceUrl,order.InvoiceStatus,order.CustomerTaxOffice,order.ShippingCountry,order.ShippingPhone,shipment?.PackageCode,shipment?.PaymentParty,shipment?.DeliveryType,item.ProductId,item.MarketplaceProductCode,item.InvoiceTitle,item.PurchasePrice,item.DiscountAmount,item.CommissionAmount,order.UndiscountedTotal,order.SellerNote];
                    for(var col=0;col<values.Length;col++)Set(sheet.Cell(row,col+1),values[col]);row++;
                }
            }
            var range=sheet.Range(1,1,row-1,headers.Length);range.CreateTable();sheet.SheetView.FreezeRows(1);sheet.Row(1).Style.Font.Bold=true;sheet.Columns().AdjustToContents(8,35);book.SaveAs(temporary);File.Move(temporary,full,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    static void Set(IXLCell cell,object? value)
    {
        switch(value)
        {
            case null:return;case decimal number:cell.Value=number;break;case int number:cell.Value=number;break;case DateTime date:cell.Value=date;cell.Style.DateFormat.Format="dd.MM.yyyy HH:mm";break;default:cell.Value=value.ToString()??"";break;
        }
    }
}
