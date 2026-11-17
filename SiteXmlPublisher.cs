using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The shared site-upload settings that the other MarketXmlSuite programs keep under HKCU.
/// The token is only read here for the duration of one upload; MonoBridge never stores, logs, shows or returns it.
/// </summary>
public interface ISiteUploadSettings
{
    string? UploadToken { get; }
    string UploadUrl { get; }
    string PublicBaseUrl { get; }
}

public sealed class RegistrySiteUploadSettings : ISiteUploadSettings
{
    public const string DefaultKeyPath = @"Software\MarketXmlSuite\FiyatKarsilastirmaPro\marketxml";
    public const string DefaultUploadUrl = "https://www.petshoptedarik.com.tr/marketxml/upload.php";
    public const string DefaultPublicBaseUrl = "https://www.petshoptedarik.com.tr/marketxml/files";
    readonly string keyPath;

    public RegistrySiteUploadSettings(string keyPath = DefaultKeyPath) => this.keyPath = keyPath;

    public string? UploadToken => Read("upload_token") is { Length: > 0 } token ? token : null;
    public string UploadUrl => Read("upload_url") is { Length: > 0 } url ? url : DefaultUploadUrl;
    public string PublicBaseUrl => Read("public_base_url") is { Length: > 0 } url ? url : DefaultPublicBaseUrl;

