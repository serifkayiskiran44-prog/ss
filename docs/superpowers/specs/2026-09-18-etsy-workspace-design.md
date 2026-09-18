# Etsy workspace design

The user requests an Etsy workspace following the working Trendyol workspace, the two supplied Entegra tutorials, current official Etsy API documentation, and reuse of the existing authorized Etsy account connection. This is an extension of existing WPF OAuth/listing code, not a replacement application.

## User workflow

- Add Etsy under integrations and an Etsy settings shortcut. Use Etsy control, Settings and Operation history tabs.
- Control is a wide product grid with compact search, expandable filters and bulk actions. Show local SKU/name/stock, Etsy listing ID/state/stock/price/currency and errors. Product editing and send preview open separate dialogs so the list keeps useful height.
- Settings contain connection, category mapping, and listing/shipping templates. Preserve DPAPI credentials; show masked fields, connection status and clear OAuth start/callback actions. Load existing credentials and refresh tokens where possible. API reads verify account/shop identity.
- Retrieve Etsy seller taxonomy, properties, shipping profiles, processing/readiness profiles and shop sections from official APIs. Search categories by name/path; retain IDs behind readable labels. Retrieve properties on category selection; required values are visible, never invented.
- Named listing templates store currency, who made, when made, supply type, shipping/processing profile, tags and materials. Product-specific overrides take precedence; template/category mapping changes cannot change unselected local catalog fields.
- Match remote listings by exact SKU, with ambiguity/duplicate guards and manual selection. Existing legacy listing IDs must be validated against the connected shop before use. No SKU-to-barcode conversion.
- Preview create-draft, price, quantity, combined price/quantity, content, publish and deactivate actions. Create draft is distinct from publish. Each row has actionable validation and clear price currency. Price-only cannot change quantity and quantity-only cannot change price. Preserve remote inventory properties; unsupported variant writes are blocked because variants remain deferred.
- Persist immutable account/shop-bound previews, catalog/workspace revisions and durable per-row receipts. Repeated or uncertain writes cannot silently retry. Verify remote ownership/state before mutation and return real API results.
- Reuse the existing Orders page for Etsy order reads. No new order module or hidden schedule/write automation.

## Sources and API decisions

Reviewed complete Turkish auto-generated transcripts of https://www.youtube.com/watch?v=5NdZfFWlMHk (8:04) and https://www.youtube.com/watch?v=bwzb8fvWByU (2:32), embedded in the user's Entegra links. The first covers OAuth callback, category retrieval, shipping template retrieval, currency and category mapping; the second covers category/property selection, shipping and who/when/supply defaults, product review and listing results. Variant segments remain outside scope.

Official API authority: https://developers.etsy.com/documentation/ and its API reference, authentication, request standards and listing tutorials, checked 2026-09-18. Existing community SDK research is a secondary implementation reference only. OAuth uses PKCE/state and x-api-key keystring:shared_secret. Physical drafts use shipping_profile_id and readiness_state_id. Confirm current endpoint contracts against the reference before implementing.

## Constraints and acceptance

WPF/.NET 8; existing data and credentials preserved; no application login. Seven DEFERRED_BY_USER areas remain excluded. Secrets never enter source, fixtures, logs or reports. All automated tests use temporary storage/fake HTTP. Live writes require concrete reviewed user approval; this request authorizes connection and read verification, not arbitrary product publication.

Acceptance: regression tests for API contracts, wrong-shop/currency/stale/duplicate guards, match conflicts, blank overrides and desktop navigation/layout; full Release test command; self-contained Windows-Excel-Final publish; open and visually verify the app; connect/read existing shop if credentials/session permit, otherwise report the precise remaining authorization step. Do not report local previews as live success.
