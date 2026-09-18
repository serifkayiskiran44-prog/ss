using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record EtsyShopInfo(long ShopId, string Name, string Currency, long UserId);
public sealed record EtsyTaxonomyNode(long Id, string Name, string Path, long ParentId);
public sealed record EtsyNamedValue(long Id, string Name);
public sealed record EtsyPropertyDefinition(long Id, string Name, bool Required, bool SupportsAttributes, bool SupportsVariations,
    IReadOnlyList<EtsyNamedValue> Values, IReadOnlyList<EtsyNamedValue> Scales);
public sealed record EtsyShippingProfile(long Id, string Name);
public sealed record EtsyProcessingProfile(long Id, string Name, string ReadinessState);
public sealed record EtsyShopSection(long Id, string Name);

public sealed class EtsyMetadataClient(HttpClient client)
{
    const string BaseUrl = "https://openapi.etsy.com/v3/application";
    const int MaxItems = 10_000;

    public async Task<EtsyShopInfo> GetShopAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        var shopId = ShopId(credentials);
        using var document = await GetAsync(credentials, $"shops/{shopId}", bearer: !string.IsNullOrWhiteSpace(credentials.Token), 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var root = Object(document.RootElement);
        var returnedShopId = PositiveId(root, "shop_id");
        var userId = PositiveId(root, "user_id");
        var name = Text(root, "shop_name");
        var currency = Text(root, "currency_code");
        if (returnedShopId != shopId)
            throw new InvalidOperationException("Etsy yanıtı yapılandırılan mağazaya ait değil.");
        var tokenUserId = TokenUserId(credentials.Token);
        if (tokenUserId.HasValue && tokenUserId.Value != userId)
            throw new InvalidOperationException("Etsy erişim bilgisi yapılandırılan mağaza sahibine ait değil.");
        return new(returnedShopId, name, currency, userId);
    }

    public async Task<IReadOnlyList<EtsyTaxonomyNode>> GetSellerTaxonomyAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync(credentials, "seller-taxonomy/nodes", false, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var results = Results(document.RootElement);
        var nodes = new List<EtsyTaxonomyNode>();
        foreach (var result in results.EnumerateArray()) FlattenTaxonomy(result, "", nodes);
        return nodes;
    }