    string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return (key?.GetValue(name) as string)?.Trim();
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

public class SiteXmlPublishException : InvalidOperationException
{
    public SiteXmlPublishException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The upload may already have reached the site, but what the site now serves does not match the local file.</summary>
public sealed class SiteXmlPublishVerificationException : SiteXmlPublishException
{
    public SiteXmlPublishVerificationException(string message) : base(message) { }
}

public sealed record SiteXmlPublishPreview(
    string Id, string SiteKey, string FileName, string SourcePath, string TargetUrl,
    string Sha256, int ModelCount, long Bytes, DateTime CreatedUtc);

public sealed record SiteXmlPublishReceipt(
    string PreviewId, string PublicUrl, string Sha256, int ModelCount, long Bytes, DateTime CompletedUtc);

/// <summary>
/// Publishes an XML feed to the user's own site (the "own website" channel). A live write, so it follows the same gates as every
/// other write: immutable preview (hash + model count + target) → explicit approval → stale check against the file → upload →
/// read-back verification → receipt. It is never started by a scheduler or the tray; a caller has to pass an approved preview.
/// The token travels in the query string because the site's upload.php expects it there, so the request address is never logged and every
/// message that leaves this class is scrubbed of the token. Redirects are not followed, and the host is pinned.
/// </summary>
public sealed class SiteXmlPublisher : IDisposable
{
    public const string ExpectedHost = "www.petshoptedarik.com.tr";
    public const long MaxBytes = 256L * 1024 * 1024;
    static readonly Regex SafeName = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TokenInText = new("token=[^&\\s\"']+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly string connectionString;
    readonly ISiteUploadSettings settings;
    readonly HttpClient http;

    public SiteXmlPublisher(string? directory = null, ISiteUploadSettings? settings = null, HttpMessageHandler? handler = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        this.settings = settings ?? new RegistrySiteUploadSettings();
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "site-xml-publish.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS site_xml_publish_previews(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS site_xml_publish_attempts(
                preview_id TEXT PRIMARY KEY, status TEXT NOT NULL, detail TEXT NOT NULL, receipt_json TEXT NOT NULL, updated_utc TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => http.Dispose();

    /// <summary>True when the shared token is present. The value itself is never exposed.</summary>
    public bool IsConfigured => settings.UploadToken is not null;

    /// <summary>Reads and checks the local file and the target; nothing is sent anywhere.</summary>
    public SiteXmlPublishPreview Preview(string siteKey, string fileName, string xmlPath)
    {
        var target = ValidateTarget(siteKey, fileName);
        var payload = ReadPayload(xmlPath);
        var preview = new SiteXmlPublishPreview(Guid.NewGuid().ToString("N"), siteKey, fileName, Path.GetFullPath(xmlPath), target,
            Hash(payload), ModelCount(payload), payload.LongLength, DateTime.UtcNow);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO site_xml_publish_previews(id,json) VALUES($id,$json)";
        command.Parameters.AddWithValue("$id", preview.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview));
        command.ExecuteNonQuery();
        return preview;
    }

    public SiteXmlPublishReceipt? Receipt(string previewId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT receipt_json FROM site_xml_publish_attempts WHERE preview_id=$id AND status='Succeeded'";
        command.Parameters.AddWithValue("$id", previewId);
        return command.ExecuteScalar() is string json && json.Length > 0 ? JsonSerializer.Deserialize<SiteXmlPublishReceipt>(json) : null;
    }

    public async Task<SiteXmlPublishReceipt> PublishAsync(SiteXmlPublishPreview preview, bool approved, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!approved) throw new SiteXmlPublishException("Siteye yüklemek için önizleme açıkça onaylanmalı; yükleme yapılmadı.");
        var stored = LoadPreview(preview.Id) ?? throw new SiteXmlPublishException("Yükleme önizlemesi bulunamadı; yeni önizleme alın.");
        if (stored != preview) throw new SiteXmlPublishException("Yükleme önizlemesi değiştirilmiş; yeni önizleme alın.");
        if (Receipt(preview.Id) is not null) throw new SiteXmlPublishException("Bu önizleme daha önce siteye yüklendi.");
        var token = settings.UploadToken ?? throw new SiteXmlPublishException("XML yükleme anahtarı ayarlanmamış; yükleme yapılmadı.");
        if (ValidateTarget(preview.SiteKey, preview.FileName) != preview.TargetUrl)
            throw new SiteXmlPublishException("Yükleme adresi önizlemeden sonra değişti; yeni önizleme alın.");
        var payload = ReadPayload(preview.SourcePath);
        if (Hash(payload) != preview.Sha256 || ModelCount(payload) != preview.ModelCount)
            throw new SiteXmlPublishException("XML dosyası önizlemeden sonra değişti; yeni önizleme alın.");

        SaveAttempt(preview.Id, "Publishing", "", "");
        try
        {
            var uploadUrl = new Uri(settings.UploadUrl);
            var query = $"?token={Uri.EscapeDataString(token)}&market={Uri.EscapeDataString(preview.SiteKey)}&name={Uri.EscapeDataString(preview.FileName)}";
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new("application/xml");
            using var response = await http.PostAsync(new Uri(uploadUrl.GetLeftPart(UriPartial.Path) + query), content, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SiteXmlPublishException($"Site yüklemeyi kabul etmedi (HTTP {(int)response.StatusCode}).");
            var returned = ReturnedAddress(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            if (!Uri.TryCreate(uploadUrl, returned, out var returnedUri) || returnedUri.AbsoluteUri != new Uri(preview.TargetUrl).AbsoluteUri)
                throw new SiteXmlPublishException("Sunucu beklenmeyen bir XML adresi döndürdü; yüklenen dosya doğrulanamadı.");

            using var verify = new HttpRequestMessage(HttpMethod.Get, new Uri(preview.TargetUrl + "?_verify=" + preview.Sha256[..16]));
            verify.Headers.CacheControl = new() { NoCache = true };
            verify.Headers.UserAgent.ParseAdd("MonoBridge/1.0");
            using var download = await http.SendAsync(verify, cancellation).ConfigureAwait(false);
            if (!download.IsSuccessStatusCode) throw new SiteXmlPublishVerificationException($"Yüklenen XML geri okunamadı (HTTP {(int)download.StatusCode}).");
            var remote = await download.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            var remoteCount = -1;
            try { remoteCount = ModelCount(remote); } catch (SiteXmlPublishException) { }
            if (Hash(remote) != preview.Sha256 || remoteCount != preview.ModelCount)
                throw new SiteXmlPublishVerificationException("Yüklenen XML yerel dosyayla eşleşmiyor.");

            var receipt = new SiteXmlPublishReceipt(preview.Id, preview.TargetUrl, preview.Sha256, preview.ModelCount, preview.Bytes, DateTime.UtcNow);
            SaveAttempt(preview.Id, "Succeeded", "", JsonSerializer.Serialize(receipt));
            return receipt;
        }
        catch (SiteXmlPublishException error)
        {
            var message = Scrub(error.Message, token);
            SaveAttempt(preview.Id, "Failed", message, "");
            throw error is SiteXmlPublishVerificationException ? new SiteXmlPublishVerificationException(message) : new SiteXmlPublishException(message);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or UriFormatException)
        {
            var message = Scrub(error.Message, token);
            SaveAttempt(preview.Id, "Failed", message, "");
            throw new SiteXmlPublishException("Siteye yükleme başarısız: " + message);
        }
    }

    string ValidateTarget(string siteKey, string fileName)
    {
        if (!SafeName.IsMatch(siteKey ?? "") || !SafeName.IsMatch(fileName ?? "")) throw new SiteXmlPublishException("Site veya XML dosya adı güvenli değil.");
        if (!Uri.TryCreate(settings.UploadUrl, UriKind.Absolute, out var upload) || !Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var baseUrl) ||
            upload.Scheme != Uri.UriSchemeHttps || baseUrl.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(upload.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase) || !string.Equals(baseUrl.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase))
            throw new SiteXmlPublishException("XML yayın adresi güvenli değil; yalnız " + ExpectedHost + " üzerinde https kabul edilir.");
        return baseUrl.AbsoluteUri.TrimEnd('/') + "/" + siteKey + "/" + fileName + ".xml";
    }

    static byte[] ReadPayload(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new SiteXmlPublishException("XML dosyası bulunamadı.");
        if (info.Length > MaxBytes) throw new SiteXmlPublishException("XML dosyası çok büyük; yükleme yapılmadı.");
        return File.ReadAllBytes(path);
    }

    /// <summary>Root must be Models or ArrayOfModel (the feed the site serves) and must hold at least one Model; an empty feed would blank the live file.</summary>
    static int ModelCount(byte[] payload)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException error) { throw new SiteXmlPublishException("XML doğrulanamadı.", error); }
        var root = document.Root;
        if (root is null || root.Name.NamespaceName.Length > 0 || (root.Name.LocalName != "Models" && root.Name.LocalName != "ArrayOfModel"))
            throw new SiteXmlPublishException("XML kök alanı Models veya ArrayOfModel değil; bu dosya siteye yüklenmez.");
        var count = root.Elements("Model").Count();
        if (count == 0) throw new SiteXmlPublishException("XML hiç Model içermiyor; boş dosya siteye yüklenmez.");
        return count;
    }

