namespace TrMarketplaceHubDesktop.Catalog;
public sealed record CatalogFilter {
 public bool? Active {get;init;}
 public string[] Brands {get;init;}=[];
 public string[] Categories {get;init;}=[];
 public string[] Skus {get;init;}=[];
 public bool? DescriptionPresent {get;init;}
 public bool? ImagePresent {get;init;}
}
