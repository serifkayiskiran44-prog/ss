using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record OrderColumnDefinition(string Key,string Header,string Path,double Width,bool DefaultVisible=true,bool Required=false);

public static class OrderColumnPreferences
{
 public const string StorageKey="orders-visible-columns-v1";

 public static HashSet<string> Resolve(string? json,IReadOnlyCollection<OrderColumnDefinition> definitions)
 {
  var known=definitions.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
  HashSet<string> selected;
  try
  {
   selected=string.IsNullOrWhiteSpace(json)
    ?definitions.Where(x=>x.DefaultVisible).Select(x=>x.Key).ToHashSet(StringComparer.Ordinal)
    :(JsonSerializer.Deserialize<string[]>(json)??[]).Where(known.Contains).ToHashSet(StringComparer.Ordinal);
  }
  catch(JsonException){selected=definitions.Where(x=>x.DefaultVisible).Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);}
  foreach(var required in definitions.Where(x=>x.Required))selected.Add(required.Key);
  if(selected.Count==0&&definitions.Count>0)selected.Add(definitions.First().Key);
  return selected;
 }

 public static string Serialize(IEnumerable<string> keys)=>JsonSerializer.Serialize(keys.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal));
}
