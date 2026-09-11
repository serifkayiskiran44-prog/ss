using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record FxQuote(string Currency, decimal TryPerUnit, DateTime RateDate, DateTimeOffset FetchedUtc, string Kind);

/// <summary>Reads published indicative rates; the supplied HttpClient must disable automatic redirects.</summary>
public sealed class TcmbRates(HttpClient client)
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly Uri Endpoint = new("https://www.tcmb.gov.tr/kurlar/today.xml");

    public async Task<FxQuote> FetchAsync(string currency, string kind = "ForexSelling", CancellationToken cancellationToken = default)
    {
        currency = Validate(currency, kind);
        cancellationToken.ThrowIfCancellationRequested();
        if (currency == "TRY") return new(currency, 1m, DateTime.UtcNow.Date, DateTimeOffset.UtcNow, kind);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("TCMB kur isteği yönlendirildi; kur alınamadı.");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != Endpoint)
            throw new HttpRequestException("TCMB kur isteğinin adresi değişti; kur alınamadı.");
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new InvalidDataException("TCMB kur yanıtı izin verilen boyutu aşıyor.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("TCMB kur yanıtı izin verilen boyutu aşıyor.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var text = new StreamReader(buffer, System.Text.Encoding.UTF8, true);
        return Parse(await text.ReadToEndAsync(timeout.Token).ConfigureAwait(false), currency, kind, DateTimeOffset.UtcNow);
    }

    public static FxQuote Parse(string xml, string currency, string kind, DateTimeOffset now)
    {
        currency = Validate(currency, kind);
        if (currency == "TRY") return new(currency, 1m, now.UtcDateTime.Date, now, kind);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxBytes)
            throw new InvalidDataException("TCMB kur XML verisi boş veya fazla büyük.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "Tarih_Date") throw new InvalidDataException("TCMB kur XML biçimi geçersiz.");
            if (!DateTime.TryParseExact((string?)root.Attribute("Tarih"), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && !DateTime.TryParseExact((string?)root.Attribute("Date"), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                throw new InvalidDataException("TCMB kur tarihi okunamadı.");
            var today = now.UtcDateTime.Date;
            if (date < today.AddDays(-7) || date > today.AddDays(1))
                throw new InvalidDataException($"TCMB kur tarihi güncel değil: {date:dd.MM.yyyy}. Kur kullanılmadı.");
            var row = root.Elements("Currency").FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("Kod"), currency, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)e.Attribute("CurrencyCode"), currency, StringComparison.OrdinalIgnoreCase));
            if (row == null) throw new InvalidDataException($"TCMB verisinde {currency} kuru bulunamadı.");
            var unit = Positive((string?)row.Element("Unit"), "kur birimi");
            var published = Positive((string?)row.Element(kind), $"{currency} {kind}");
            var perUnit = published / unit;
            if (perUnit <= 0) throw new InvalidDataException("TCMB kuru hesaplanamayacak kadar küçük.");
            return new(currency, perUnit, date, now, kind);
        }
        catch (XmlException ex) { throw new InvalidDataException("TCMB kur XML verisi geçersiz veya güvenli değil.", ex); }
        catch (OverflowException ex) { throw new InvalidDataException("TCMB kur değeri hesaplama sınırlarını aşıyor.", ex); }
    }

    private static decimal Positive(string? value, string label)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"TCMB {label} değeri eksik veya geçersiz.");
        return parsed;
    }

    private static string Validate(string currency, string kind)
    {
        if (kind is not ("ForexSelling" or "ForexBuying")) throw new ArgumentException("Kur türü ForexSelling veya ForexBuying olmalıdır.", nameof(kind));
        currency = currency?.Trim().ToUpperInvariant() ?? "";
        if (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z')) throw new ArgumentException("Üç harfli para birimi kodu gereklidir.", nameof(currency));
        return currency;
    }
}
