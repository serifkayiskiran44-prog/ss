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
 /// <summary>#886: no criterion set -- the list shows the whole pool.</summary>
 public bool IsDefault=>Active is null&&Brands.Length==0&&Categories.Length==0&&Skus.Length==0&&SourceIds.Length==0&&DescriptionPresent is null&&ImagePresent is null&&MinPrice is null&&MaxPrice is null&&MinCost is null&&MaxCost is null&&MinStock is null&&MaxStock is null&&SourceMissing is null;
}
