# AI Gateway — Codex build prompt (static HTML prototype)

> **For you (do not paste this note):** paste everything from the line `BEGIN PROMPT` to the end of the file into Codex. The prompt is self-contained. Codex will create a new folder `gateway/ui-mockup/` and nothing else. Open `gateway/ui-mockup/index.html` in a browser when it finishes (or run `npx serve gateway/ui-mockup`).

---

BEGIN PROMPT

## 1. Task

Build a **static, clickable HTML prototype** of an admin console called "AI Gateway" (full product and screen specs are in sections 3–6). It is a design mockup with fake in-memory data: **no backend, no real network calls**. The goal is a polished, consistent, realistic UI that a team can review in a browser and later port to React.

**Hard rules**
- Create ONLY the new folder `gateway/ui-mockup/`. Do not modify, move, or delete anything else in the repository (especially `gateway/web`, `gateway/src`, `gateway/tests`, docs).
- The prototype must work by **double-clicking `index.html`** (`file://`). Therefore: no build step, no npm install required to view, no `type="module"` imports of local files, no `fetch()` of local JSON. Use classic `<script>` tags and a small global namespace; keep the fake data in `js/data.js`.
- HTML, CSS, and JavaScript are free to use. Libraries are allowed from a CDN with **pinned versions**. Recommended minimal set: plain CSS with CSS variables (or Tailwind CDN), vanilla JS (or Alpine.js), **Chart.js** for the dashboard chart, and an icon set (e.g. Lucide). If you use CDN libraries, also add a short `README.md` in the folder explaining how to vendor them locally for offline use. Do not add heavy frameworks or a bundler.
- All UI text is **English** and must use the **exact strings** given in the screen specs.

## 2. Deliverables

```text
gateway/ui-mockup/
  index.html          entry point, loads css and js, contains the app root and <dialog> templates if needed
  css/app.css         design tokens (CSS variables), light + dark themes, components, layout
  js/data.js          fake data (see section 6), deterministic (seeded) generator for usage logs and audit rows
  js/app.js           hash router, state store, shell (sidebar + top bar), role switcher, theme toggle
  js/views/*.js       one file per screen group (auth, dashboard, config, monitoring, platform)
  js/components.js    shared UI helpers: table, pager, badge, modal, secret panel, alert, form field, toast
  README.md           how to open, structure, libraries used, how to reset demo data
```

## 2.1 Required behaviour

1. **Hash routes** mirroring the real app: `#/login`, `#/invite`, `#/reset`, `#/account`, `#/` (Dashboard), `#/providers`, `#/models`, `#/projects`, `#/keys`, `#/policies`, `#/usage`, `#/alerts`, `#/webhooks`, `#/users`, `#/audit`, `#/platform/tenants`, `#/platform/plans`, `#/platform/templates`, `#/platform/catalog`, `#/platform/usage`, `#/platform/audit`. Unknown or not-permitted routes show the warning banner `Page “{path}” was not found or is not available for your role.`.
2. **Fake sign-in**: the sign-in screen accepts any email/password and also offers four demo shortcut buttons: "Platform admin", "Owner", "Admin", "Viewer". After sign-in the role decides the navigation (platform_admin is redirected to `#/platform/tenants`; tenant roles to `#/`).
3. **Role switcher** in the top bar (a small dropdown, clearly labelled "Demo role") so a reviewer can switch roles without signing out. Role rules: viewer sees no write buttons and the muted text "read-only" in action cells; "Users" is owner-only; admin cannot see "Users"; platform_admin sees only the Platform section.
4. **Theme**: light, dark, and system, with a toggle in the top bar; remember the choice in `localStorage`.
5. **Working interactions on the in-memory data** (no persistence required, but add a "Reset demo data" action in the README or a footer link): add/edit/delete rows through the dialogs; enable/disable toggles; revoke key; invite user and role change inline; every "secret shown once" flow shows the one-time panel with a working "Copy" button and a "Close" button; confirmation prompts use a styled confirm dialog (not `window.confirm`).
6. **Filters, pagination, and sorting-free tables** must really work on fake data for: Usage (summary grouping + log filters + pager + "Export CSV" that downloads a real CSV of the filtered rows), Audit and Platform audit (date range, rows per page, pager, expandable detail), API Keys (project filter), Policies (scope selectors), Catalog (template filter).
7. **Dashboard** has the six KPI cards, the segmented time range ("Today", "7 days", "This month") that really changes the numbers, a Chart.js bar/line chart from the per-day data, and the "Per day" / "Per model" tables.
8. **Forms** validate client-side with the exact messages from the specs (for example webhook URL rules, "Password confirmation does not match.", threshold 1–100) and show loading/disabled button states briefly (fake 400 ms delay) before applying changes.
9. **States**: every table has an empty-state row, a skeleton/loading state ("Loading…"), and an error banner example reachable through a hidden demo switch (for example `?demo=error` in the hash query) so reviewers can see them.
10. **Responsive and accessible**: usable from 1440px down to tablet width; sidebar collapses to an icon rail or drawer; keyboard focus visible; dialogs use native `<dialog>` with focus trapping and Esc to close; semantic table markup; skip link "Skip to content"; AA contrast in both themes.

