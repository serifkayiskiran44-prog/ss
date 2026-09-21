using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public static class OrderExcelHeaders
{
    static readonly Dictionary<string,string[]> Aliases=new(StringComparer.OrdinalIgnoreCase)
    {
        ["OrderId"]=["Sipariş Numarası","Sipariş No","Order ID"], ["PlatformReference"]=["Platform Referans No","No"], ["OrderDate"]=["Tarih","Sipariş Tarihi"], ["AddedAt"]=["Entegraya Eklenme Tarihi","Entegrasyona Eklenme Tarihi"],
        ["CustomerName"]=["Müşteri","İsim Soyisim","Müşteri İsmi"], ["CustomerId"]=["Müşteri Id","Müşteri ID"], ["CustomerCode"]=["Müşteri Kodu"], ["CustomerEmail"]=["E-Mail","Email"], ["CustomerPhone"]=["Cep Telefonu","Müşteri Telefon"],
        ["PaymentStatus"]=["Ödeme Durumu"], ["PaymentType"]=["Ödeme Tipi","Ödeme Adı","Pazaryeri Ödeme Tipi"], ["RawStatus"]=["Durumu","Sipariş Durumu","Pazaryeri Durumu"], ["Currency"]=["Para Birimi","Döviz"],
        ["Total"]=["Genel Toplam","Sipariş Toplam","Sipariş Toplamı"], ["NetTotal"]=["Sipariş Net","Tutar"], ["VatTotal"]=["Kdv Tutarı","KDV Tutarı"], ["DiscountTotal"]=["Toplam İndirim","İndirim Tutarı"], ["CommissionTotal"]=["Komisyon","Komisyon Tutarı"], ["UndiscountedTotal"]=["İndirimsiz Genel Toplam","İndirimsiz Fiyat"], ["SellerNote"]=["Satıcı Notu","Not"],
        ["BillingName"]=["Fatura İsmi","Müşteri Fatura İsmi"], ["BillingAddress"]=["Fatura Adresi"], ["BillingCountry"]=["Fatura Ülkesi"], ["BillingCity"]=["Fatura İli"], ["BillingDistrict"]=["Fatura İlçesi"], ["TaxNumber"]=["Vergi Numarası","TC No"], ["CustomerTaxOffice"]=["Vergi Dairesi"], ["InvoiceType"]=["Fatura Tipi"], ["InvoiceNumber"]=["Fatura No"], ["InvoiceUrl"]=["Fatura Url","Fatura URL"],
        ["ShippingName"]=["Müşteri Sevk İsmi","Sevk İsmi","Teslim Alan"], ["ShippingAddress"]=["Sevk Adresi"], ["ShippingCountry"]=["Sevk Ülkesi"], ["ShippingCity"]=["Sevk İli"], ["ShippingDistrict"]=["Sevk İlçesi"], ["ShippingPhone"]=["Sevk Telefon","Sevk GSM"],
        ["Carrier"]=["Kargo Firması","Kargo Şirketi"], ["TrackingNumber"]=["Kargo Takip Kodu","Kargo Kodu","Kargo Kodu2"], ["PackageCode"]=["Paket Kodu","Kargo Paket Kodu"], ["PaymentParty"]=["Kargo Ödemesi"], ["DeliveryType"]=["Teslimat Tipi"],
        ["Sku"]=["Ürün Kodu","Model No","SKU"], ["ProductId"]=["Ürün Id","Ürün ID"], ["MarketplaceProductCode"]=["Mağaza Ürün Kodu","Mağaza Ürünü"], ["Barcode"]=["Barkod","GTIN"], ["Name"]=["Ürün İsmi","Ürün Adı"], ["InvoiceTitle"]=["Fatura Ürün İsmi"], ["Quantity"]=["Adet","Miktar"], ["NetUnitPrice"]=["Fiyat","Net Fiyat","KDV Hariç Fiyat"], ["UnitPrice"]=["KDV Dahil Fiyat","Sipariş Detay Fiyat","Döviz Fiyatı"], ["VatRate"]=["Kdv","KDV","KDV Oranı","KDV %"], ["ItemDiscount"]=["İndirim","Ürün İndirim","Ürün İndirimi"], ["ItemCommission"]=["Ürün Komisyonu","Komisyon Oranı"], ["PurchasePrice"]=["Alış Fiyatı","buying_price"]
    };

    public static IReadOnlyList<string> For(string field)=>Aliases.GetValueOrDefault(field,[field]);

    public static bool Matches(string header,string alias)
    {
        var left=Simplify(header);var right=Simplify(alias);
        if(left==right)return true;
        var pattern="^"+Regex.Escape(left).Replace("\\?","[a-z0-9]*")+"$";
        return Regex.IsMatch(right,pattern,RegexOptions.CultureInvariant);
    }

    public static string Suggest(string field,IEnumerable<ExcelSheetColumn> columns)
        =>columns.FirstOrDefault(c=>For(field).Any(a=>Matches(c.Header,a)))?.Letter??"";

    static string Simplify(string value)
    {
        var input=(value??"").Trim().ToLowerInvariant().Replace('ı','i').Normalize(NormalizationForm.FormD);
        var builder=new StringBuilder();bool space=false;
        foreach(var c in input)
        {
            if(CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark)continue;
            if(c=='�'){builder.Append('?');space=false;continue;}
            if(char.IsLetterOrDigit(c)){builder.Append(c);space=false;continue;}
            if(char.IsWhiteSpace(c)&&builder.Length>0&&!space){builder.Append(' ');space=true;}
        }
        return builder.ToString().Trim();
    }
}
