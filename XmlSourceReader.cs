using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace TrMarketplaceHubDesktop;
public record XmlAuth(string User="",string Password="");
public class XmlSourceReader(HttpClient client)
{
 public const int Limit=25*1024*1024;
 public async Task<string> ReadAsync(string location,XmlAuth? auth=null,CancellationToken ct=default)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(60));
  try {
   if(File.Exists(location)){await using var file=File.OpenRead(location);return await ParseAsync(file,timeout.Token);}
   if(!Uri.TryCreate(location,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo))throw new InvalidOperationException("XML dosyası veya kullanıcı bilgisi içermeyen HTTPS adresi gerekli.");
   using var request=new HttpRequestMessage(HttpMethod.Get,uri);
   if(auth!=null&&!string.IsNullOrEmpty(auth.User)){
    if(auth.User.Contains(':')||auth.User.Any(char.IsControl)||auth.Password.Any(char.IsControl))throw new InvalidOperationException("XML kullanıcı bilgileri geçersiz.");
    request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.User+":"+auth.Password)));
   }
   using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
   if((int)response.StatusCode is >=300 and <400)throw new InvalidOperationException("XML adresi yönlendiriliyor. Son HTTPS adresini kullanın.");
   if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"XML alınamadı (HTTP {(int)response.StatusCode}).");
   if(response.Content.Headers.ContentLength>Limit)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");
   await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);return await ParseAsync(stream,timeout.Token);
  }catch(XmlException){throw new InvalidOperationException("XML biçimi geçersiz veya DTD içeriyor. Hiçbir ürün değiştirilmedi.");}
   catch(HttpRequestException){throw new InvalidOperationException("XML sunucusuna erişilemedi. Adresi ve bağlantıyı kontrol edin.");}
   catch(OperationCanceledException){throw new InvalidOperationException("XML işlemi iptal edildi veya zaman aşımına uğradı.");}
 }
 private static async Task<string> ParseAsync(Stream stream,CancellationToken ct)
 {
  using var buffer=new MemoryStream();var block=new byte[81920];int read;
  while((read=await stream.ReadAsync(block,ct))>0){if(buffer.Length+read>Limit)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");await buffer.WriteAsync(block.AsMemory(0,read),ct);}
  buffer.Position=0;
  using var reader=XmlReader.Create(buffer,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=Limit});
  return XDocument.Load(reader).ToString(SaveOptions.DisableFormatting);
 }
}
public static class XmlAuthStore
{
 private static string PathFor(string id,string? directory=null){if(!Guid.TryParse(id,out var key))throw new InvalidOperationException("Kaynak kimliği geçersiz.");return Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"),"source-auth",key.ToString("N")+".bin");}
 public static XmlAuth Load(string id,string? directory=null){var path=PathFor(id,directory);if(!File.Exists(path))return new();var plain=CredentialStore.Unprotect(File.ReadAllBytes(path));try{return JsonSerializer.Deserialize<XmlAuth>(plain)??new();}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
 public static void Save(string id,XmlAuth auth,string? directory=null){var path=PathFor(id,directory);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var plain=JsonSerializer.SerializeToUtf8Bytes(auth);try{var tmp=path+".tmp";File.WriteAllBytes(tmp,CredentialStore.Protect(plain));File.Move(tmp,path,true);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
}