## 2.2 Quality bar and acceptance checklist

- One cohesive design system: all colors, spacing, radii, shadows, and type sizes come from CSS variables defined once in `css/app.css`. No inline styles for theming, no copy-pasted style blocks per screen.
- No lorem ipsum; use the sample data in section 6. Numbers, dates, and currency must look realistic and consistent between Dashboard, Usage, and Platform usage.
- All 21 screens (S1–S21) and 4 overlay dialogs (O1–O4) are reachable and match their specs (columns, labels, hints, buttons, empty/loading/error texts).
- No console errors or 404s when opening `index.html` from disk (CDN libraries aside); the app still renders (without the chart) if the CDN is unreachable.
- Do a self-check before finishing: open every route in a headless browser (Playwright or Puppeteer if available in the sandbox; otherwise reason through the code) and confirm no uncaught exceptions; take one screenshot of Dashboard (light and dark) if a browser is available.
- Final report: at most 15 lines: what was built, how to open it, libraries and versions used, anything from the specs that you could not implement and why.

## 2.3 Suggested build order

1. Tokens + shell (sidebar, top bar, role switcher, theme toggle, router).
2. Shared components (table, pager, badge, modal/confirm, secret panel, alert, field, toast, skeleton).
3. Data layer and seeded generators.
4. Screens in this order: Dashboard (S4), Providers (S5), Models (S6), Projects (S7), API Keys (S8), Policies (S9), Usage (S10), Alerts (S11), Webhooks (S12), Users (S13), Audit (S14), then S1–S3, then Platform S15–S20, then S21 and overlays O1–O4.

---

## 3. Product, roles, and shared design system

```text
Design a web admin console called "AI Gateway".

PRODUCT
AI Gateway is a multi-tenant proxy that sits between customer apps and AI model providers (OpenAI-compatible APIs). Each tenant brings its own provider keys (BYOK), publishes model aliases that route to upstream models with priority/weight failover, sets token prices, issues API keys per project, enforces policies (token quotas, rate limits, monthly budget, allowed models), watches usage and cost, receives quota alerts through signed webhooks, manages team members, and reviews an audit trail. A separate Platform area is for the installation operator: tenants, plans, provider templates, a global model catalog, cross-tenant usage, and an installation-wide audit log.

ROLES
- platform_admin: operator; sees ONLY the Platform section.
- owner: full control of one tenant, including user management.
- admin: configures everything except users.
- viewer: read-only; write buttons are hidden and the row action cell shows the muted text "read-only".

STYLE
Modern, calm, data-dense admin console (think Stripe / Linear / Vercel dashboards). Desktop-first at 1440px, responsive down to tablet. Provide LIGHT and DARK themes. Generous whitespace but compact tables. No decorative illustrations; this is a tool for operators.

DESIGN SYSTEM
- Primary accent: blue (#1d4ed8 light, #6f9bff dark). Neutral grays for surfaces (light: bg #f6f7f9, surface #ffffff, border #d8dce2, text #16181d, muted #5c6470; dark: bg #14161a, surface #1c1f25, border #33383f, text #eceef2, muted #a3abb8).
- Status colors: success green (#14653a text on #e5f5ea), danger red (#8a2c22 text on #fbe9e7), warning amber (#b7791f), info = accent.
- Typography: Inter (or system-ui). Page title 24px semibold, section title 17px, table/body 14-15px, hints 13px muted. Monospace (ui-monospace) for IDs, key prefixes, URLs, model aliases, and JSON.
- Radius: 8px inputs/buttons, 12px cards and dialogs, pill badges. Soft shadow only on dialogs.
- App shell: left sidebar navigation (grouped) + top bar. The top bar shows the brand "AI Gateway" with a muted subtitle (the tenant name, or "platform" for platform_admin), the user's display name, a role badge, a ghost button "Account" (tenant users only) and a ghost button "Sign out".
  Tenant sidebar groups and labels: Dashboard | Providers, Models, Projects, API Keys, Policies | Usage, Alerts, Webhooks, Audit | Users (owner only).
  Platform sidebar labels: Tenants, Plans, Templates, Catalog, Platform usage, Platform audit.
- Reusable components: page header (title, muted description, right-aligned primary action); stat cards; data table with sticky header and horizontal scroll; status badge ("active" green / "inactive" red pills); toolbar of inline filters; pager ("Previous" / "Page 1 of 4 (180 rows)" / "Next"); modal dialog (title + close "✕", scrollable body, footer with primary button + ghost "Cancel"); inline alert banners with a colored left border (error, success, warning, info); a "secret shown once" panel (warning-styled box with text, a monospace value, and buttons "Copy" and "Close"); empty-state table rows; loading text "Loading…".
- Accessibility: visible focus rings, AA contrast, skip link "Skip to content".

LANGUAGE
All UI text is English. Use the exact strings given in each screen prompt. Technical values stay as-is (for example: active, suspended, daily_tokens, monthly_budget, ok, error, denied).

CONSTRAINTS
Keep the same information architecture, columns, fields, and actions as specified. Do not add features that need new backend data. Allowed visual-only enhancements: left sidebar instead of a horizontal nav, a simple bar/line chart on the Dashboard built from the existing per-day numbers (requests, tokens, cost), progress bars for "used vs limit" in alert events, skeleton loaders.
```

