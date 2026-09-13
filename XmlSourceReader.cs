using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.IO.Compression;
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
   if((int)response.StatusCode is 401 or 403)throw new XmlSourceAuthException((int)response.StatusCode,auth!=null&&!string.IsNullOrEmpty(auth.User));
   if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"XML alınamadı (HTTP {(int)response.StatusCode}).");
   var mediaType=response.Content.Headers.ContentType?.MediaType;
   if(mediaType is not null&&(mediaType.Equals("text/html",StringComparison.OrdinalIgnoreCase)||mediaType.Equals("application/xhtml+xml",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException($"Sunucu XML yerine HTML içerik türü ('{mediaType}') döndürdü; hata veya giriş sayfası olabilir.");
   if(response.Content.Headers.ContentLength>Limit)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");
   await using var raw=await response.Content.ReadAsStreamAsync(timeout.Token);await using var stream=response.Content.Headers.ContentEncoding.Any(x=>string.Equals(x,"gzip",StringComparison.OrdinalIgnoreCase))?new GZipStream(raw,CompressionMode.Decompress):raw;return await ParseAsync(stream,timeout.Token);
  }catch(XmlException){throw new InvalidOperationException("XML biçimi geçersiz veya DTD içeriyor. Hiçbir ürün değiştirilmedi.");}
   catch(HttpRequestException){throw new InvalidOperationException("XML sunucusuna erişilemedi. Adresi ve bağlantıyı kontrol edin.");}
   catch(OperationCanceledException){throw new InvalidOperationException("XML işlemi iptal edildi veya zaman aşımına uğradı.");}
 }
 private static async Task<string> ParseAsync(Stream stream,CancellationToken ct)
 {
  using var buffer=new MemoryStream();var block=new byte[81920];int read;
  while((read=await stream.ReadAsync(block,ct))>0){if(buffer.Length+read>Limit)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");await buffer.WriteAsync(block.AsMemory(0,read),ct);}
  if(buffer.Length==0)throw new InvalidOperationException("XML yanıtı boş.");
  buffer.Position=0;EnsureLooksLikeXml(buffer);buffer.Position=0;
  using var reader=XmlReader.Create(buffer,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=Limit});
  return XDocument.Load(reader).ToString(SaveOptions.DisableFormatting);
 }
 // A well-formed-enough HTML error/login page can otherwise parse as generic XML syntax; sniff the payload's
 // own signature so a server that mislabels its content-type is still caught, not just a bad header.
 private static void EnsureLooksLikeXml(MemoryStream buffer)
 {
  var peekLength=(int)Math.Min(buffer.Length,4096);var head=new byte[peekLength];_=buffer.Read(head,0,peekLength);
  var text=Encoding.UTF8.GetString(head).TrimStart('﻿').TrimStart();
  if(text.StartsWith("<!DOCTYPE html",StringComparison.OrdinalIgnoreCase)||text.StartsWith("<html",StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("Sunucu XML yerine HTML sayfası döndürdü (ör. hata veya giriş sayfası). Adresi ve yetkilendirmeyi kontrol edin.");
  if(!text.StartsWith("<"))throw new InvalidOperationException("Yanıt XML biçiminde değil (içerik '<' ile başlamıyor).");
 }
}
public static class XmlAuthStore
{
 private static string PathFor(string id,string? directory=null){if(!Guid.TryParse(id,out var key))throw new InvalidOperationException("Kaynak kimliği geçersiz.");return Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"),"source-auth",key.ToString("N")+".bin");}
 public static XmlAuth Load(string id,string? directory=null){var path=PathFor(id,directory);if(!File.Exists(path))return new();var plain=CredentialStore.Unprotect(File.ReadAllBytes(path));try{return JsonSerializer.Deserialize<XmlAuth>(plain)??new();}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
 /// <summary>#892: whether a credential is saved for the source -- presence only; a blob this machine cannot open is unreadable, never an exception on a health surface.</summary>
 public static CredentialPresence Presence(string id,string? directory=null){try{return SourceCredentialHealth.PresenceOf(Load(id,directory));}catch(Exception){return CredentialPresence.Unreadable;}}
 /// <summary>#901: removes the saved credential of a source that is being deleted; nothing to do when there is none, and an id that is not a source id is ignored.</summary>
 public static void Delete(string id,string? directory=null){try{var path=PathFor(id,directory);if(File.Exists(path))File.Delete(path);}catch(InvalidOperationException){}}
 public static void Save(string id,XmlAuth auth,string? directory=null){var path=PathFor(id,directory);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var plain=JsonSerializer.SerializeToUtf8Bytes(auth);try{var tmp=path+".tmp";File.WriteAllBytes(tmp,CredentialStore.Protect(plain));File.Move(tmp,path,true);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
}

