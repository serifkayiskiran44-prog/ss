namespace TrMarketplaceHubDesktop;

public static class EtsyCredentialChanges
{
    public static EtsyCredentials Merge(EtsyCredentials prior, string key, string secret, string token, string refreshToken, string shopId, string redirectUri)
    {
        static string Keep(string value, string existing) => string.IsNullOrWhiteSpace(value) ? existing : value.Trim();

        var nextKey = Keep(key, prior.Key);
        var nextSecret = Keep(secret, prior.Secret);
        var nextToken = Keep(token, prior.Token);
        var nextRefresh = Keep(refreshToken, prior.RefreshToken);
        var appChanged = !string.Equals(nextKey, prior.Key, StringComparison.Ordinal) ||
                         !string.Equals(nextSecret, prior.Secret, StringComparison.Ordinal);
        var tokenChanged = !string.Equals(nextToken, prior.Token, StringComparison.Ordinal);

        if (appChanged && (string.IsNullOrWhiteSpace(token) || string.Equals(nextToken, prior.Token, StringComparison.Ordinal)))
            nextToken = "";
        if ((appChanged || tokenChanged) &&
            (string.IsNullOrWhiteSpace(refreshToken) || string.Equals(nextRefresh, prior.RefreshToken, StringComparison.Ordinal)))
            nextRefresh = "";

        return prior with
        {
            Key = nextKey,
            Secret = nextSecret,
            Token = nextToken,
            RefreshToken = nextRefresh,
            ShopId = shopId.Trim(),
            RedirectUri = redirectUri.Trim(),
            ExpiresAt = appChanged || tokenChanged ? null : prior.ExpiresAt,
            GrantedScopes = appChanged || tokenChanged ? null : prior.GrantedScopes
        };
    }
}