---

## 4. Screens (S1–S21)

### S1 — Sign in

```text
Apply the shared design system and app shell from section 3.
Screen: Sign in (unauthenticated). Centered card, about 420px wide, on a quiet background.
Content: title "AI Gateway"; subtitle "Sign in with your tenant or platform account."; field "Email" (type email, required); field "Password" (type password, required); primary full-width button "Sign in" (loading state "Signing in…" and disabled); footer text "Have an invitation token? Accept invitation. Forgot your password? Reset password." where "Accept invitation" and "Reset password" are links.
Also show the error state: a red alert banner above the form with a message such as "Invalid email or password." and a locked state such as "Account temporarily locked. Try again later.".
No sidebar or top bar on this screen. Light and dark variants.
```

### S2 — Accept invitation / Reset password

```text
Apply the shared design system and app shell from section 3.
Screen: token redemption, two variants of the same layout (centered card, 420px, no app shell).
Variant A "Accept Invitation": subtitle "Paste the invitation token from the platform/owner to create your account password."; fields "Token" (pre-filled when the URL contains it, monospace), "New password" with hint "At least as required by the gateway password policy.", "Repeat password"; primary button "Activate account" (loading "Saving…").
Variant B "Reset Password": subtitle "Paste the password reset token from your owner to set a new password."; same fields; primary button "Save password".
States to show: validation error "Password confirmation does not match."; success banner "Done. Sign in with your new password. Sign in" (the last word is a link). Bottom link "Back to sign in".
```

### S3 — Account (change password)

```text
Apply the shared design system and app shell from section 3.
Screen: "Account" inside the app shell (tenant user). Page header: title "Account", description "Change your account password.".
One card titled "Change password" with fields "Current password", "New password", "Repeat new password" and primary button "Change password" (loading "Saving…").
Show the success state: banner "Password changed. Session ended — please sign in again." and the mismatch error "Password confirmation does not match.".
```

### S4 — Dashboard

```text
Apply the shared design system and app shell from section 3.
Screen: "Dashboard" (roles owner/admin/viewer; read-only for everyone).
Page header: title "Dashboard", description "Usage 2026-10-01 to 2026-10-07.". Under it a segmented control "Time range" with three options: "Today", "7 days", "This month" (one selected).
Six KPI stat cards in one row, label then big number: "Requests", "Denied", "Errors", "Input tokens", "Output tokens", "Cost" (cost shown like "USD 12.3456").
Card "Per day": a small bar/line chart of the per-day numbers is allowed, followed by a table with columns Day | Requests | Denied | Errors | Input tokens | Output tokens | Cost. Empty state text: "No usage in this range.".
Card "Per model": table with columns Model | Requests | Denied | Errors | Input tokens | Output tokens | Cost. Empty text "No data yet.".
Use realistic numbers from the sample data. Also show the loading state "Loading…".
```

### S5 — Providers

