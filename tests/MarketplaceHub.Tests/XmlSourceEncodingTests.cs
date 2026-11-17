using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class XmlSourceEncodingTests
{
    [TestMethod]
    public async Task Windows1254FeedIsDecodedWithTurkishLetters()
    {
        var path = Path.Combine(Path.GetTempPath(), "xml-1254-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var text = "<?xml version=\"1.0\" encoding=\"windows-1254\"?><Products><Product><Name>Çocuk Şampuanı ığüö</Name></Product></Products>";
            File.WriteAllBytes(path, CodePagesEncodingProvider.Instance.GetEncoding(1254)!.GetBytes(text));

            var xml = await new XmlSourceReader(new HttpClient()).ReadAsync(path);

            StringAssert.Contains(xml, "Çocuk Şampuanı ığüö");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
