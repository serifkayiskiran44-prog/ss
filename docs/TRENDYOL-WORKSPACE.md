# Trendyol workspace — training and API contract

## User scope and evidence
The user approved implementing Trendyol first, using the five supplied Entegra trainings as the workflow reference. Credentials were subsequently supplied and saved through the existing Windows-encrypted settings. The user authorized exactly one product creation, then requested stock/price/update tests on that same product. Existing local products, XML locks, credentials and prior work are preserved. Orders redesign remains excluded. Variants, bundles, fulfillment and settlements remain deferred.

All five full Turkish auto-caption transcripts were read on 2026-09-18:

| Training | Video | Observed workflow |
|---|---|---|
| Product listing | https://www.youtube.com/watch?v=sldBPpBuWOM | Product → Trendyol → leaf category → required/optional attributes → carrier/duration/dispatch-return addresses → quantity/list-sale prices → images/description → preview/send → asynchronous status |
| Product matching | https://www.youtube.com/watch?v=j3kLprGFFC4 | Remote barcode is a separate integration code; local SKU/barcode preserved. Exact barcode match or explicit integration code. Stock only, price only and stock+price preserve remote content. |
| Integration setup | https://www.youtube.com/watch?v=g1JefYlvciE | Save seller ID/key/secret; download category/attribute/brand dictionaries; map categories/brands; select content sources and GTIN option. Orders, variants and settlements portions are outside this task. |
| Competition | https://www.youtube.com/watch?v=GfqindgkVoM | Product-level enabled flag, minimum/maximum and undercut amount; preview suggested price, own rank and logs. Competitor names/cart discounts require data not supplied by the documented API. |
| Delivery templates | https://www.youtube.com/watch?v=dGuA3Qb6Dj0 | Named carrier/duration template, apply to selected products, then explicitly send an update. Seller-panel defaults may take precedence. No buyer/seller-pays flag in this template. |

## Starting code audit
`TrendyolConnection` originally only validated credentials and threw LIVE_API_BLOCKED; `TrendyolPanel` only saved settings. These now use the real V2 client and workspace. Existing taxonomy mapping is inbound ExternalKey → LocalId. Trendyol outbound mappings use a separate LocalId → RemoteId collection, allowing multiple local categories to target one Trendyol leaf without changing legacy inbound semantics.

## Implementation decisions
- Product matching has a separate review listing every selected row with matched, new-to-create, barcode-needed, conflict, already linked and skipped filters. Manual search uses the cached seller product list, shows up to 200 results with a refinement notice, and requires an explicit choice. Similar titles never establish an automatic match. Current and proposed remote identities remain visible before saving.
- The user's final barcode-first workflow takes the saved integration barcode, otherwise local Barcode, or an explicitly entered barcode. An exact seller-cache barcode links the existing product; a missing barcode prepares that profile and opens the existing immutable Create preview for only new rows. Blank barcodes wait for input and never fall back to SKU/GTIN. Per-row unchecked drafts survive row/filter changes and block saving until checked or skipped.
- Saving reviewed profiles is one local SQLite transaction with seller/workspace/catalog/ownership checks; a missing-barcode decision requires a seller cache no older than 24 hours. Empty reviews do not create a revision. Matching itself sends no HTTP request. New products still require valid category, brand, required attributes, price and images, and a separate explicit preview/send approval.
- The main list puts product identity, publication state, stock and marketplace prices before long error columns. Action panels are scroll-bounded to retain the list; product code/name stay visible during horizontal scrolling. A WPF regression checks room for eight rows in a 1140×600 panel with both filter/action areas expanded.
- Four main sections: Trendyol control, Settings, Competition and History. Settings contains connection, category/brand matching and delivery templates. The dense product grid follows the supplied Entegra screenshots; detailed filters and grouped bulk actions start collapsed. Product editing and immutable preview have separate views.
- Delivery templates use a named list and adjacent edit form. Carrier and address names are readable, missing cached references are preserved, API refresh retains the draft, and save/edit/bulk assignment retain selected products. New clears the complete form. Template save is local; assigning and sending are separate actions.
- API base is fixed to the documented production host. No arbitrary endpoint/redirect support. Credentials remain in the existing DPAPI store; no live calls run on startup.
- Product V2 is used. Brands and category tree remain the common V1/V2 endpoints. Required attributes and allowed values come from V2 endpoints.
- Dictionary downloads are atomic: incomplete or failed pagination cannot replace a good cache. Cache revision and fetch date remain visible.
- The normal refresh retrieves the full category tree and searches catalog brand names through the official `/brands/by-name` endpoint; manually referenced brands are refreshed as well. Manual API brand search handles different local spellings. This avoids downloading every unrelated global brand. Similar search results are never automatically equated.
- Automatic suggestions never invent IDs. Exact normalized names/full paths produce candidates; ambiguous names remain for review. Only known leaf categories can be selected. Existing outbound mappings are preserved unless explicitly replaced.
- The primary product workflow uses the entered barcode only. The older exact SKU/barcode/GTIN suggestion helper remains available to code callers, but does not supply a barcode for the primary UI workflow. Blank local barcode is allowed when a Trendyol profile barcode is entered.
- A product profile stores integration code, category/brand overrides, typed attribute selections, delivery template and update mode. Local product identity and content remain unchanged.
- Create/stock/price/both/delivery previews are immutable and bound to seller, local catalog and configuration revisions. Nonselected fields never enter stock/price payloads. Barcode is required only when Trendyol requires it for that operation.
- Explicit send operates only on the reviewed payload; persistent claim/receipt prevents double send. Network ambiguity is retained as unknown and never automatically retried. Batch acceptance is not publication success.
- Competition uses the documented buybox response. Bounds/difference produce a price suggestion; analysis never changes local or remote price automatically. Unknown competitor identity/cart discounts are not guessed.
- Automated tests use fake HTTP transport and temporary SQLite only. Manual live checks read the encrypted account and use the application's persisted preview/SendAsync engine. The diagnostic utility requires one persisted row and the exact approved barcode for inventory operations; credentials are never command-line arguments.