```text
Apply the shared design system and app shell from section 3.
Screen: "Providers" (upstream AI providers owned by the tenant, bring-your-own-key).
Page header: title "Providers", description "Upstream providers owned by your tenant (BYOK). Only the last 4 characters of a key are shown.", primary button "Add provider" (hidden for viewer).
Table columns: Name | Type | Template | Base URL (monospace) | Key (shows "…a1b2" or "—") | Status (badge "active"/"inactive") | Actions.
Row actions (owner/admin): "Edit", "Disable" or "Enable", "Rotate key", "Test discovery", "Import models", and a red "Delete". Collapse them into a compact button group or an overflow menu. Viewer sees muted "read-only".
Empty row: "No data yet.". Error banner state for failed loads.
Rows: provider "opencode" (template opencode, https://opencode.ai/inference/openai/v1/, key …9x2f, active) and a custom one "internal-llm" (template —, inactive).
```

### S6 — Models

```text
Apply the shared design system and app shell from section 3.
Screen: "Models" (model aliases that clients call).
Page header: title "Models", description "Model aliases clients see, routes to providers, and prices.", primary button "Add model".
Table columns: Alias (monospace) | Description | Routes | Max output | Price | Status | Actions.
Routes cell: a short list, each item "opencode → deepseek-v4.1-flash (p1/w1)" and "(p2/w1, disabled)" for a disabled route; muted "no routes" when empty. Price cell: "0.3 / 1.2 (+1 tier)" or "—".
Row actions: "Edit", "Routes", "Add price", "Disable"/"Enable", red "Delete"; viewer sees "read-only".
Rows: alias "deepseek-v4.1-flash" with two routes and one price tier; alias "glm-5.3-flash" with one route; one disabled model.
```

### S7 — Projects

```text
Apply the shared design system and app shell from section 3.
Screen: "Projects".
Page header: title "Projects", description "Projects separate keys, policies, and usage per application.", primary button "Add project".
Table columns: Name | Status (badge, values "active" green / "suspended" red) | Content logging ("yes"/"no") | Content retention ("30 days" or "—") | Created (date-time) | Actions ("Edit", red "Delete"; viewer: "read-only").
Dialog "Add project" / "Edit project — {name}" as an overlay state: field "Name"; select "Status" (active, suspended) with hint "Suspending a project rejects new requests on it."; checkbox "Store conversation content logs"; number "Content retention (days)" with hint "Leave empty for no retention; enter 0 or more."; edit-only checkbox "Clear content retention"; buttons "Save" and "Cancel".
```

### S8 — API Keys

```text
Apply the shared design system and app shell from section 3.
Screen: "API Keys".
Page header: title "API Keys", description "Keys are grouped by project; the full value is shown only once when created.", primary button "Issue key".
Show the "secret shown once" panel at the top after creating a key: "New API key — copy it now; this value is shown only once." with the monospace value gw_a1b2c3d4e5_… and buttons "Copy" and "Close".
Toolbar: select "Filter by project" with "All projects" + project names.
Table columns: Name | Project | Prefix (monospace, "gw_a1b2c3d4e5…") | Allowed IPs (comma list or "all") | Expires | Last used | Status (green "active" or red "revoked 2026-10-05") | Actions (active: "Edit", red "Revoke"; revoked: muted "revoked"; viewer: "read-only").
Dialog "Issue API key" / "Edit key — {name}": select "Project" (create only, "Select project…"), "Key name", datetime-local "Expires" with hint "Leave empty for no expiry." (edit hint "Currently: …" plus checkbox "Remove expiry"), textarea "Allowed IPs" (4 rows) with hint "One IP per line (or comma-separated). Empty = all IPs."; buttons "Issue key" / "Save changes" and "Cancel".
```

### S9 — Policies

```text
Apply the shared design system and app shell from section 3.
Screen: "Policies".
Page header: title "Policies", description "Quota limits and allowed models per tenant, project, or API key.".
Toolbar: select "Scope" (Tenant, Project, API key); when Project is chosen a select "Project" ("Select project…"); when API key is chosen a select "API key" ("Select key…" with "name (prefix…)").
Inline editor card (not a modal) titled "Project policy {id}" with a monospace id. Fields in a grid: "Max tokens per request" (hint "Empty = no limit."), "Requests per minute", "Daily token quota", "Monthly token quota", "Monthly budget" (decimal), textarea "Allowed models" (hint "One alias per line. Empty = all models."), checkbox "Policy enabled", primary button "Save policy". Success banner "Policy saved.". For viewers all inputs are disabled with the note "Only owners/admins can change policies.". Placeholder states: "Select a project to see its policy." and "No policy for this scope yet — fill in the form to create one.".
Card "All policies": table Scope | ID (monospace) | Max tokens/req | Req/min | Daily quota | Monthly quota | Monthly budget | Allowed models ("all" or list) | Status (active/inactive) | (red "Delete"). Empty: "No policies yet.".
```

