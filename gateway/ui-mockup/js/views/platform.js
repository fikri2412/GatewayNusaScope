/* Platform views (S15-S20) + catalog entry dialog (O4).
   Classic script; relies on window.GW and GW.C being defined by app.js/components.js. */
(function () {
  'use strict';

  var GW = window.GW;
  var C = GW.C;
  var V = (GW.views = GW.views || {});
  GW.mounts = GW.mounts || {};

  /* ------------------------------------------------------------------ */
  /* local helpers                                                       */
  /* ------------------------------------------------------------------ */

  function e(v) { return C.e(v === null || v === undefined ? '' : v); }

  function mono(v) {
    return '<span class="mono">' + e(v === null || v === undefined || v === '' ? '—' : v) + '</span>';
  }

  function byId(list, id) {
    var n = Number(id);
    list = list || [];
    for (var i = 0; i < list.length; i++) if (list[i].id === n) return list[i];
    return null;
  }

  function fieldValue(form, name) {
    var el = form.elements[name];
    return el ? String(el.value).trim() : '';
  }

  function numOrNull(form, name) {
    var v = fieldValue(form, name);
    return v === '' ? null : Number(v);
  }

  function numOrDash(v) { return v === null || v === undefined ? '—' : C.num(v); }

  function isChecked(form, name) {
    var el = form.elements[name];
    return !!(el && el.checked);
  }

  // C.alert returns banner markup; tolerate an imperative implementation too.
  function banner(kind, text) {
    var out = C.alert ? C.alert(kind, text) : '';
    return typeof out === 'string' ? out : '';
  }

  // Per contract C.secret displays the panel itself; render first so a fresh
  // view does not wipe it, then let the component place the panel.
  function reveal(title, value) {
    GW.render();
    C.secret(title, value);
  }

  function rowActions(html) { return '<div class="row-actions">' + html + '</div>'; }

  function planOptions() {
    return GW.data.plans.map(function (p) { return { value: p.id, label: p.name }; });
  }

  function templateOptions() {
    var list = GW.data.templates || [];
    if (!list.length) return [{ value: '', label: '(no templates)' }];
    return list.map(function (t) { return { value: t.id, label: t.name + ' (' + t.code + ')' }; });
  }

  function toMs(v) {
    if (!v) return null;
    var t = new Date(v).getTime();
    return isNaN(t) ? null : t;
  }

  var INVITE_TITLE = 'Tenant owner invitation token — copy it now; this value is shown only once.';
  var RESET_TITLE = 'Password reset token — copy it now; this value is shown only once.';

  /* ------------------------------------------------------------------ */
  /* S15 - Tenants                                                       */
  /* ------------------------------------------------------------------ */

  V['/platform/tenants'] = function () {
    var rows = GW.data.tenants.map(function (t) {
      var plan = byId(GW.data.plans, t.planId);
      return [
        e(t.name),
        mono(t.slug),
        C.badge(t.status),
        plan ? e(plan.name) : '—',
        mono(t.id),
        C.date(t.created),
        rowActions(
          C.button('Edit', function () { tenantEdit(t.id); }) +
          C.button('Invite owner', function () { tenantInvite(t.id); }) +
          C.button('Reset password', function () { tenantReset(t.id); })
        )
      ];
    });
    return C.page('Tenants', 'Create tenants, set plans, and send owner invitations.',
        C.button('Add tenant', tenantAdd, 'primary')) +
      C.table(['Name', 'Slug', 'Status', 'Plan', 'Tenant ID', 'Created', 'Actions'], rows);
  };

  function tenantAdd() {
    C.modal({
      title: 'Add tenant',
      body:
        C.field('name', 'Tenant name', 'text', '', { required: true }) +
        C.field('slug', 'Slug', 'text', '', { required: true, hint: 'Lowercase letters, numbers, and hyphens.' }) +
        C.field('planId', 'Plan', 'select', '', {
          required: true,
          options: [{ value: '', label: 'Select plan…' }].concat(planOptions())
        }) +
        C.field('ownerEmail', 'Owner email', 'email', '', { required: true }) +
        C.field('ownerName', 'Owner name', 'text', '', { required: true }),
      submit: 'Create tenant + invitation',
      validate: function (form) {
        return /^[a-z0-9-]+$/.test(fieldValue(form, 'slug'))
          ? ''
          : 'Slug must use lowercase letters, numbers, and hyphens.';
      },
      onSubmit: function (form) {
        var t = {
          id: GW.nextId(GW.data.tenants),
          name: fieldValue(form, 'name'),
          slug: fieldValue(form, 'slug'),
          status: 'active',
          planId: Number(fieldValue(form, 'planId')),
          created: GW.now
        };
        GW.data.tenants.push(t);
        GW.record('tenant.create', 'tenant', t.id, { slug: t.slug });
        reveal(INVITE_TITLE, GW.secret('inv'));
      }
    });
  }

  function tenantEdit(id) {
    var t = byId(GW.data.tenants, id);
    if (!t) return;
    C.modal({
      title: 'Edit tenant — ' + t.name,
      body:
        C.field('name', 'Name', 'text', t.name, { required: true }) +
        C.field('status', 'Status', 'select', t.status, {
          options: [{ value: 'active', label: 'active' }, { value: 'suspended', label: 'suspended' }],
          hint: 'Suspending rejects all changes from the tenant (read-only).'
        }) +
        C.field('planId', 'Plan', 'select', t.planId, { options: planOptions() }),
      submit: 'Save changes',
      onSubmit: function (form) {
        t.name = fieldValue(form, 'name');
        t.status = fieldValue(form, 'status');
        t.planId = Number(fieldValue(form, 'planId'));
        GW.record('tenant.update', 'tenant', t.id, { status: t.status });
        GW.render();
      }
    });
  }

  function tenantInvite(id) {
    var t = byId(GW.data.tenants, id);
    if (!t) return;
    C.modal({
      title: 'Invite owner — ' + t.name,
      body:
        C.field('email', 'Email', 'email', '', { required: true }) +
        C.field('name', 'Display name', 'text', '', { required: true }),
      submit: 'Create owner invitation',
      onSubmit: function (form) {
        GW.record('user.invite', 'tenant', t.id, { email: fieldValue(form, 'email') });
        reveal(INVITE_TITLE, GW.secret('inv'));
      }
    });
  }

  function tenantReset(id) {
    var t = byId(GW.data.tenants, id);
    if (!t) return;
    C.modal({
      title: 'Reset user password — ' + t.name,
      body:
        '<p class="muted">Enter the user ID (number) from that tenant\'s Users page.</p>' +
        C.field('userId', 'User ID', 'number', '', { required: true, min: 1 }),
      submit: 'Create reset token',
      onSubmit: function (form) {
        GW.record('user.reset', 'user', numOrNull(form, 'userId'), { tenantId: t.id });
        reveal(RESET_TITLE, GW.secret('rst'));
      }
    });
  }

  /* ------------------------------------------------------------------ */
  /* S16 - Plans                                                         */
  /* ------------------------------------------------------------------ */

  V['/platform/plans'] = function () {
    var rows = GW.data.plans.map(function (p) {
      return [
        e(p.name),
        numOrDash(p.maxProjects),
        numOrDash(p.maxKeys),
        numOrDash(p.maxRequests),
        numOrDash(p.maxTokens),
        rowActions(
          C.button('Edit', function () { planEdit(p.id); }) +
          C.button('Delete', function () { planDelete(p.id); }, 'danger')
        )
      ];
    });
    return C.page('Plans', 'Quota limits per tenant. Empty means unlimited.',
        C.button('Add plan', planAdd, 'primary')) +
      C.table(['Name', 'Max projects', 'Max API keys', 'Max requests/month', 'Max tokens/month', 'Actions'], rows);
  };

  function planFields(p) {
    p = p || {};
    return C.field('name', 'Plan name', 'text', p.name || '', { required: true }) +
      '<div class="form-grid">' +
      C.field('maxProjects', 'Max projects', 'number', p.maxProjects == null ? '' : p.maxProjects, { min: 0, hint: 'Empty = unlimited.' }) +
      C.field('maxKeys', 'Max API keys', 'number', p.maxKeys == null ? '' : p.maxKeys, { min: 0 }) +
      C.field('maxRequests', 'Max requests/month', 'number', p.maxRequests == null ? '' : p.maxRequests, { min: 0 }) +
      C.field('maxTokens', 'Max tokens/month', 'number', p.maxTokens == null ? '' : p.maxTokens, { min: 0 }) +
      '</div>';
  }

  function planSave(form, p) {
    p.name = fieldValue(form, 'name');
    p.maxProjects = numOrNull(form, 'maxProjects');
    p.maxKeys = numOrNull(form, 'maxKeys');
    p.maxRequests = numOrNull(form, 'maxRequests');
    p.maxTokens = numOrNull(form, 'maxTokens');
  }

  function planAdd() {
    C.modal({
      title: 'Add plan',
      body: planFields(null),
      submit: 'Save plan',
      onSubmit: function (form) {
        var p = { id: GW.nextId(GW.data.plans) };
        planSave(form, p);
        GW.data.plans.push(p);
        GW.record('plan.create', 'plan', p.id, { name: p.name });
        GW.render();
      }
    });
  }

  function planEdit(id) {
    var p = byId(GW.data.plans, id);
    if (!p) return;
    C.modal({
      title: 'Edit plan — ' + p.name,
      body: planFields(p),
      submit: 'Save plan',
      onSubmit: function (form) {
        planSave(form, p);
        GW.record('plan.update', 'plan', p.id, { name: p.name });
        GW.render();
      }
    });
  }

  function planDelete(id) {
    var p = byId(GW.data.plans, id);
    if (!p) return;
    C.confirm('Delete plan “' + p.name + '”?', function () {
      var i = GW.data.plans.indexOf(p);
      if (i >= 0) GW.data.plans.splice(i, 1);
      GW.record('plan.delete', 'plan', p.id, {});
      GW.render();
    }, 'Delete');
  }

  /* ------------------------------------------------------------------ */
  /* S17 - Provider templates                                            */
  /* ------------------------------------------------------------------ */

  V['/platform/templates'] = function () {
    var rows = GW.data.templates.map(function (t) {
      var auth = (t.authHeader || t.authPrefix)
        ? e([t.authHeader, t.authPrefix].filter(Boolean).join(' '))
        : '—';
      var sync = t.syncKind
        ? e(t.syncKind) + (t.syncUrl ? '<div class="muted mono">' + e(t.syncUrl) + '</div>' : '')
        : '—';
      return [
        e(t.name),
        mono(t.code),
        e(t.type),
        '<span class="mono">' + e(t.baseUrl) + '</span>',
        auth,
        '<span class="mono">' + e(t.modelsPath || '—') + '</span>',
        sync,
        C.badge(t.enabled ? 'active' : 'inactive'),
        rowActions(
          C.button('Edit', function () { templateEdit(t.id); }) +
          C.button('Delete', function () { templateDelete(t.id); }, 'danger')
        )
      ];
    });
    return C.page('Provider templates', 'Upstream templates used to create tenant providers; catalog sync uses the template sync URL.',
        C.button('Add template', templateAdd, 'primary')) +
      C.table(['Name', 'Code', 'Type', 'Base URL', 'Auth', 'Models path', 'Sync', 'Status', 'Actions'], rows);
  };

  function templateFields(t, isEdit) {
    t = t || {};
    return '<div class="form-grid">' +
      C.field('code', 'Code', 'text', t.code || '', isEdit
        ? { readonly: true, hint: 'Unique identifier, e.g. openai.' }
        : { required: true, hint: 'Unique identifier, e.g. openai.' }) +
      C.field('name', 'Name', 'text', t.name || '', { required: true }) +
      C.field('type', 'Type', 'select', t.type || 'openai', { options: ['openai'] }) +
      '</div>' +
      C.field('baseUrl', 'Default base URL', 'text', t.baseUrl || '', { required: true }) +
      '<div class="form-grid">' +
      C.field('authHeader', 'Auth header', 'text', t.authHeader || '') +
      C.field('authPrefix', 'Auth prefix', 'text', t.authPrefix || '') +
      C.field('modelsPath', 'Models path', 'text', t.modelsPath || '') +
      '</div>' +
      '<div class="form-grid">' +
      C.field('syncKind', 'Sync kind', 'select', t.syncKind || '', {
        options: [{ value: '', label: '(none)' }, { value: 'opencode_config', label: 'opencode_config' }]
      }) +
      C.field('syncUrl', 'Sync URL', 'text', t.syncUrl || '', { hint: 'Default for catalog sync.' }) +
      '</div>' +
      C.field('enabled', 'Template enabled', 'checkbox', t.enabled !== false);
  }

  function templateSave(form, t) {
    t.name = fieldValue(form, 'name');
    t.type = fieldValue(form, 'type') || 'openai';
    t.baseUrl = fieldValue(form, 'baseUrl');
    t.authHeader = fieldValue(form, 'authHeader');
    t.authPrefix = fieldValue(form, 'authPrefix');
    t.modelsPath = fieldValue(form, 'modelsPath');
    t.syncKind = fieldValue(form, 'syncKind');
    t.syncUrl = fieldValue(form, 'syncUrl');
    t.enabled = isChecked(form, 'enabled');
  }

  function templateAdd() {
    C.modal({
      title: 'Add template',
      wide: true,
      body: templateFields(null, false),
      submit: 'Save template',
      onSubmit: function (form) {
        var t = { id: GW.nextId(GW.data.templates), code: fieldValue(form, 'code') };
        templateSave(form, t);
        GW.data.templates.push(t);
        GW.record('template.create', 'template', t.id, { code: t.code });
        GW.render();
      }
    });
  }

  function templateEdit(id) {
    var t = byId(GW.data.templates, id);
    if (!t) return;
    C.modal({
      title: 'Edit template — ' + t.name,
      wide: true,
      body: templateFields(t, true),
      submit: 'Save template',
      onSubmit: function (form) {
        templateSave(form, t);
        GW.record('template.update', 'template', t.id, { code: t.code });
        GW.render();
      }
    });
  }

  function templateDelete(id) {
    var t = byId(GW.data.templates, id);
    if (!t) return;
    C.confirm('Delete template “' + t.code + '”?', function () {
      var i = GW.data.templates.indexOf(t);
      if (i >= 0) GW.data.templates.splice(i, 1);
      GW.record('template.delete', 'template', t.id, { code: t.code });
      GW.render();
    }, 'Delete');
  }

  /* ------------------------------------------------------------------ */
  /* S18 - Model catalog (+ O4 catalog entry dialog)                     */
  /* ------------------------------------------------------------------ */

  var SYNC_RESULT = 'New templates: 1; new models: 111; updated: 1; skipped (manual): 0; unsupported: 65.';
  var SYNCED_UPSTREAM = 'opencode/zen-flash';

  V['/platform/catalog'] = function () {
    var s = GW.viewState('/platform/catalog', { templateId: null, syncResult: '' });
    if (s.templateId == null && GW.data.templates.length) s.templateId = GW.data.templates[0].id;
    if (s.templateId != null && !byId(GW.data.templates, s.templateId)) {
      s.templateId = GW.data.templates.length ? GW.data.templates[0].id : null;
    }
    var tpl = byId(GW.data.templates, s.templateId);
    var rows = GW.data.catalog
      .filter(function (m) { return m.templateId === s.templateId; })
      .map(catalogRow);

    return C.page('Model catalog', 'Template catalog entries used by tenant providers when importing models.',
        C.button('Add entry', function () { catalogAdd(s.templateId); }, 'primary', s.templateId == null ? 'disabled' : '')) +
      C.card('Catalog sync',
        '<p class="muted">Fetches the catalog from the OpenCode source. The service key is neither stored nor recorded in the audit log. Default URL: <span class="mono">' +
          e(tpl && tpl.syncUrl ? tpl.syncUrl : '—') + '</span></p>' +
        (s.syncResult ? banner('success', s.syncResult) : '') +
        C.form(
          C.field('serviceKey', 'Service key', 'password', '', { required: true }) +
          C.field('url', 'URL', 'text', '', { hint: 'Leave empty to use the template sync URL.' }),
          'Start sync',
          function (form) { catalogSync(s, tpl, fieldValue(form, 'url')); },
          {
            loading: 'Syncing…',
            validate: function (form) {
              if (!tpl) return 'Select a template.';
              return fieldValue(form, 'serviceKey') ? '' : 'Service key is required.';
            }
          })) +
      '<div class="toolbar">' +
        C.field('templateId', 'Template', 'select', s.templateId, {
          options: templateOptions(),
          onChange: function (v) { s.templateId = v === '' ? null : Number(v); GW.render(); }
        }) +
      '</div>' +
      C.table(['Upstream model', 'Display name', 'Family', 'Context', 'Max in/out',
        'Price in/out per 1M', 'Tiers', 'Features', 'Source', 'Status', 'Actions'], rows);
  };

  function catalogRow(m) {
    var price = (m.inputPrice == null && m.outputPrice == null)
      ? '—'
      : String(m.inputPrice == null ? 0 : m.inputPrice) + ' / ' +
        String(m.outputPrice == null ? 0 : m.outputPrice) + ' ' + (m.currency || 'USD');
    var tiers = m.tiers && m.tiers.length
      ? m.tiers.length + ' tier' + (m.tiers.length === 1 ? '' : 's')
      : '—';
    var features = [
      m.tools ? 'tools' : '',
      m.vision ? 'vision' : '',
      m.reasoning ? 'reasoning' : ''
    ].filter(Boolean).join(', ') || '—';
    return [
      mono(m.upstream),
      e(m.name),
      e(m.family),
      m.context == null ? '—' : C.num(m.context),
      (m.maxInput == null ? '—' : C.num(m.maxInput)) + ' / ' + (m.maxOutput == null ? '—' : C.num(m.maxOutput)),
      e(price),
      e(tiers),
      e(features),
      e(m.source),
      C.badge(m.enabled ? 'active' : 'inactive'),
      rowActions(
        C.button('Edit', function () { catalogEdit(m.id); }) +
        C.button('Delete', function () { catalogDelete(m.id); }, 'danger')
      )
    ];
  }

  function catalogSync(s, tpl, url) {
    var templateId = tpl ? tpl.id : null;
    var target = url || (tpl && tpl.syncUrl) || '';
    runSync(templateId, target);
    s.syncResult = SYNC_RESULT;
    GW.render();
  }

  // Deterministic, in-memory only - no network. The banner counts are the spec's
  // fixed sample copy; the mutation is bounded to one synced entry per template so
  // repeated syncs refresh that entry instead of fabricating 111 rows.
  function runSync(templateId, url) {
    if (templateId == null) return;
    var entry = null;
    GW.data.catalog.forEach(function (m) {
      if (m.templateId === templateId && m.upstream === SYNCED_UPSTREAM) entry = m;
    });
    var fresh = {
      templateId: templateId,
      upstream: SYNCED_UPSTREAM,
      name: 'Zen Flash',
      family: 'openai_chat',
      context: 131072,
      maxInput: 122880,
      maxOutput: 8192,
      inputPrice: 0.2,
      outputPrice: 0.8,
      cacheRead: 0.05,
      cacheWrite: 0.1,
      currency: 'USD',
      tiers: [],
      tools: true,
      vision: true,
      reasoning: false,
      source: 'opencode_config',
      enabled: true
    };
    if (entry) {
      Object.assign(entry, fresh);
    } else {
      GW.data.catalog.push(Object.assign({ id: GW.nextId(GW.data.catalog) }, fresh));
    }
    GW.record('catalog.sync', 'template', templateId, { url: url });
  }

  function catalogFields(m, templateId) {
    m = m || {};
    var tpl = m.templateId != null ? m.templateId : templateId;
    function f(name, label, type, value, opts) {
      return C.field(name, label, type, value == null ? '' : value, opts || {});
    }
    return '<div class="form-grid">' +
      f('templateId', 'Template', 'select', tpl, { options: templateOptions(), required: true }) +
      f('upstream', 'Upstream model', 'text', m.upstream, { required: true, hint: 'Model name on the provider side.' }) +
      f('name', 'Display name', 'text', m.name, { required: true }) +
      f('family', 'API family', 'select', m.family || 'openai_chat', {
        options: ['openai_chat', 'openai_responses', 'anthropic_messages', 'google_generate']
      }) +
      '</div>' +
      '<div class="form-grid">' +
      f('context', 'Context window', 'number', m.context, { min: 0 }) +
      f('maxInput', 'Max input tokens', 'number', m.maxInput, { min: 0 }) +
      f('maxOutput', 'Max output tokens', 'number', m.maxOutput, { min: 0 }) +
      f('currency', 'Currency', 'text', m.currency || 'USD') +
      '</div>' +
      '<div class="form-grid">' +
      f('inputPrice', 'Input / 1M', 'number', m.inputPrice, { min: 0, step: 'any' }) +
      f('outputPrice', 'Output / 1M', 'number', m.outputPrice, { min: 0, step: 'any' }) +
      f('cacheRead', 'Cache read / 1M', 'number', m.cacheRead, { min: 0, step: 'any' }) +
      f('cacheWrite', 'Cache write / 1M', 'number', m.cacheWrite, { min: 0, step: 'any' }) +
      '</div>' +
      '<fieldset><legend>Price tiers</legend>' +
        '<div data-catalog-tiers>' + tierRowsHTML(m.tiers) + '</div>' +
        C.button('Add tier', tierAdd) +
      '</fieldset>' +
      '<div class="check-row">' +
      f('tools', 'Supports tools', 'checkbox', !!m.tools) +
      f('vision', 'Supports vision', 'checkbox', !!m.vision) +
      f('reasoning', 'Supports reasoning', 'checkbox', !!m.reasoning) +
      f('enabled', 'Entry enabled', 'checkbox', m.enabled !== false) +
      '</div>' +
      '<p class="muted">Entry source is always “manual” when saved from the UI.</p>';
  }

  function tierRowsHTML(tiers) {
    var html = '';
    (tiers || []).forEach(function (t, i) { html += tierRowHTML(i, t); });
    return html;
  }

  function tierRowHTML(idx, t) {
    t = t || {};
    function f(name, label, value) {
      return C.field('tier' + name + '_' + idx, label, 'number', value == null ? '' : value,
        { min: 0, step: 'any' });
    }
    return '<div class="tier-row" data-tier-row>' +
      f('Starts', 'Starts at input tokens', t.starts) +
      f('Input', 'Input / 1M', t.input) +
      f('Output', 'Output / 1M', t.output) +
      f('CacheRead', 'Cache read', t.cacheRead) +
      f('CacheWrite', 'Cache write', t.cacheWrite) +
      C.button('Remove', tierRemove) +
      '</div>';
  }

  function tierAdd(event) {
    if (event && event.preventDefault) event.preventDefault();
    var btn = event && event.target ? event.target.closest('button') : null;
    var dialog = btn ? btn.closest('dialog') : null;
    if (!dialog) return;
    var box = dialog.querySelector('[data-catalog-tiers]');
    if (!box) return;
    var seq = Number(dialog.dataset.tierSeq || 0);
    dialog.dataset.tierSeq = String(seq + 1);
    box.insertAdjacentHTML('beforeend', tierRowHTML(seq));
  }

  function tierRemove(event) {
    if (event && event.preventDefault) event.preventDefault();
    var btn = event && event.target ? event.target.closest('button') : null;
    var row = btn ? btn.closest('[data-tier-row]') : null;
    if (row) row.remove();
  }

  function numFrom(input) {
    if (!input) return null;
    var v = String(input.value).trim();
    return v === '' ? null : Number(v);
  }

  function readTiers(dialog) {
    var out = [];
    Array.prototype.forEach.call(dialog.querySelectorAll('[data-tier-row]'), function (row) {
      var ins = row.querySelectorAll('input');
      out.push({
        starts: numFrom(ins[0]),
        input: numFrom(ins[1]),
        output: numFrom(ins[2]),
        cacheRead: numFrom(ins[3]),
        cacheWrite: numFrom(ins[4])
      });
    });
    return out;
  }

  function catalogAdd(templateId) {
    C.modal({
      title: 'Add catalog entry',
      wide: true,
      body: catalogFields(null, templateId),
      submit: 'Save entry',
      onOpen: function (dialog) { dialog.dataset.tierSeq = '0'; },
      onSubmit: function (form, dialog) { catalogSave(form, dialog, null); }
    });
  }

  function catalogEdit(id) {
    var m = byId(GW.data.catalog, id);
    if (!m) return;
    C.modal({
      title: 'Edit catalog entry — ' + m.upstream,
      wide: true,
      body: catalogFields(m),
      submit: 'Save entry',
      onOpen: function (dialog) { dialog.dataset.tierSeq = String((m.tiers || []).length); },
      onSubmit: function (form, dialog) { catalogSave(form, dialog, m); }
    });
  }

  function catalogSave(form, dialog, existing) {
    var m = existing || { id: GW.nextId(GW.data.catalog) };
    m.templateId = Number(fieldValue(form, 'templateId'));
    m.upstream = fieldValue(form, 'upstream');
    m.name = fieldValue(form, 'name');
    m.family = fieldValue(form, 'family') || 'openai_chat';
    m.context = numOrNull(form, 'context');
    m.maxInput = numOrNull(form, 'maxInput');
    m.maxOutput = numOrNull(form, 'maxOutput');
    m.currency = fieldValue(form, 'currency') || 'USD';
    m.inputPrice = numOrNull(form, 'inputPrice');
    m.outputPrice = numOrNull(form, 'outputPrice');
    m.cacheRead = numOrNull(form, 'cacheRead');
    m.cacheWrite = numOrNull(form, 'cacheWrite');
    m.tiers = readTiers(dialog);
    m.tools = isChecked(form, 'tools');
    m.vision = isChecked(form, 'vision');
    m.reasoning = isChecked(form, 'reasoning');
    m.enabled = isChecked(form, 'enabled');
    m.source = 'manual'; // spec: source is always "manual" when saved from the UI
    if (!existing) GW.data.catalog.push(m);
    GW.record(existing ? 'catalog.update' : 'catalog.create', 'catalog', m.id, { upstream: m.upstream });
    GW.render();
  }

  function catalogDelete(id) {
    var m = byId(GW.data.catalog, id);
    if (!m) return;
    C.confirm('Delete catalog entry “' + m.upstream + '”?', function () {
      var i = GW.data.catalog.indexOf(m);
      if (i >= 0) GW.data.catalog.splice(i, 1);
      GW.record('catalog.delete', 'catalog', m.id, { upstream: m.upstream });
      GW.render();
    }, 'Delete');
  }

  /* ------------------------------------------------------------------ */
  /* S19 - Platform usage                                                */
  /* ------------------------------------------------------------------ */

  V['/platform/usage'] = function () {
    var s = GW.viewState('/platform/usage', { from: '2026-10-01', to: '2026-10-07' });
    var rows = GW.data.usage.filter(function (r) {
      var d = String(r.time || '').slice(0, 10);
      if (s.from && d < s.from) return false;
      if (s.to && d > s.to) return false;
      return true;
    });
    var agg = GW.aggregate(rows, function (r) { return r.tenantId; });
    agg.sort(function (a, b) { return b.cost - a.cost; });
    var totalCost = agg.reduce(function (sum, a) { return sum + a.cost; }, 0);
    var cells = agg.map(function (a) {
      var t = byId(GW.data.tenants, a.name);
      var share = totalCost > 0 ? Math.min(100, (a.cost / totalCost) * 100) : 0;
      return [
        t ? e(t.name) : e(a.name),
        C.num(a.requests),
        C.num(a.denied),
        C.num(a.errors),
        C.num(a.input),
        C.num(a.output),
        C.money(a.cost) + '<progress class="progress" max="100" value="' + (Math.round(share * 10) / 10) + '"></progress>'
      ];
    });
    return C.page('Platform usage', 'Aggregate usage per tenant.') +
      '<div class="toolbar">' +
        C.field('from', 'From', 'date', s.from, {
          onChange: function (v) { s.from = v; GW.render(); }
        }) +
        C.field('to', 'To', 'date', s.to, {
          onChange: function (v) { s.to = v; GW.render(); }
        }) +
      '</div>' +
      C.table(['Tenant', 'Requests', 'Denied', 'Errors', 'Input tokens', 'Output tokens', 'Cost'],
        cells, { empty: 'No data yet.' });
  };

  /* ------------------------------------------------------------------ */
  /* S20 - Platform audit                                                */
  /* ------------------------------------------------------------------ */

  V['/platform/audit'] = function () {
    var s = GW.viewState('/platform/audit', { page: 1, size: '25', from: '', to: '' });
    var fromMs = toMs(s.from);
    var toMsVal = toMs(s.to);

    var list = (GW.data.platformAudit || []).slice().sort(function (a, b) {
      return String(b.time || '').localeCompare(String(a.time || ''));
    });
    if (fromMs != null || toMsVal != null) {
      list = list.filter(function (r) {
        var t = new Date(r.time).getTime();
        if (fromMs != null && t < fromMs) return false;
        if (toMsVal != null && t > toMsVal) return false;
        return true;
      });
    }

    var pages = Math.max(1, Math.ceil(list.length / s.size));
    if (s.page > pages) s.page = pages;
    var pageRows = list.slice((s.page - 1) * s.size, s.page * s.size);

    // No detail column on this screen (S20).
    var cells = pageRows.map(function (r) {
      return [
        C.date(r.time),
        r.userId == null || r.userId === '' ? '—' : mono(r.userId),
        mono(r.action),
        r.entity == null || r.entity === '' ? '—' : e(r.entity),
        r.entityId == null || r.entityId === '' ? '—' : mono(r.entityId),
        r.ip == null || r.ip === '' ? '—' : e(r.ip)
      ];
    });

    return C.page('Platform audit', 'Installation-wide audit (platform and tenant actions), without payload details.') +
      '<div class="toolbar">' +
        C.field('from', 'From', 'datetime-local', s.from, {
          onChange: function (v) { s.from = v; s.page = 1; GW.render(); }
        }) +
        C.field('to', 'To', 'datetime-local', s.to, {
          onChange: function (v) { s.to = v; s.page = 1; GW.render(); }
        }) +
        C.field('size', 'Rows per page', 'select', s.size, {
          options: ['25', '50', '100', '200'],
          onChange: function (v) { s.size = String(v); s.page = 1; GW.render(); }
        }) +
      '</div>' +
      C.table(['Time', 'User', 'Action', 'Entity', 'Entity ID', 'IP'], cells, { empty: 'No data yet.' }) +
      C.pager(list.length, s, function (p) { s.page = Number(p) || 1; GW.render(); });
  };
})();