## Official sources (verified 2026-09-18)
- https://developers.trendyol.com/tr/v2.0/docs/product-v2-api-endpoint
- https://developers.trendyol.com/docs/2-authorization
- https://developers.trendyol.com/docs/trendyol-marka-listesi-getbrands
- https://developers.trendyol.com/reference/getcategorytree
- https://developers.trendyol.com/docs/kategori-%C3%B6zellik-listesi-v2
- https://developers.trendyol.com/docs/kategori-%C3%B6zellik-de%C4%9Ferleri-listesi-v2
- https://developers.trendyol.com/tr/docs/%C3%BCr%C3%BCn-yaratma-v2
- https://developers.trendyol.com/docs/%C3%BCr%C3%BCn-filtreleme-onayl%C4%B1-%C3%BCr%C3%BCn-v2
- https://developers.trendyol.com/docs/%C3%BCr%C3%BCn-buybox-kontrol-servisi
- https://developers.trendyol.com/docs/i%CC%87ade-ve-sevkiyat-adres-bilgileri-getsuppliersaddresses

## Verification
- Final command: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release`: **1050 passed, 0 failed, 0 skipped**. New tests cover all-row manual review, barcode match/create routing, unchecked draft preservation, required-field blocking and atomic local save guards.
- Requested self-contained win-x64 `Windows-Excel-Final` publish succeeded; the published application was launched.
- Live reads: account addresses succeeded; 3,868 category nodes, 1,021 brand candidates from 254 catalog-name searches, and 14,553 pre-existing remote barcode records were read. The >10,000-content cursor path was exercised.
- Live data regressions fixed with tests: whitespace in display labels is normalized; whitespace in remote stock codes is preserved exactly. Barkcodes/tokens/credentials stay strict. NUL/ESC remain rejected.
- **Exactly one live create** was submitted on 2026-09-18 08:06 UTC: `PTD-1367`, Moderna Sensibowl white 350 ml, category 1295, brand 11303, quantity 1, sale/list price TRY 90.55, VAT 20. Local barcode remains blank; the integration code is separate.
- Persisted create plan: `2f4340521a1c48dd8eb5a60a939577c0`; batch: `da8af056-6caf-49f1-9cf3-807f304e14db-1790323566`. Batch result: `SUCCESS`. The product initially returned `pendingApproval`, then approved/onSale with content ID 1201037162. Inventory previews correctly blocked before approval.
- Following approval, the same product alone was updated manually through the application's preview/send engine: price 90.55 → 90.56 (`2b39c962c9934fa387f5e8d794795009`), stock 1 → 0 (`082a8181ec8f4fa9869a0baadb4de9c2`), then combined restoration to 90.55/1 (`0c72a1104de54ffd8947684c490b6aa6`). Every batch returned `PTD-1367: SUCCESS`. Profile sale/list overrides were restored to null. Final full-cache read verified quantity 1, sale/list 90.55, approved and onSale; 14,554 remote records now include this one added product.
- These initial create/inventory sends used the manual diagnostic runner, not GUI clicks. Subsequently the published application itself was controlled through its visible UI: saved the user's `Deneme` template, refreshed 9 carrier choices and 2 dispatch/2 return addresses, retained PTD-1367 selection, assigned this one product, previewed and submitted Delivery and ShippingDetails separately, then queried both results in History. Both returned `İşlem tamamlandı` with the single expected barcode verified by the dispatcher. Final approved-product GET confirmed `deliveryDuration: 0`, `SAME_DAY_SHIPPING`, quantity 1, sale/list 90.55 and onSale true. The template retains the user's selected same-day option and address choices.
- Content and competition have offline coverage; live success for these operations is not claimed.

GUI send evidence on 2026-09-18: Delivery plan `35f83439a38544a09269b04d883c0d07`, batch `302a0b25-149b-4636-8461-cb39a3bae7d5-1789737635`; ShippingDetails plan `0478b2951d044dd78720ecbca8e3693a`, batch `12db4b01-0a4c-4bf9-b8f4-72675343c2bd-1789737719`. Both persisted receipts contain `PTD-1367: SUCCESS`.

## Single-product factual sources
- [Manufacturer: Sensibowl Small](https://www.modernaproducts.com/gateway/product/4061/sensibowl-small): 350 ml, white option, plastic, manual bowl.
- [Matching white 350 ml product](https://zoobonus.ua/catalog/miska-dlja-sobak-i-kotiv-moderna-sensibowl-350ml-bila/p14396) and [matching product country of origin](https://wee.ae/product/moderna-sensibowl-single-plastic-white-350ml-m-40646): Belgium. Required attribute values were selected from the live category API. Existing catalog images were retained; the primary image was visually checked and returned HTTP 200.
- [Stock and price update contract](https://developers.trendyol.com/docs/stok-ve-fiyat-g%C3%BCncelleme-updatepriceandinventory).

## Barkod, marka denetim ve teslimat düzeltmesi — 2026-09-18

- Yeni ürünün barkodu yalnız `ListingBarcode` veya ürün kartındaki `Barcode` alanından alınır. Eski mağaza bağlantısı (`IntegrationCode`), SKU ve GTIN yeni barkod üretmez. Stok kodu `stockCode`, model kodu `productMainId` olarak ayrı gönderilir.
- Gönderim barkodu mevcut eşleşmeden farklı kaydedilirse güncellemeler yeniden eşleştirme yapılana kadar durur. Barkodu boş olan eski profillerin mevcut bağlantıları korunur.
- Teslimat şablonunda termin süresi ve “Ürün kartından al / Gönderme” desi seçimi bulunur. Desi her ürünün kendi `XmlAttributes[Desi]` değerinden alınır; virgüllü ondalık kabul edilir, binlik ayırıcı reddedilir. Yeni/onaysız ürün ve onaylı kargo-adres güncellemesi desteklenir; termin ayrı teslimat isteğinde gönderilir.
- Marka denetim ekranında çoklu/tüm marka seçimiyle üretici, üç ithalatçı ve kullanım uyarıları bir kez tanımlanır. Dolu alanlar birleştirilir, boş alanlar korunur; açık alan kaldırma işlemi ayrıca bulunur. Ürün özellikleri marka varsayılanlarından önceliklidir.
- Denetim varsayılanları yeni/onaysız ürün önizlemesinde güncel kategori tanımlarıyla doğrulanır. Mevcut onaylı ürünün içerik özelliklerini topluca değiştirme bu değişikliğin kapsamı değildir; alanlar gönderilmiş gibi gösterilmez.
- Canlı kategori 1295 metadata GET ile üretici 1198/1294/1296, birincil ithalatçı 1216/1305/1304, ikincil 1297/1298/1299, üçüncül 1300/1301/1302 ve uyarı1116 doğrulandı. 50 karakter sınırı belgede yalnız Renk (47) içindir.
- Önceki tek canlı denemede PTD-1367, yardımcı tarafından hazırlanan profilde barkod olarak girilmişti. Bu düzeltme mevcut mağaza barkodunu değiştirmez. Onaylı barkod API ile yerinde değiştirilemediğinden doğru barkod ve canlı düzeltme kapsamı kullanıcıyla netleştirilmelidir; bu çalışmada yeni canlı ürün yazımı yapılmadı.

Kaynaklar: [V2 ürün yaratma](https://developers.trendyol.com/tr/docs/%C3%BCr%C3%BCn-yaratma-v2), [V2 onaylı güncelleme](https://developers.trendyol.com/docs/%C3%BCr%C3%BCn-g%C3%BCncelleme-onayl%C4%B1-%C3%BCr%C3%BCn-v2).

Doğrulama: 1074 Release testi geçti (0 hata/atlama); Windows-Excel-Final yayını açıldı. Yayınlanan GUI’de termin/desi kaynağı, PTD-1367 kartında boş gönderim barkodu ile eski mağaza eşleşmesinin ayrımı ve görünür marka listesinde 240 marka / 1738 ürün toplu seçimi kontrol edildi. Alanlara örnek/fiktif üretici bilgisi yazılmadı, canlı gönderim yapılmadı.