### S10 — Usage

```text
Apply the shared design system and app shell from section 3.
Screen: "Usage".
Page header: title "Usage", description "Aggregate summary and per-request log.".
Card "Summary": toolbar with date "From", date "To", select "Group by" (Day, Project, Model, API key); table with columns Day (or Name) | Requests | Denied | Errors | Input tokens | Output tokens | Cost.
Card "Request log": toolbar with datetime "From", datetime "To", selects "Project", "Model", "API key" (each with "All"), select "Status" (All, ok, error, denied), select "Rows per page" (25, 50, 100, 200) and a button "Export CSV". Table columns: Time | Request (first 8 chars, monospace) | Status (badge: ok green; error/denied red) | HTTP | Reason (e.g. "daily_quota_exceeded:key" or "—") | Input | Output | Cache | Cost | Latency ("812 ms") | Attempt | Project | Model | Key | Content (button "View", owner/admin only). Pager below.
Overlay "Request content — log #123" (wide dialog): muted "Expires: …", heading "Request" with a pretty-printed JSON block, heading "Response" with another JSON block; error state "… Content is only available if the project stores conversation content and the retention period has not passed.".
```

### S11 — Alerts

```text
Apply the shared design system and app shell from section 3.
Screen: "Alerts".
Page header: title "Alerts", description "Quota notifications based on usage percentage. Two rules are recommended: 80 (warning) and 100 (critical).", primary button "Add alert".
Rules table columns: Name | Metric (monospace: daily_tokens, monthly_tokens, monthly_budget) | Scope ("tenant", "project: chatbot-prod" or "key: ci-key") | Threshold ("80%") | Webhook (name or "—") | Last triggered | Status (active/inactive) | Actions ("Edit", red "Delete"; viewer: "read-only").
Card "Recent events" (last 50): table Time | Rule | Metric | Scope | Threshold | Period | Used | Limit; a progress bar for Used vs Limit is allowed. Empty texts "No data yet." and "No events yet.".
Dialog "Add alert" / "Edit alert — {name}": "Name"; select "Metric"; select "Scope" (tenant, project, key) with a dependent select "Project" ("Select project…") or "API key" ("Select API key…"); number "Threshold (%)" min 1 max 100 with hint "1–100. 80 = warning, 100 = critical."; select "Webhook" with "(no webhook)" and hint "No webhooks yet; alerts are still recorded in the event list." when none exist; edit-only checkbox "Detach webhook from this rule"; checkbox "Rule enabled"; buttons "Save alert" and "Cancel".
```

### S12 — Webhooks

```text
Apply the shared design system and app shell from section 3.
Screen: "Webhooks".
Page header: title "Webhooks", description "Outgoing notifications when an alert threshold is exceeded; payloads are signed with HMAC-SHA256 (X-Gateway-Signature header).", primary button "Add webhook".
Table columns: Name | URL (monospace small) | Secret ("…7890") | Status (active/inactive) | Updated | Actions ("Deliveries", "Edit", red "Delete"; viewer sees only "Deliveries").
Dialog "Add webhook" / "Edit webhook — {name}": "Name"; "URL" (placeholder https://example.com/hooks/gateway) with hint "https://host/path only — no ?query, user@, or #fragment. Private/loopback IP hosts are rejected."; password "Signing secret" (edit: "New signing secret (optional)") with hint "16–200 characters. The secret is never shown again after saving."; checkbox "Webhook enabled"; buttons "Save webhook" and "Cancel". Show URL validation errors, e.g. "URL must use https.".
Wide overlay "Deliveries — {name}": table Created | Status (badge: succeeded green, failed red, pending neutral) | Attempt | Code | Next attempt | Completed | Error; empty text "No deliveries yet.".
```

### S13 — Users (owner only)

