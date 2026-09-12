namespace TrMarketplaceHubDesktop.Catalog;
public sealed record CatalogFilter {
 public bool? Active {get;init;}
 public string[] Brands {get;init;}=[];
 public string[] Categories {get;init;}=[];
 public string[] Skus {get;init;}=[];
 public string[] SourceIds {get;init;}=[];
 public bool? DescriptionPresent {get;init;}
 public bool? ImagePresent {get;init;}
 public decimal? MinPrice {get;init;}
 public decimal? MaxPrice {get;init;}
 public decimal? MinCost {get;init;}
 public decimal? MaxCost {get;init;}
 public int? MinStock {get;init;}
 public int? MaxStock {get;init;}
 public bool? SourceMissing {get;init;}
 public string SortBy {get;init;}="Name";
 public bool SortDescending {get;init;}
}
