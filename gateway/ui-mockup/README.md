# GateWay NusaScope — interactive prototype

Double-click `index.html`. It runs directly from `file://`; no server, build, npm install, or backend is required. Use the **Platform admin**, **Owner**, **Admin**, or **Viewer** shortcut on the sign-in screen. Any valid email and nonempty password also signs in to the owner demo. The avatar menu contains **Demo role**, **Theme**, Account, and Sign out.

All configuration, invitations, keys, discovery, imports, catalog sync, and account operations are simulated in memory. Do not enter real credentials. Reloading the page resets the session and demo data. The sidebar's **Reset demo data** action resets data without signing out. Only the Light/Dark/System theme persists (`localStorage` key `gw-demo-theme`).

## Design and controls

- Dark-first quiet-precision / mission-control design; Light and System remain available. A saved theme is respected.
- **Ctrl+K** (Windows/Linux) or **⌘K** (Mac) opens the command palette. Type to filter; Arrow keys select, Enter opens, Esc closes. Navigation and creation commands respect the role; request-ID search stays inside the tenant workspace.
- The sidebar collapses into an icon rail; tablet uses the rail automatically, mobile uses a focus-managed navigation drawer. Sidebar NusaScope lockups switch with the theme; authentication pages always use the **Primary lockup — light**, centered above the form (with a light logo backing in dark mode). The supplied app mark serves the rail and favicon.
- Dashboard: sparkline metric strips, measured latency/TTFB p50/p95, fallback rate, provider circuit estimates, scope-aware quota bars, key/model request shares, chart metric tabs, and a chart/table toggle. Latency includes failed forwarded requests; TTFB includes only measured first bytes; fallback rate counts all forwarded requests. Denied requests are excluded from these upstream metrics.
- **Inspect** opens a request-detail drawer from Dashboard or Usage; the full request ID can be copied. Viewer gets metadata only; stored content remains owner/admin-only and respects project logging and expiry.
- Circuit states are **simulated estimates**, not live health telemetry. The disabled provider's fallback records are historical (Oct 1–2). No current circuit-breaker API or backend schema is added. Quota bars use the actual enabled policy scope; output caps are not confused with combined token quotas.

## Review routes and states

- Tenant: `#/`, `#/providers`, `#/models`, `#/projects`, `#/keys`, `#/policies`, `#/usage`, `#/alerts`, `#/webhooks`, `#/users`, `#/audit`, `#/account`.
- Platform: `#/platform/tenants`, `#/platform/plans`, `#/platform/templates`, `#/platform/catalog`, `#/platform/usage`, `#/platform/audit`.
- Authentication: `#/login`, `#/invite?token=demo-token`, `#/reset?token=demo-token`.
- Shared state sheet: `#/states` (also linked in the sidebar footer).
- Append `?demo=loading`, `?demo=empty`, or `?demo=error` to a data route, e.g. `#/usage?demo=error`. All its tables use the requested state. Sign-in also supports `#/login?demo=locked`.
- Unknown or forbidden routes show the specified warning; Users is owner-only. Viewers have no configuration write controls, but can inspect webhook deliveries and change their own account password.

The seeded clock is **October 7, 2026, 16:00 UTC**. October 1–7 contains 8,755 Acme requests, 12 denied, 4 errors, 3,420,000 input tokens, 1,980,000 output tokens, and USD 3.4125. Dashboard, Usage, CSV, and Platform usage aggregate the same individual records. Today changes the range to October 7; **7 days** and **This month** legitimately agree because the demo is on the seventh day of the month. The catalog-sync success copy is the specified sample scenario, while its local mutation adds/refreshes one deterministic preview entry; it never fetches a provider catalog.

## Files

```text
index.html                 classic-script entry point
css/app.css                shared tokens, light/dark themes, responsive layouts
js/components.js           tables, forms, dialogs, alerts, pagination, secrets
js/data.js                 seeded records and shared usage aggregation
js/app.js                  hash router, role-aware shell, command palette, theme controls
js/views/auth.js           authentication and account
js/views/dashboard.js      Dashboard and State sheet
js/views/config.js         Providers, Models, Projects, API Keys, Policies
js/views/monitoring.js     Usage, Alerts, Webhooks, Users, Audit
js/views/platform.js       platform screens and catalog entry overlay
js/check-data.cjs           optional data regression check (Node.js)
fonts/                     vendored Geist and Geist Mono + OFL license
assets/                    cropped transparent NusaScope logo exports
GateWay NusaScope Logo-png/ supplied source artwork, unchanged
dashboard-light.png         browser capture
dashboard-dark.png          browser capture
```

## Library and offline use

The only external library is **Chart.js 4.4.7**, loaded from the pinned jsDelivr URL. Layout, navigation icons (inline SVG), forms, routing, and dialogs use native HTML/CSS/JavaScript. Geist and Geist Mono are vendored locally with the SIL OFL license; system fonts remain fallbacks. Logo exports in `assets/` remove the supplied PNGs' large blank margins and solid backgrounds without changing the source files. No API, local JSON fetch, or module imports are needed. If the CDN is blocked, the chart automatically gives way to the per-day table; fonts, branding, and interactions still work offline.

To include the chart fully offline, run these commands from this folder:

```powershell
New-Item -ItemType Directory -Force js/vendor
Invoke-WebRequest https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js -OutFile js/vendor/chart.umd.min.js
```

In `index.html`, replace the Chart.js script's `src` with `js/vendor/chart.umd.min.js`, leaving the other attributes intact. Keep Chart.js's license notice in the vendored file.

## Verification

Optional deterministic data check, from the repository root:

```powershell
node gateway/ui-mockup/js/check-data.cjs
```

Current browser smoke: the 18 tenant/platform resource routes render in both themes at 1440px, 820px, and 390px (108 route/viewport/theme combinations), with zero horizontal page overflow. The shared State sheet and dashboard loading/empty/error modes are exercised separately. The icon rail is 60px; mobile navigation traps focus, closes on Esc/navigation, and keeps its off-screen sidebar inert.

Command-palette keyboard navigation and Create project, tenant request-ID lookup, request drawer, actual clipboard copying (including the native fallback), viewer restrictions, forbidden Users route, scope-aware quota updates, metric totals, chart/table switching, and System theme transitions were exercised. Blocking the Chart.js CDN on a fresh document revealed the seven-day table with the correct totals; local fonts and logos still loaded. No uncaught JavaScript errors were observed. Both Dashboard captures are refreshed.

All nine scripts passed `node --check`; the deterministic data check covers exact sample totals, nearest-rank percentile boundaries, denied exclusion, and failed-attempt latency/fallback accounting. Backend application and `gateway/web` are unchanged; the project handoff in `DECISIONS.md` records the redesign and the separate .NET build/test results.