    static string ReturnedAddress(string body)
    {
        body = body.Trim();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "url", "public_url" })
                    if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return text;
                return "";
            }
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : body;
        }
        catch (JsonException) { return body; }
    }

    static string Hash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    /// <summary>Removes the token (raw and URL-encoded) and any token= query value from text that may leave this class.</summary>
    static string Scrub(string text, string token)
    {
        foreach (var form in new[] { token, Uri.EscapeDataString(token), WebUtility.UrlEncode(token) })
            if (!string.IsNullOrEmpty(form)) text = text.Replace(form, "***", StringComparison.Ordinal);
        return TokenInText.Replace(text, "token=***");
    }

    SiteXmlPublishPreview? LoadPreview(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM site_xml_publish_previews WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SiteXmlPublishPreview>(json) : null;
    }

    void SaveAttempt(string previewId, string status, string detail, string receiptJson)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO site_xml_publish_attempts(preview_id,status,detail,receipt_json,updated_utc) VALUES($id,$status,$detail,$receipt,$updated) " +
            "ON CONFLICT(preview_id) DO UPDATE SET status=excluded.status,detail=excluded.detail,receipt_json=excluded.receipt_json,updated_utc=excluded.updated_utc";
        command.Parameters.AddWithValue("$id", previewId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$receipt", receiptJson);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
}