```text
Apply the shared design system and app shell from section 3.
Screen: "Users" (visible to owner only).
Page header: title "Users", description "Manage tenant members. Only owners can change roles and invite users.", primary button "Invite user".
After inviting or resetting a password show the "secret shown once" panel with the token and buttons "Copy" and "Close".
Table columns: Email | Name | Role (inline select: owner, admin, viewer) | Status (inline switch/checkbox with text "active"/"inactive") | Last login | Created | Actions (button "Reset password"). Changes apply immediately without a confirmation dialog; show a subtle busy state on the row.
Dialog "Invite user": "Email", "Display name", select "Role" (owner, admin, viewer; default viewer); buttons "Create invitation" (loading "Sending…") and "Cancel".
```

### S14 — Audit

```text
Apply the shared design system and app shell from section 3.
Screen: "Audit" (read-only for all tenant roles).
Page header: title "Audit", description "Tenant configuration change trail.".
Toolbar: datetime-local "From", datetime-local "To", select "Rows per page" (25, 50, 100, 200).
Table columns: Time | User (id or "—") | Action (monospace, e.g. "provider.create", "key.revoke", "auth.login") | Entity | Entity ID (monospace) | IP | Detail (expandable "view" that reveals a monospace JSON block). Pager below ("Page 1 of 3 (120 rows)"). Empty text "No data yet.".
```

### S15 — Tenants (platform)

```text
Apply the shared design system and app shell from section 3.
Screen: "Tenants" (platform_admin only; platform sidebar). Page header: title "Tenants", description "Create tenants, set plans, and send owner invitations.", primary button "Add tenant".
Secret-once panel after creating an invitation: "Tenant owner invitation token — copy it now; this value is shown only once." with "Copy" and "Close".
Table columns: Name | Slug (monospace) | Status (active green, suspended red) | Plan | Tenant ID (monospace small) | Created (date) | Actions ("Edit", "Invite owner", "Reset password").
Dialogs (show as overlay states): "Add tenant" with "Tenant name", "Slug" (hint "Lowercase letters, numbers, and hyphens."), select "Plan" ("Select plan…"), "Owner email", "Owner name", button "Create tenant + invitation"; "Edit tenant — {name}" with "Name", select "Status" (active, suspended; hint "Suspending rejects all changes from the tenant (read-only)."), select "Plan", button "Save changes"; "Invite owner — {name}" with "Email", "Display name", button "Create owner invitation"; "Reset user password — {name}" with note "Enter the user ID (number) from that tenant's Users page.", field "User ID", button "Create reset token".
```

### S16 — Plans

```text
Apply the shared design system and app shell from section 3.
Screen: "Plans" (platform_admin). Page header: title "Plans", description "Quota limits per tenant. Empty means unlimited.", primary button "Add plan".
Table columns: Name | Max projects | Max API keys | Max requests/month | Max tokens/month | Actions ("Edit", red "Delete"). Unlimited values show "—".
Dialog "Add plan" / "Edit plan — {name}": "Plan name" and a 4-column grid of number inputs "Max projects" (hint "Empty = unlimited."), "Max API keys", "Max requests/month", "Max tokens/month"; buttons "Save plan" and "Cancel".
Rows: "Default" (all "—"), "Starter" (3 / 10 / 100,000 / 5,000,000), "Pro" (20 / 100 / 5,000,000 / 200,000,000).
```

### S17 — Provider templates

```text
Apply the shared design system and app shell from section 3.
Screen: "Provider templates" (platform_admin). Page header: title "Provider templates", description "Upstream templates used to create tenant providers; catalog sync uses the template sync URL.", primary button "Add template".
Table columns: Name | Code (monospace) | Type | Base URL | Auth ("Authorization Bearer") | Models path | Sync (kind + small URL) | Status (active/inactive) | Actions ("Edit", red "Delete").
Wide dialog "Add template" / "Edit template — {name}": row of "Code" (hint "Unique identifier, e.g. openai.", read-only on edit), "Name", select "Type" (openai); "Default base URL"; row of "Auth header", "Auth prefix", "Models path"; row of select "Sync kind" ("(none)", "opencode_config") and "Sync URL" (hint "Default for catalog sync."); checkbox "Template enabled"; buttons "Save template" and "Cancel".
```

### S18 — Model catalog

```text
Apply the shared design system and app shell from section 3.
Screen: "Model catalog" (platform_admin). Page header: title "Model catalog", description "Template catalog entries used by tenant providers when importing models.", primary button "Add entry" (disabled when no template is selected).
Card "Catalog sync": help text "Fetches the catalog from the OpenCode source. The service key is neither stored nor recorded in the audit log. Default URL: …"; inline form with password "Service key" (required), "URL" (hint "Leave empty to use the template sync URL."), button "Start sync" (loading "Syncing…"). Success banner: "New templates: 1; new models: 111; updated: 1; skipped (manual): 0; unsupported: 65.".
Toolbar: select "Template" ("OpenCode (opencode)").
Table columns: Upstream model (monospace) | Display name | Family | Context | Max in/out | Price in/out per 1M ("0.3 / 1.2 USD") | Tiers ("1 tier" or "—") | Features ("tools, vision, reasoning" or "—") | Source (manual/…) | Status | Actions ("Edit", red "Delete").
```

