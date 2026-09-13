using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>Whether a source has a saved credential — presence only, never the values; unreadable when the saved blob cannot be opened on this machine.</summary>
public enum CredentialPresence { None, Saved, Unreadable }

/// <summary>A source's credential state as the health surfaces show it (#892): the words a person reads, never a user name, a password or a header.</summary>
public sealed record CredentialVerdict(string State, string Word, string Detail, SeverityLevel Level)
{
    public bool Blocks => State is SourceCredentialHealth.Missing or SourceCredentialHealth.Invalid or SourceCredentialHealth.Unreadable;
}

/// <summary>
/// Source credential health (#892). A feed either needs no credential, has one that works, has none while the
/// server asks for one, has one the server rejects, or has one this machine can no longer open (the encrypted
/// blob is bound to the user profile, so a copied data folder or a rotated profile needs the credential entered
/// again). The state is decided from two facts only — whether a credential is saved (presence, never its value)
/// and what the last real request answered (the probe's or the download's HTTP status) — and it is persisted on
/// the source as a word, so it survives a restart and the health list can show it before the next probe. The
/// download entry points fail fast on it: a 401/403 becomes a typed refusal that says whether a credential was
/// sent, so the operator learns "enter one" or "renew it" instead of a bare status code.
/// </summary>
public static class SourceCredentialHealth
{
    public const string Unknown = "UNKNOWN";
    public const string NotNeeded = "NOT_NEEDED";
    public const string Valid = "VALID";
    public const string Missing = "MISSING";
    public const string Invalid = "INVALID";
    public const string Unreadable = "UNREADABLE";

    /// <summary>Presence from a loaded credential: a user name or a password saved counts; an empty record is none.</summary>
    public static CredentialPresence PresenceOf(XmlAuth? auth) => auth is null ? CredentialPresence.None : auth.User.Length > 0 || auth.Password.Length > 0 ? CredentialPresence.Saved : CredentialPresence.None;

    /// <summary>The state after a real request answered: 401/403 without a saved credential is missing, with one is rejected; a success with one is valid, without one needs none; other answers say nothing about the credential and keep the presence.</summary>
    public static string Evaluate(CredentialPresence presence, int? httpStatus, string healthState)
    {
        if (presence == CredentialPresence.Unreadable) return Unreadable;
        var state = (healthState ?? "").Trim().ToUpperInvariant();
        if (httpStatus is 401 or 403 || state == "AUTH_ERROR") return presence == CredentialPresence.Saved ? Invalid : Missing;
        if (state == "HEALTHY") return presence == CredentialPresence.Saved ? Valid : NotNeeded;
        return presence == CredentialPresence.Saved ? Valid + "?" : Unknown;
    }

    /// <summary>The state after the download itself was refused with a credential sent or not.</summary>
    public static string AfterRefusal(bool credentialSent) => credentialSent ? Invalid : Missing;

    /// <summary>The state after the operator saved a credential: unknown until a request answers, but no longer missing or unreadable.</summary>
    public static string AfterSave(XmlAuth auth) => PresenceOf(auth) == CredentialPresence.Saved ? Valid + "?" : NotNeeded + "?";

    /// <summary>Whether a scheduled read should not even try: the last answer said the credential is missing, rejected or unreadable, and nothing changed since.</summary>
    public static bool ShouldFailFast(string? state) => Describe(state).Blocks;

    public static CredentialVerdict Describe(string? state)
    {
        var s = (state ?? "").Trim().ToUpperInvariant();
        return s switch
        {
            NotNeeded => new(NotNeeded, "gerekmiyor", "Kaynak kimlik bilgisi istemiyor.", SeverityLevel.Success),
            NotNeeded + "?" => new(NotNeeded + "?", "kayıtlı değil", "Kimlik bilgisi girilmedi; kaynak istemezse sorun yok.", SeverityLevel.Info),
            Valid => new(Valid, "kayıtlı · doğrulandı", "Son istek kayıtlı kimlik bilgisiyle kabul edildi.", SeverityLevel.Success),
            Valid + "?" => new(Valid + "?", "kayıtlı · henüz doğrulanmadı", "Bir sonraki okuma veya erişim kontrolü doğrular.", SeverityLevel.Info),
            Missing => new(Missing, "eksik", "Kaynak kimlik doğrulaması istiyor; kayıtlı kullanıcı adı ve şifre yok. Kaynak ayarlarında girin.", SeverityLevel.Blocking),
            Invalid => new(Invalid, "reddedildi · yenileme gerekli", "Kayıtlı kimlik bilgisi sunucu tarafından reddedildi (HTTP 401/403). Yeni değeri girip kaydedin.", SeverityLevel.Blocking),
            Unreadable => new(Unreadable, "okunamadı · yeniden kaydedin", "Kayıtlı kimlik bilgisi bu makinede açılamadı; yeniden girip kaydedin.", SeverityLevel.Blocking),
            _ => new(Unknown, "bilinmiyor", "Henüz bir istek yanıtlamadı.", SeverityLevel.Info),
        };
    }
}

/// <summary>A download refused for a credential: the status and whether a credential was sent, never the credential itself.</summary>
public sealed class XmlSourceAuthException : InvalidOperationException
{
    public int HttpStatus { get; }
    public bool CredentialSent { get; }
    public XmlSourceAuthException(int httpStatus, bool credentialSent)
        : base(credentialSent
            ? $"Kaynak kayıtlı kimlik bilgisini reddetti (HTTP {httpStatus}); kullanıcı adı ve şifreyi yenileyip kaydedin. Hiçbir ürün değiştirilmedi."
            : $"Kaynak kimlik doğrulaması istiyor (HTTP {httpStatus}); kayıtlı kullanıcı adı ve şifre yok. Kaynak ayarlarında girin. Hiçbir ürün değiştirilmedi.")
    { HttpStatus = httpStatus; CredentialSent = credentialSent; }
}
