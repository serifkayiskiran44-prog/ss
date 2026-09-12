using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public static class XmlPreviewFingerprint
{
    public static string Create(string xml, string sourceSnapshot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml + "\n" + sourceSnapshot)));

    public static string FeedHash(string xml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    public static string FeedHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}