### S19 — Platform usage

```text
Apply the shared design system and app shell from section 3.
Screen: "Platform usage" (platform_admin). Page header: title "Platform usage", description "Aggregate usage per tenant.".
Toolbar: date "From", date "To" (changing them reloads automatically; no submit button).
Table columns: Tenant | Requests | Denied | Errors | Input tokens | Output tokens | Cost ("USD 0.0000"). A horizontal bar per row for share of cost is allowed. Empty text "No data yet.".
```

### S20 — Platform audit

```text
Apply the shared design system and app shell from section 3.
Screen: "Platform audit" (platform_admin). Page header: title "Platform audit", description "Installation-wide audit (platform and tenant actions), without payload details.".
Toolbar: datetime-local "From", datetime-local "To", select "Rows per page" (25, 50, 100, 200).
Table columns: Time | User | Action (monospace, e.g. "tenant.create", "plan.update") | Entity | Entity ID | IP. No detail column. Pager below. Empty text "No data yet.".
```

### S21 — State sheet (empty, loading, error, not allowed)

```text
Apply the shared design system and app shell from section 3.
Screen: a reference sheet of shared states, laid out as a grid of small cards: loading text "Loading…" (with a skeleton table); table empty row "No data yet."; error banner (red left border) with a server message; success banner; warning banner; info banner; not-found/forbidden banner "Page “/x” was not found or is not available for your role."; viewer row action cell with muted "read-only"; the "secret shown once" panel; confirmation dialog text 'Revoke key "ci-key"? Clients using it will be rejected immediately.' with buttons "Cancel" and a red confirm button.
```

---

---

## 5. Overlay dialogs (O1–O4)

Each overlay is a modal dialog opened from its parent screen (not a separate page).

### O1 — Add provider

```text
Apply the shared design system and app shell from section 3.
Screen: wide modal "Add provider" over the Providers table. A fieldset "Provider type" with two radio cards: "From template" (selected) and "Custom".
Template mode fields: select "Template" ("Select template…", options like "OpenCode (opencode)") with hint "Platform catalog template; base URL and headers can be overridden."; "Provider name" (placeholder = template name); "Base URL" (placeholder = template default, hint "Default: https://opencode.ai/inference/openai/v1/"); password "API key" with hint "Stored encrypted; never shown again.".
Custom mode fields: "Provider name", "Base URL" (placeholder "https://api.example.com/v1"), "API key", "Auth header" (hint "Example: Authorization."), "Auth prefix" (hint "Example: Bearer."), "Models path" (hint "Example: /models for discovery.").
Footer: primary "Save provider" (loading "Saving…"), ghost "Cancel".
```

### O2 — Test discovery / Import models

```text
Apply the shared design system and app shell from section 3.
Screen: wide modal "Discovery — opencode" (the same layout is reused for "Import from catalog — opencode"). Loading state "Contacting provider…". Then a toolbar with buttons "Select all" and "Clear" and the text "3 of 12 selected", followed by a scrollable checklist of upstream model names (monospace). Footer: primary "Import 3 models" (disabled when none selected; loading "Importing…") and ghost "Close". Success banner: "Import complete: 3 models created, 0 skipped.". Empty state: "The provider returned no models.". For the catalog variant add the notes "Only active catalog entries of the openai_chat family can be imported." and, for custom providers, "Custom providers have no catalog template. Use “Test discovery” to import models from the upstream.".
```

### O3 — Add model (routes + tiered pricing)

```text
Apply the shared design system and app shell from section 3.
Screen: wide modal "Add model". Fields: "Alias" (hint "The model name clients use, e.g. gpt-4o-mini."), "Description", "Max output tokens".
Fieldset "Routes" with hint "Lower priority numbers are tried first; weight sets the load split.": repeatable rows of select "Provider", "Upstream model", number "Priority", number "Weight", ghost "Remove"; ghost button "Add route".
Checkbox "Include initial price" (checked) revealing: "Input / 1M", "Output / 1M", "Cache read / 1M", "Cache write / 1M", "Currency" (USD), and a fieldset "Price tiers" with hint "Price per 1 million tokens for long prompts; the base tier is the main price above." containing repeatable rows "Starts at input tokens", "Input / 1M", "Output / 1M", "Cache read", "Cache write", ghost "Remove", and a ghost button "Add tier".
Validation message: "At least one route with a provider and an upstream model.". Footer: primary "Save model", ghost "Cancel".
```

