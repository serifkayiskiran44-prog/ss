# Management Center Implementation Plan

> **For agentic workers:** Use executing-plans to implement inline.

**Goal:** Preserve existing workflows in a channel-neutral management center.
**Architecture:** Reuse all existing WPF controls; route a grouped sidebar to bounded pages and group channel controls into local tabs.
**Tech Stack:** .NET 8 WPF, existing SQLite stores.
**Spec:** Parent task: shared products, XML, channel management, orders and settings at 1150×760.

## Constraints
No seeded data or invented API integration. Preserve XML pricing, product editing, Etsy actions, order and connection panels.

- [x] Add runtime smoke that switches products/xml/etsy/ebay/ozon/joom/orders/settings and checks usable bounds; baseline fails with missing sidebar.
- [x] Replace shell in MainWindow.xaml with sidebar, page title and bounded content.
- [x] Reuse built controls through named routes; put Etsy listings, template, connection into channel tabs. Create explicit pending channel product pages.
- [x] Change XML settings to local source/mapping/pricing tabs; retain preview and actions.
- [x] Run navigation smoke and all existing tests; build and publish Windows-Yonetim-Merkezi.

Verification: baseline smoke failed with missing sidebar; final smoke passed all eight routes at 1150×760. Existing suite 212/212 passed. Release self-contained win-x64 publish succeeded. Channel-card tests maintained separately by channel agent.