    public async Task<IReadOnlyList<EtsyPropertyDefinition>> GetPropertiesAsync(EtsyCredentials credentials, long taxonomyId, CancellationToken cancellationToken = default)
    {
        if (taxonomyId <= 0) throw new ArgumentOutOfRangeException(nameof(taxonomyId));
        using var document = await GetAsync(credentials, $"seller-taxonomy/nodes/{taxonomyId.ToString(CultureInfo.InvariantCulture)}/properties", false, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var properties = new List<EtsyPropertyDefinition>();
        foreach (var item in Results(document.RootElement).EnumerateArray())
        {
            var property = Object(item);
            properties.Add(new(PositiveId(property, "property_id"), DisplayText(property), Boolean(property, "is_required"),
                Boolean(property, "supports_attributes"), Boolean(property, "supports_variations"),
                NamedValues(property, "possible_values", "value_id", false), NamedValues(property, "scales", "scale_id", true)));
        }
        return properties;
    }

    public async Task<IReadOnlyList<EtsyShippingProfile>> GetShippingProfilesAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        var shop = await GetShopAsync(credentials, cancellationToken).ConfigureAwait(false);
        using var document = await GetAsync(credentials, $"shops/{shop.ShopId}/shipping-profiles", true, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var profiles = new List<EtsyShippingProfile>();
        foreach (var item in Results(document.RootElement).EnumerateArray())
        {
            var profile = Object(item);
            if (PositiveId(profile, "user_id") != shop.UserId) throw new InvalidOperationException("Etsy kargo profili farklı bir mağaza sahibine ait.");
            profiles.Add(new(PositiveId(profile, "shipping_profile_id"), Text(profile, "title")));
        }
        return profiles;
    }

    public async Task<IReadOnlyList<EtsyProcessingProfile>> GetProcessingProfilesAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        var shop = await GetShopAsync(credentials, cancellationToken).ConfigureAwait(false);
        var profiles = new List<EtsyProcessingProfile>();
        var offset = 0;
        while (true)
        {
            using var document = await GetAsync(credentials, $"shops/{shop.ShopId}/readiness-state-definitions?limit=100&offset={offset.ToString(CultureInfo.InvariantCulture)}", true, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var root = Object(document.RootElement);
            var results = Results(root);
            var count = NonNegativeInt64(root, "count");
            var pageCount = 0;
            foreach (var item in results.EnumerateArray())
            {
                var profile = Object(item);
                if (PositiveId(profile, "shop_id") != shop.ShopId) throw new InvalidOperationException("Etsy hazırlık profili farklı bir mağazaya ait.");
                profiles.Add(new(PositiveId(profile, "readiness_state_id"), Text(profile, "processing_days_display_label"), Text(profile, "readiness_state")));
                pageCount++;
                if (profiles.Count > MaxItems) throw new InvalidOperationException("Etsy metadata sonucu öğe sınırını aşıyor.");
            }
            if (pageCount == 0 || profiles.Count >= count) break;
            offset += pageCount;
        }
        return profiles;
    }

    public async Task<IReadOnlyList<EtsyShopSection>> GetSectionsAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        var shop = await GetShopAsync(credentials, cancellationToken).ConfigureAwait(false);
        using var document = await GetAsync(credentials, $"shops/{shop.ShopId}/sections", true, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var sections = new List<EtsyShopSection>();
        foreach (var item in Results(document.RootElement).EnumerateArray())
        {
            var section = Object(item);
            if (PositiveId(section, "user_id") != shop.UserId) throw new InvalidOperationException("Etsy mağaza bölümü farklı bir kullanıcıya ait.");
            sections.Add(new(PositiveId(section, "shop_section_id"), Text(section, "title")));
        }
        return sections;
    }

    async Task<JsonDocument> GetAsync(EtsyCredentials credentials, string path, bool bearer, int maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{path}");
        EtsyHttp.AddHeaders(request, credentials, bearer);
        return await EtsyHttp.SendJsonAsync(client, request, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    static long ShopId(EtsyCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.ShopId) || credentials.ShopId.Any(c => c < '0' || c > '9') ||
            !long.TryParse(credentials.ShopId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        return id;
    }

    static long? TokenUserId(string token)
    {
        var separator = token.IndexOf('.');
        if (separator <= 0) return null;
        var prefix = token.AsSpan(0, separator);
        if (!prefix.ToString().All(char.IsAsciiDigit)) throw new ArgumentException("Etsy erişim bilgisi kullanıcı kimliği öneki içermiyor.");
        return long.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id :
            throw new ArgumentException("Etsy erişim bilgisi kullanıcı kimliği geçersiz.");
    }

    static void FlattenTaxonomy(JsonElement element, string parentPath, List<EtsyTaxonomyNode> nodes)
    {
        if (nodes.Count >= MaxItems) throw new InvalidOperationException("Etsy taxonomy sonucu öğe sınırını aşıyor.");
        var node = Object(element);
        var id = PositiveId(node, "id");
        var name = Text(node, "name");
        var path = string.IsNullOrEmpty(parentPath) ? name : parentPath + " > " + name;
        var parentId = OptionalId(node, "parent_id");
        nodes.Add(new(id, name, path, parentId));
        if (node.TryGetProperty("children", out var children))
        {
            if (children.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var child in children.EnumerateArray()) FlattenTaxonomy(child, path, nodes);
        }
    }

    static IReadOnlyList<EtsyNamedValue> NamedValues(JsonElement parent, string propertyName, string idName, bool displayName)
    {
        if (!parent.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        var result = new List<EtsyNamedValue>();
        foreach (var item in values.EnumerateArray())
        {
            var value = Object(item);
            result.Add(new(PositiveId(value, idName), displayName ? DisplayText(value) : Text(value, "name")));
            if (result.Count > MaxItems) throw new InvalidOperationException("Etsy metadata sonucu öğe sınırını aşıyor.");
        }
        return result;
    }

    static JsonElement Results(JsonElement element)
    {
        var root = Object(element);
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > MaxItems)
            throw InvalidResponse();
        return results;
    }

    static JsonElement Object(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw InvalidResponse();
    static long PositiveId(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var id) && id > 0 ? id : throw InvalidResponse();
    static long OptionalId(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? 0 :
        value.TryGetInt64(out var id) && id > 0 ? id : throw InvalidResponse();
    static long NonNegativeInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0 && count <= MaxItems ? count : throw InvalidResponse();
    static bool Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : throw InvalidResponse();
    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 4096
            ? value.GetString()! : throw InvalidResponse();
    static string DisplayText(JsonElement element) =>
        element.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(display.GetString())
            ? Text(element, "display_name") : Text(element, "name");
    static InvalidOperationException InvalidResponse() => new("Etsy geçerli bir metadata yanıtı döndürmedi.");
}