### O4 — Catalog entry

```text
Apply the shared design system and app shell from section 3.
Screen: wide modal "Add catalog entry" / "Edit catalog entry — {model}". Grid of 4 columns: row 1 select "Template", "Upstream model" (hint "Model name on the provider side."), "Display name", select "API family" (openai_chat, openai_responses, anthropic_messages, google_generate); row 2 "Context window", "Max input tokens", "Max output tokens", "Currency" (USD); row 3 "Input / 1M", "Output / 1M", "Cache read / 1M", "Cache write / 1M". Fieldset "Price tiers" (repeatable rows "Starts at input tokens", "Input / 1M", "Output / 1M", "Cache read", "Cache write", "Remove"; button "Add tier"). Checkbox group: "Supports tools", "Supports vision", "Supports reasoning", "Entry enabled". Muted note: "Entry source is always “manual” when saved from the UI.". Footer "Save entry" and "Cancel".
```

---

---

## 6. Sample data (seed for the fake data layer)

Use this data so the screens feel real (no lorem ipsum).

- Tenant: "Acme Studio" (slug `acme-studio`), plan "Pro". Platform tenants: "Acme Studio" (active), "Budi Labs" (active), "Demo Corp" (suspended).
- Users: Rina Putri `rina@acme.id` (owner), Dimas Pratama `dimas@acme.id` (admin), Sari Wulandari `sari@acme.id` (viewer, inactive).
- Providers: `opencode` (template `opencode`, `https://opencode.ai/inference/openai/v1/`, key `…9x2f`, active); `internal-llm` (custom, inactive).
- Models: `deepseek-v4.1-flash` → opencode → `deepseek-v4.1-flash` (p1/w1), price 0.3 / 1.2 USD per 1M; `glm-5.3-flash` → opencode → `glm-5.3-flash` (p1/w1), price 0.1 / 0.4.
- Projects: `chatbot-prod` (active, content logging yes, retention 30 days), `internal-tools` (active, content logging no), `sandbox` (suspended).
- API keys: `web-app` (prefix `gw_a1b2c3d4e5…`, project chatbot-prod, all IPs, last used today), `ci-key` (prefix `gw_f6e5d4c3b2…`, IP 203.0.113.10, expires 2026-12-31), `old-key` (revoked).
- 7-day usage: requests 1,240 / 1,310 / 980 / 1,505 / 1,720 / 890 / 1,110; denied 12; errors 4; input tokens 3,420,000; output tokens 1,980,000; cost USD 3.4125.
- Request log rows: status `ok` 200, input 842, output 311, cache 0, cost USD 0.0006, latency 812 ms, attempt 1; one `denied` 429 with reason `daily_quota_exceeded:key`; one `error` 502 with reason `upstream_failed`.
- Policies: tenant — monthly quota 200,000,000 tokens, monthly budget 150; project `chatbot-prod` — max tokens/req 4,096, req/min 120, allowed models `deepseek-v4.1-flash`.
- Alerts: "Monthly quota 80%" (monthly_tokens, tenant, 80%, webhook "ops-slack"), "Critical budget" (monthly_budget, tenant, 100%).
- Webhook: "ops-slack" `https://hooks.acme.id/gateway`, secret `…7890`, active; deliveries: succeeded (code 200), failed (code 500, "HTTP 500", attempt 3).
- Audit actions: `provider.create`, `key.create`, `key.revoke`, `policy.update`, `auth.login`, `user.invite`.
- Plans: see S16. Template: `opencode` (Authorization Bearer, models path `/models`, sync `opencode_config`).

Data-layer requirements: build these as plain JS arrays in `js/data.js`. Add at least 60 deterministic (seeded) usage log rows spread over the last 7 days (mix of ok / error / denied, several projects, models, and keys), 25 audit rows per tenant plus 40 platform audit rows, 12 catalog models for the "opencode" template (include one with tiers and different feature flags), 3 plans, 3 tenants, the users, providers, models, projects, keys, policies, alert rules + events, webhooks + deliveries listed above. Dashboard, Usage, and Platform usage must derive from the same usage data so totals agree.
