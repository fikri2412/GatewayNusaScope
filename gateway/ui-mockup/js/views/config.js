/* AI Gateway prototype — configuration screens (S5–S9) and overlays (O1–O3):
   Providers, Models, Projects, API Keys, Policies.
   Classic script; requires window.GW and GW.C (components.js + app.js) first. */
(function () {
  'use strict';

  var GW = window.GW || {};
  var C = GW.C;
  if (!C) return;

  /* -------------------------------- helpers -------------------------------- */

  function esc(v) { return C.e(v === null || v === undefined ? '' : String(v)); }
  function each(list, fn) { for (var i = 0; i < list.length; i++) fn(list[i], i); }
  function byId(list, id) {
    if (id === null || id === undefined) return null;
    for (var i = 0; i < list.length; i++) { if (list[i].id === Number(id)) return list[i]; }
    return null;
  }
  function dash(v) { return (v === null || v === undefined || v === '') ? '—' : esc(v); }
  function numDash(v) { return (v === null || v === undefined || v === '') ? '—' : C.num(v); }
  function plainNum(v) { return (v === null || v === undefined || v === '') ? '—' : String(v); }
  function trim(v) { return String(v === null || v === undefined ? '' : v).trim(); }
  function optInt(v) { var s = trim(v); if (s === '') return null; var n = parseInt(s, 10); return isNaN(n) ? null : n; }
  function optNum(v) { var s = trim(v); if (s === '') return null; var n = Number(s); return isNaN(n) ? null : n; }
  function readOnlyCell() { return '<span class="muted">read-only</span>'; }
  function providerName(id) { var p = byId(GW.data.providers, id); return p ? p.name : null; }
  function keyLabel(k) { return k.name + ' (' + k.prefix + '…)' }

  function toLocalInput(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    function z(n) { return (n < 10 ? '0' : '') + n; }
    return d.getFullYear() + '-' + z(d.getMonth() + 1) + '-' + z(d.getDate()) + 'T' + z(d.getHours()) + ':' + z(d.getMinutes());
  }
  function rnd10() {
    var s = '';
    while (s.length < 10) s += Math.random().toString(36).slice(2);
    return s.slice(0, 10);
  }
  function parseIps(text) {
    var parts = String(text === null || text === undefined ? '' : text).split(/[\s,]+/).filter(function (x) { return x !== ''; });
    var bad = parts.some(function (address) {
      if (address.indexOf(':') >= 0) {
        try { new URL('https://[' + address + ']/'); return false; } catch { return true; }
      }
      return !/^\d+\.\d+\.\d+\.\d+$/.test(address) || address.split('.').some(function (octet) { return Number(octet) > 255; });
    });
    return { ips: parts, bad: bad };
  }

  function toolbarSelect(name, label, list, current, cb) {
    return C.field(name, label, 'select', current, { options: list, onChange: cb });
  }

  function tableCard(html) {
    return '<section class="card"><div class="card-body">' + html + '</div></section>';
  }

  function menuBtn(label, cb, variant) {
    return C.button(label, function (ev) {
      var t = ev && ev.target;
      var d = t && t.closest ? t.closest('details') : null;
      if (d) d.open = false;
      cb(ev);
    }, variant || 'ghost');
  }
  function rowMenu(buttons) {
    return '<div class="row-actions"><details><summary class="btn btn-ghost">Actions</summary>' +
      '<div class="stack">' + buttons.join('') + '</div></details></div>';
  }

  function wireDialog(dialog, onClick, onChange) {
    if (dialog.__cfgCleanup) dialog.__cfgCleanup();
    dialog.addEventListener('click', onClick);
    if (onChange) dialog.addEventListener('change', onChange);
    dialog.__cfgCleanup = function () {
      dialog.removeEventListener('click', onClick);
      if (onChange) dialog.removeEventListener('change', onChange);
    };
  }
  function clickTarget(ev) {
    var t = ev.target;
    return t && t.closest ? t.closest('[data-act]') : null;
  }

  /* ============================ S5 — Providers ============================ */

  function providerKeyHint(p) {
    var hint = p.keyHint === null || p.keyHint === undefined ? '' : String(p.keyHint);
    if (hint !== '' && hint.charAt(0) !== '…') hint = '…' + hint;
    return hint;
  }

  function renderProviders() {
    var rows = GW.data.providers.map(function (p) {
      var tpl = byId(GW.data.templates, p.templateId);
      return [
        esc(p.name),
        esc(p.type || '—'),
        tpl ? esc(tpl.name) + ' <span class="muted mono">(' + esc(tpl.code) + ')</span>' : '—',
        '<span class="mono">' + dash(p.baseUrl) + '</span>',
        providerKeyHint(p) ? '<span class="mono">' + esc(providerKeyHint(p)) + '</span>' : '—',
        C.badge(p.enabled !== false ? 'active' : 'inactive'),
        providerActions(p)
      ];
    });
    var action = GW.canWrite() ? C.button('Add provider', function () { openProviderDialog(null); }, 'primary') : '';
    return C.page('Providers', 'Upstream providers owned by your tenant (BYOK). Only the last 4 characters of a key are shown.', action) +
      tableCard(C.table(['Name', 'Type', 'Template', 'Base URL', 'Key', 'Status', 'Actions'], rows, { empty: 'No data yet.' }));
  }

  function providerActions(p) {
    if (!GW.canWrite()) return readOnlyCell();
    return rowMenu([
      menuBtn('Edit', function () { openProviderDialog(p); }),
      menuBtn(p.enabled !== false ? 'Disable' : 'Enable', function () {
        p.enabled = (p.enabled === false);
        GW.record('provider.update', 'provider', p.id, { enabled: p.enabled });
        C.toast(p.enabled ? 'Provider enabled.' : 'Provider disabled.');
        GW.render();
      }),
      menuBtn('Rotate key', function () { openRotateDialog(p); }),
      menuBtn('Test discovery', function () { openDiscovery(p, 'discovery'); }),
      menuBtn('Import models', function () { openDiscovery(p, 'catalog'); }),
      menuBtn('Delete', function () {
        C.confirm('Delete provider "' + p.name + '"? Models that route to it will stop working.', function () {
          var i = GW.data.providers.indexOf(p);
          if (i >= 0) GW.data.providers.splice(i, 1);
          GW.record('provider.delete', 'provider', p.id, { name: p.name });
          GW.render();
        });
      }, 'danger')
    ]);
  }

  function openProviderDialog(p) {
    var isEdit = !!p;
    var custom = isEdit ? !p.templateId : false;
    var tplSel = (isEdit && p.templateId) ? p.templateId : '';
    var tpl = byId(GW.data.templates, tplSel);
    var tplOpts = [{ value: '', label: 'Select template…' }].concat(GW.data.templates.map(function (t) {
      return { value: t.id, label: t.name + ' (' + t.code + ')' };
    }));
    var body = '<div class="stack">' +
      '<fieldset class="field"><legend class="field-label">Provider type</legend>' +
        '<div class="inline">' +
          '<label class="check-row"><input type="radio" name="ptype" value="template"' + (custom ? '' : ' checked') + '> From template</label>' +
          '<label class="check-row"><input type="radio" name="ptype" value="custom"' + (custom ? ' checked' : '') + '> Custom</label>' +
        '</div>' +
      '</fieldset>' +
      '<div class="form-grid">' +
        '<div class="span-full" data-sec="template"' + (custom ? ' hidden' : '') + '>' +
          C.field('tpl', 'Template', 'select', tplSel, {
            options: tplOpts,
            hint: 'Platform catalog template; base URL and headers can be overridden.'
          }) +
        '</div>' +
        C.field('name', 'Provider name', 'text', isEdit ? p.name : '', { placeholder: tpl ? tpl.name : '' }) +
        '<div class="span-full" data-sec="template"' + (custom ? ' hidden' : '') + '>' +
          C.field('baseUrl', 'Base URL', 'text', isEdit && !custom ? p.baseUrl : '', {
            placeholder: tpl ? tpl.baseUrl : '',
            hint: 'Default: https://opencode.ai/inference/openai/v1/'
          }) +
          C.field('apiKey', 'API key', 'password', '', {
            hint: 'Stored encrypted; never shown again.',
            required: !isEdit
          }) +
        '</div>' +
        '<div class="span-full" data-sec="custom"' + (custom ? '' : ' hidden') + '>' +
          C.field('cbaseUrl', 'Base URL', 'text', isEdit && custom ? p.baseUrl : '', { placeholder: 'https://api.example.com/v1' }) +
          C.field('capiKey', 'API key', 'password', '', {
            hint: 'Stored encrypted; never shown again.',
            required: !isEdit
          }) +
          C.field('authHeader', 'Auth header', 'text', isEdit && custom ? (p.authHeader || '') : '', { placeholder: 'Authorization', hint: 'Example: Authorization.' }) +
          C.field('authPrefix', 'Auth prefix', 'text', isEdit && custom ? (p.authPrefix || '') : '', { placeholder: 'Bearer', hint: 'Example: Bearer.' }) +
          C.field('modelsPath', 'Models path', 'text', isEdit && custom ? (p.modelsPath || '') : '', { placeholder: '/models', hint: 'Example: /models for discovery.' }) +
        '</div>' +
      '</div>' +
    '</div>';

    C.modal({
      title: isEdit ? 'Edit provider — ' + p.name : 'Add provider',
      wide: true,
      body: body,
      submit: 'Save provider',
      loading: 'Saving…',
      onOpen: function (dialog) {
        var form = dialog.querySelector('form') || dialog;
        function mode() {
          var r = dialog.querySelector('input[name="ptype"]:checked');
          return r ? r.value : 'template';
        }
        function applyMode() {
          var m = mode();
          each(dialog.querySelectorAll('[data-sec]'), function (sec) {
            var on = sec.getAttribute('data-sec') === m;
            sec.hidden = !on;
            each(sec.querySelectorAll('input,select,textarea'), function (el) { el.disabled = !on; });
          });
        }
        function onTplChange() {
          var t = byId(GW.data.templates, dialog.querySelector('[name="tpl"]').value);
          var nameEl = form.elements.name;
          var urlEl = form.elements.baseUrl;
          if (nameEl) nameEl.placeholder = t ? t.name : '';
          if (urlEl) urlEl.placeholder = t ? t.baseUrl : '';
        }
        wireDialog(dialog, function () { }, function (ev) {
          if (!ev.target || !ev.target.name) return;
          if (ev.target.name === 'ptype') applyMode();
          else if (ev.target.name === 'tpl') onTplChange();
        });
        applyMode();
        onTplChange();
      },
      validate: function (form) {
        var m = (form.querySelector('input[name="ptype"]:checked') || {}).value || 'template';
        var key = trim(form.elements[m === 'template' ? 'apiKey' : 'capiKey'].value);
        var tpl = m === 'template' ? byId(GW.data.templates, form.elements.tpl.value) : null;
        var name = trim(form.elements.name.value);
        var url = trim(form.elements[m === 'template' ? 'baseUrl' : 'cbaseUrl'].value);
        if (m === 'template' && !tpl) return 'Select a template.';
        if (!name && !tpl) return 'Provider name is required.';
        var effUrl = url || (tpl ? tpl.baseUrl : '');
        if (!effUrl) return 'Base URL is required.';
        if (!/^https?:\/\/.+/i.test(effUrl)) return 'Base URL must be a valid http(s) URL.';
        if (!isEdit && !key) return 'API key is required.';
        return '';
      },
      onSubmit: function (form) {
        var m = (form.querySelector('input[name="ptype"]:checked') || {}).value || 'template';
        var tpl = m === 'template' ? byId(GW.data.templates, form.elements.tpl.value) : null;
        var key = trim(form.elements[m === 'template' ? 'apiKey' : 'capiKey'].value);
        var name = trim(form.elements.name.value) || (tpl ? tpl.name : '');
        var url = trim(form.elements[m === 'template' ? 'baseUrl' : 'cbaseUrl'].value) || (tpl ? tpl.baseUrl : '');
        var keyHint = key ? '…' + key.slice(-4) : (isEdit ? p.keyHint : null);
        var fields = {
          name: name,
          type: m === 'template' ? (tpl.type || 'openai') : 'custom',
          templateId: tpl ? tpl.id : null,
          baseUrl: url,
          authHeader: m === 'template' ? (tpl.authHeader || '') : (trim(form.elements.authHeader.value) || 'Authorization'),
          authPrefix: m === 'template' ? (tpl.authPrefix || '') : trim(form.elements.authPrefix.value),
          modelsPath: m === 'template' ? (tpl.modelsPath || '') : (trim(form.elements.modelsPath.value) || '/models')
        };
        if (isEdit) {
          p.name = fields.name;
          p.type = fields.type;
          p.templateId = fields.templateId;
          p.baseUrl = fields.baseUrl;
          p.authHeader = fields.authHeader;
          p.authPrefix = fields.authPrefix;
          p.modelsPath = fields.modelsPath;
          if (keyHint) p.keyHint = keyHint;
          GW.record('provider.update', 'provider', p.id, { name: fields.name });
        } else {
          fields.id = GW.nextId(GW.data.providers);
          fields.keyHint = keyHint;
          fields.enabled = true;
          GW.data.providers.push(fields);
          GW.record('provider.create', 'provider', fields.id, { name: fields.name });
        }
        GW.render();
      }
    });
  }

  function openRotateDialog(p) {
    C.modal({
      title: 'Rotate key — ' + p.name,
      body: C.field('apiKey', 'New API key', 'password', '', { required: true, hint: 'Stored encrypted; never shown again.' }),
      submit: 'Save',
      loading: 'Saving…',
      validate: function (form) { return trim(form.elements.apiKey.value) ? '' : 'API key is required.'; },
      onSubmit: function (form) {
        var k = trim(form.elements.apiKey.value);
        p.keyHint = '…' + k.slice(-4);
        GW.record('provider.rotate_key', 'provider', p.id, { name: p.name });
        C.toast('Key rotated.');
        GW.render();
      }
    });
  }

  /* ==================== O2 — Test discovery / Import models ==================== */

  function openDiscovery(p, mode) {
    var tpl = byId(GW.data.templates, p.templateId);
    var candidates = [];
    var note = '';
    var noCatalog = mode === 'catalog' && !tpl;
    if (noCatalog) {
      note = 'Custom providers have no catalog template. Use \u201CTest discovery\u201D to import models from the upstream.';
    } else if (mode === 'catalog') {
      note = 'Only active catalog entries of the openai_chat family can be imported.';
      candidates = GW.data.catalog.filter(function (c) {
        return c.templateId === tpl.id && c.enabled !== false && c.family === 'openai_chat';
      }).map(function (c) { return c.upstream; });
    } else if (tpl) {
      candidates = GW.data.catalog.filter(function (c) { return c.templateId === tpl.id; }).map(function (c) { return c.upstream; });
    } else {
      candidates = [p.name + '-chat', p.name + '-mini'];
    }
    candidates = candidates.filter(function (x, i, a) { return x && a.indexOf(x) === i; });

    var title = (mode === 'catalog' ? 'Import from catalog — ' : 'Discovery — ') + p.name;
    if (noCatalog) {
      C.modal({ title: title, wide: true, body: '<p class="hint">' + esc(note) + '</p>' });
      return;
    }

    function runImport(dialog) {
      var created = 0;
      var skipped = 0;
      each(dialog.querySelectorAll('[data-disc-item]:checked'), function (cb) {
        var upstream = cb.value;
        var exists = GW.data.models.some(function (m) { return m.alias === upstream; });
        if (exists) { skipped++; return; }
        var cat = null;
        if (tpl) {
          for (var i = 0; i < GW.data.catalog.length; i++) {
            var c = GW.data.catalog[i];
            if (c.templateId === tpl.id && c.upstream === upstream) { cat = c; break; }
          }
        }
        var model = {
          id: GW.nextId(GW.data.models),
          alias: upstream,
          description: cat ? (cat.name || '') : '',
          maxOutput: cat ? (cat.maxOutput || 0) : 0,
          enabled: true,
          routes: [{ providerId: p.id, upstream: upstream, priority: 1, weight: 1, enabled: true }],
          prices: (cat && cat.inputPrice !== null && cat.inputPrice !== undefined) ? [{
            input: cat.inputPrice,
            output: cat.outputPrice,
            cacheRead: cat.cacheRead || 0,
            cacheWrite: cat.cacheWrite || 0,
            currency: cat.currency || 'USD',
            tiers: (cat.tiers || []).map(function (t) {
              return { starts: t.starts, input: t.input, output: t.output, cacheRead: t.cacheRead, cacheWrite: t.cacheWrite };
            })
          }] : []
        };
        GW.data.models.push(model);
        created++;
      });
      GW.record('model.import', 'provider', p.id, { created: created, skipped: skipped });
      var area = dialog.querySelector('[data-disc-area]');
      if (area) area.innerHTML = C.alert('success', 'Import complete: ' + created + ' models created, ' + skipped + ' skipped.');
      var submitBtn = dialog.querySelector('.modal-footer button[type="submit"]');
      if (submitBtn) submitBtn.hidden = true;
      return false;
    }

    C.modal({
      title: title,
      wide: true,
      body: (note ? '<p class="hint">' + esc(note) + '</p>' : '') +
        '<div class="stack">' +
          (mode === 'discovery' ? '<div class="muted" data-disc-loading>Contacting provider…</div>' : '') +
          '<div data-disc-area' + (mode === 'discovery' ? ' hidden' : '') + '></div>' +
        '</div>',
      submit: 'Import 0 models',
      loading: 'Importing…',
      cancel: 'Close',
      onClose: function () { GW.render(); },
      onOpen: function (dialog) {
        var area = dialog.querySelector('[data-disc-area]');
        var submitBtn = dialog.querySelector('.modal-footer button[type="submit"]');
        function items() { return dialog.querySelectorAll('[data-disc-item]'); }
        function updateCount() {
          var n = dialog.querySelectorAll('[data-disc-item]:checked').length;
          var count = dialog.querySelector('[data-count]');
          if (count) count.textContent = n + ' of ' + items().length + ' selected';
          if (submitBtn) {
            submitBtn.disabled = n === 0;
            submitBtn.textContent = 'Import ' + n + ' models';
          }
        }
        function paint() {
          var loading = dialog.querySelector('[data-disc-loading]');
          if (loading) loading.hidden = true;
          area.hidden = false;
          if (!candidates.length) {
            area.innerHTML = '<p class="muted">The provider returned no models.</p>';
            if (submitBtn) submitBtn.hidden = true;
            return;
          }
          area.innerHTML =
            '<div class="toolbar">' +
              '<button type="button" class="btn btn-ghost" data-act="sel-all">Select all</button>' +
              '<button type="button" class="btn btn-ghost" data-act="clear">Clear</button>' +
              '<span class="muted" data-count></span>' +
            '</div>' +
            '<div class="stack">' + candidates.map(function (u) {
              return '<label class="check-row"><input type="checkbox" data-disc-item value="' + esc(u) + '"> <span class="mono">' + esc(u) + '</span></label>';
            }).join('') + '</div>';
          updateCount();
        }
        wireDialog(dialog, function (ev) {
          var btn = clickTarget(ev);
          if (!btn) return;
          var act = btn.getAttribute('data-act');
          if (act === 'sel-all') { each(items(), function (cb) { cb.checked = true; }); updateCount(); }
          else if (act === 'clear') { each(items(), function (cb) { cb.checked = false; }); updateCount(); }
        }, function (ev) {
          if (ev.target && ev.target.matches && ev.target.matches('[data-disc-item]')) updateCount();
        });
        if (mode === 'discovery') setTimeout(paint, 600);
        else paint();
      },
      validate: function (form) {
        return form.querySelectorAll('[data-disc-item]:checked').length ? '' : 'Select at least one model.';
      },
      onSubmit: function (form, dialog) { return runImport(dialog); }
    });
  }

  /* ============================== S6 — Models ============================== */

  function routesCell(m) {
    if (!m.routes || !m.routes.length) return '<span class="muted">no routes</span>';
    return '<div class="stack">' + m.routes.map(function (r) {
      return '<div class="muted">' + dash(providerName(r.providerId)) + ' → <span class="mono">' + dash(r.upstream) + '</span> ' +
        '(p' + plainNum(r.priority) + '/w' + plainNum(r.weight) + (r.enabled === false ? ', disabled' : '') + ')</div>';
    }).join('') + '</div>';
  }

  function priceCell(m) {
    if (!m.prices || !m.prices.length) return '—';
    return '<div class="stack">' + m.prices.map(function (pr) {
      var t = (pr.tiers || []).length;
      return '<div>' + esc(plainNum(pr.input)) + ' / ' + esc(plainNum(pr.output)) +
        (t ? ' (+' + t + ' tier' + (t === 1 ? '' : 's') + ')' : '') + '</div>';
    }).join('') + '</div>';
  }

  function renderModels() {
    var rows = GW.data.models.map(function (m) {
      return [
        '<span class="mono">' + esc(m.alias) + '</span>',
        m.description ? esc(m.description) : '—',
        routesCell(m),
        m.maxOutput ? C.num(m.maxOutput) : '—',
        priceCell(m),
        C.badge(m.enabled !== false ? 'active' : 'inactive'),
        modelActions(m)
      ];
    });
    var action = GW.canWrite() ? C.button('Add model', function () { openModelDialog(null); }, 'primary') : '';
    return C.page('Models', 'Model aliases clients see, routes to providers, and prices.', action) +
      tableCard(C.table(['Alias', 'Description', 'Routes', 'Max output', 'Price', 'Status', 'Actions'], rows, { empty: 'No data yet.' }));
  }

  function modelActions(m) {
    if (!GW.canWrite()) return readOnlyCell();
    return rowMenu([
      menuBtn('Edit', function () { openModelDialog(m); }),
      menuBtn('Routes', function () { openRoutesDialog(m); }),
      menuBtn('Add price', function () { openAddPriceDialog(m); }),
      menuBtn(m.enabled !== false ? 'Disable' : 'Enable', function () {
        m.enabled = (m.enabled === false);
        GW.record('model.update', 'model', m.id, { enabled: m.enabled });
        GW.render();
      }),
      menuBtn('Delete', function () {
        C.confirm('Delete model "' + m.alias + '"?', function () {
          var i = GW.data.models.indexOf(m);
          if (i >= 0) GW.data.models.splice(i, 1);
          GW.record('model.delete', 'model', m.id, { alias: m.alias });
          GW.render();
        });
      }, 'danger')
    ]);
  }

  /* --- repeatable rows (routes + price tiers) --- */

  function routeRowHtml(r, withDisabled) {
    r = r || {};
    var provs = GW.data.providers.map(function (p) {
      return '<option value="' + esc(p.id) + '"' + (String(p.id) === String(r.providerId) ? ' selected' : '') + '>' +
        esc(p.name) + (p.enabled === false ? ' (inactive)' : '') + '</option>';
    }).join('');
    return '<div class="route-row" data-route-row>' +
      '<select data-r="provider" aria-label="Provider"><option value="">Select provider…</option>' + provs + '</select>' +
      '<input data-r="upstream" aria-label="Upstream model" placeholder="Upstream model" value="' + esc(r.upstream || '') + '">' +
      '<input type="number" data-r="priority" aria-label="Priority" min="1" value="' + (r.priority || 1) + '">' +
      '<input type="number" data-r="weight" aria-label="Weight" min="1" value="' + (r.weight || 1) + '">' +
      (withDisabled ? '<label class="check-row"><input type="checkbox" data-r="disabled"' + (r.enabled === false ? ' checked' : '') + '> disabled</label>' : '') +
      '<button type="button" class="btn btn-ghost" data-act="remove-route">Remove</button>' +
    '</div>';
  }

  function tierRowHtml(t) {
    t = t || {};
    function inp(key, label) {
      var v = (t[key] === null || t[key] === undefined) ? '' : t[key];
      return '<input type="number" data-t="' + key + '" aria-label="' + label + '" placeholder="' + label + '" min="0" step="any" value="' + esc(v) + '">';
    }
    return '<div class="tier-row" data-tier-row>' +
      inp('starts', 'Starts at input tokens') +
      inp('input', 'Input / 1M') +
      inp('output', 'Output / 1M') +
      inp('cacheRead', 'Cache read') +
      inp('cacheWrite', 'Cache write') +
      '<button type="button" class="btn btn-ghost" data-act="remove-tier">Remove</button>' +
    '</div>';
  }

  function collectRoutes(scope) {
    var out = { valid: [], incomplete: 0 };
    each(scope.querySelectorAll('[data-route-row]'), function (row) {
      var providerId = row.querySelector('[data-r="provider"]').value;
      var upstream = trim(row.querySelector('[data-r="upstream"]').value);
      if (!providerId && !upstream) return;
      if (!providerId || !upstream) { out.incomplete++; return; }
      var priority = parseInt(row.querySelector('[data-r="priority"]').value, 10);
      var weight = parseInt(row.querySelector('[data-r="weight"]').value, 10);
      var dis = row.querySelector('[data-r="disabled"]');
      out.valid.push({
        providerId: Number(providerId),
        upstream: upstream,
        priority: (isNaN(priority) || priority < 1) ? 1 : priority,
        weight: (isNaN(weight) || weight < 1) ? 1 : weight,
        enabled: !(dis && dis.checked)
      });
    });
    return out;
  }

  function collectTiers(scope) {
    var tiers = [];
    var bad = 0;
    each(scope.querySelectorAll('[data-tier-row]'), function (row) {
      var vals = {};
      var any = false;
      ['starts', 'input', 'output', 'cacheRead', 'cacheWrite'].forEach(function (k) {
        var v = trim(row.querySelector('[data-t="' + k + '"]').value);
        if (v !== '') any = true;
        vals[k] = v;
      });
      if (!any) return;
      if (vals.starts === '') { bad++; return; }
      tiers.push({
        starts: Number(vals.starts),
        input: Number(vals.input || 0),
        output: Number(vals.output || 0),
        cacheRead: Number(vals.cacheRead || 0),
        cacheWrite: Number(vals.cacheWrite || 0)
      });
    });
    return { tiers: tiers, bad: bad };
  }

  function readPrice(form) {
    return {
      input: Number(form.elements.inputPrice.value),
      output: Number(form.elements.outputPrice.value),
      cacheRead: Number(form.elements.cacheRead.value || 0),
      cacheWrite: Number(form.elements.cacheWrite.value || 0),
      currency: form.elements.currency.value,
      tiers: collectTiers(form).tiers
    };
  }

  function routeFieldset() {
    return '<fieldset class="field span-full"><legend class="field-label">Routes</legend>' +
      '<p class="hint">Lower priority numbers are tried first; weight sets the load split.</p>' +
      '<div class="stack" data-routes></div>' +
      '<div class="row-actions"><button type="button" class="btn btn-ghost" data-act="add-route">Add route</button></div>' +
    '</fieldset>';
  }

  function priceBlock() {
    return '<div class="form-grid">' +
      C.field('inputPrice', 'Input / 1M', 'number', '', { min: 0, step: 'any' }) +
      C.field('outputPrice', 'Output / 1M', 'number', '', { min: 0, step: 'any' }) +
      C.field('cacheRead', 'Cache read / 1M', 'number', '', { min: 0, step: 'any' }) +
      C.field('cacheWrite', 'Cache write / 1M', 'number', '', { min: 0, step: 'any' }) +
      C.field('currency', 'Currency', 'select', 'USD', { options: ['USD'] }) +
      '</div>' +
      '<fieldset class="field span-full"><legend class="field-label">Price tiers</legend>' +
      '<p class="hint">Price per 1 million tokens for long prompts; the base tier is the main price above.</p>' +
      '<div class="stack" data-tiers></div>' +
      '<div class="row-actions"><button type="button" class="btn btn-ghost" data-act="add-tier">Add tier</button></div>' +
      '</fieldset>';
  }

  function wireRepeatable(dialog) {
    var routesBox = dialog.querySelector('[data-routes]');
    var tiersBox = dialog.querySelector('[data-tiers]');
    var priceSec = dialog.querySelector('[data-sec="price"]');
    var priceToggle = dialog.querySelector('[name="includePrice"]');
    function syncPrice() {
      if (!priceSec || !priceToggle) return;
      priceSec.hidden = !priceToggle.checked;
      each(priceSec.querySelectorAll('input,select,textarea'), function (el) { el.disabled = !priceToggle.checked; });
    }
    wireDialog(dialog, function (ev) {
      var btn = clickTarget(ev);
      if (!btn) return;
      var act = btn.getAttribute('data-act');
      if (act === 'add-route' && routesBox) routesBox.insertAdjacentHTML('beforeend', routeRowHtml(null, !!dialog.getAttribute('data-routes-disabled')));
      else if (act === 'remove-route') { var rr = btn.closest('[data-route-row]'); if (rr) rr.parentNode.removeChild(rr); }
      else if (act === 'add-tier' && tiersBox) tiersBox.insertAdjacentHTML('beforeend', tierRowHtml(null));
      else if (act === 'remove-tier') { var tr = btn.closest('[data-tier-row]'); if (tr) tr.parentNode.removeChild(tr); }
    }, function (ev) {
      if (ev.target && ev.target.name === 'includePrice') syncPrice();
    });
    syncPrice();
  }

  /* --- O3 — Add / Edit model --- */

  function openModelDialog(m) {
    var isEdit = !!m;
    var body = '<div class="stack">' +
      '<div class="form-grid">' +
        C.field('alias', 'Alias', 'text', isEdit ? m.alias : '', { hint: 'The model name clients use, e.g. gpt-4o-mini.' }) +
        C.field('description', 'Description', 'text', isEdit ? (m.description || '') : '') +
        C.field('maxOutput', 'Max output tokens', 'number', isEdit ? (m.maxOutput || '') : '', { min: 0 }) +
      '</div>' +
      (isEdit ? '' : routeFieldset()) +
      C.field('includePrice', 'Include initial price', 'checkbox', !isEdit) +
      '<div class="stack" data-sec="price"' + (isEdit ? ' hidden' : '') + '>' + priceBlock() + '</div>' +
    '</div>';

    C.modal({
      title: isEdit ? 'Edit model — ' + m.alias : 'Add model',
      wide: true,
      body: body,
      submit: 'Save model',
      loading: 'Saving…',
      onOpen: function (dialog) {
        var routesBox = dialog.querySelector('[data-routes]');
        if (routesBox) routesBox.insertAdjacentHTML('beforeend', routeRowHtml(null, false));
        wireRepeatable(dialog);
      },
      validate: function (form) {
        var alias = trim(form.elements.alias.value);
        if (!alias) return 'Alias is required.';
        var clash = GW.data.models.some(function (x) { return x.alias === alias && (!isEdit || x.id !== m.id); });
        if (clash) return 'A model with this alias already exists.';
        if (!isEdit) {
          var col = collectRoutes(form);
          if (col.incomplete || !col.valid.length) return 'At least one route with a provider and an upstream model.';
        }
        if (form.elements.includePrice.checked) {
          if (trim(form.elements.inputPrice.value) === '' || trim(form.elements.outputPrice.value) === '') return 'Input and output prices are required.';
          if (collectTiers(form).bad) return 'Each price tier needs a “Starts at” value.';
        }
        return '';
      },
      onSubmit: function (form) {
        var alias = trim(form.elements.alias.value);
        var description = trim(form.elements.description.value);
        var maxOutput = form.elements.maxOutput.value === '' ? 0 : Number(form.elements.maxOutput.value);
        var includePrice = form.elements.includePrice.checked;
        if (isEdit) {
          m.alias = alias;
          m.description = description;
          m.maxOutput = maxOutput;
          if (includePrice) {
            m.prices = m.prices || [];
            m.prices.push(readPrice(form));
          }
          GW.record('model.update', 'model', m.id, { alias: alias });
        } else {
          var col = collectRoutes(form);
          var model = {
            id: GW.nextId(GW.data.models),
            alias: alias,
            description: description,
            maxOutput: maxOutput,
            enabled: true,
            routes: col.valid,
            prices: includePrice ? [readPrice(form)] : []
          };
          GW.data.models.push(model);
          GW.record('model.create', 'model', model.id, { alias: alias });
        }
        GW.render();
      }
    });
  }

  function openRoutesDialog(m) {
    C.modal({
      title: 'Routes — ' + m.alias,
      wide: true,
      body: routeFieldset(),
      submit: 'Save routes',
      loading: 'Saving…',
      onOpen: function (dialog) {
        dialog.setAttribute('data-routes-disabled', '1');
        var box = dialog.querySelector('[data-routes]');
        var list = (m.routes && m.routes.length) ? m.routes : [null];
        each(list, function (r) { box.insertAdjacentHTML('beforeend', routeRowHtml(r, true)); });
        wireRepeatable(dialog);
      },
      validate: function (form) {
        return collectRoutes(form).incomplete ? 'At least one route with a provider and an upstream model.' : '';
      },
      onSubmit: function (form) {
        m.routes = collectRoutes(form).valid;
        GW.record('model.routes', 'model', m.id, { routes: m.routes.length });
        GW.render();
      }
    });
  }

  function openAddPriceDialog(m) {
    C.modal({
      title: 'Add price — ' + m.alias,
      wide: true,
      body: priceBlock(),
      submit: 'Save price',
      loading: 'Saving…',
      onOpen: function (dialog) { wireRepeatable(dialog); },
      validate: function (form) {
        if (trim(form.elements.inputPrice.value) === '' || trim(form.elements.outputPrice.value) === '') return 'Input and output prices are required.';
        if (collectTiers(form).bad) return 'Each price tier needs a “Starts at” value.';
        return '';
      },
      onSubmit: function (form) {
        m.prices = m.prices || [];
        m.prices.push(readPrice(form));
        GW.record('model.price_add', 'model', m.id, { alias: m.alias });
        GW.render();
      }
    });
  }

  /* ============================= S7 — Projects ============================= */

  function renderProjects() {
    var rows = GW.data.projects.map(function (pr) {
      return [
        esc(pr.name),
        C.badge(pr.status),
        pr.logContent ? 'yes' : 'no',
        (pr.retention === null || pr.retention === undefined) ? '—' : esc(pr.retention) + ' days',
        C.date(pr.created),
        projectActions(pr)
      ];
    });
    var action = GW.canWrite() ? C.button('Add project', GW.createProject, 'primary') : '';
    return C.page('Projects', 'Projects separate keys, policies, and usage per application.', action) +
      tableCard(C.table(['Name', 'Status', 'Content logging', 'Content retention', 'Created', 'Actions'], rows, { empty: 'No data yet.' }));
  }

  function projectActions(pr) {
    if (!GW.canWrite()) return readOnlyCell();
    return rowMenu([
      menuBtn('Edit', function () { openProjectDialog(pr); }),
      menuBtn('Delete', function () {
        C.confirm('Delete project "' + pr.name + '"? Its API keys will stop working.', function () {
          var i = GW.data.projects.indexOf(pr);
          if (i >= 0) GW.data.projects.splice(i, 1);
          GW.record('project.delete', 'project', pr.id, { name: pr.name });
          GW.render();
        });
      }, 'danger')
    ]);
  }

  function openProjectDialog(pr) {
    var isEdit = !!pr;
    var body = '<div class="form-grid">' +
      C.field('name', 'Name', 'text', isEdit ? pr.name : '', { required: true }) +
      C.field('status', 'Status', 'select', isEdit ? pr.status : 'active', {
        options: [{ value: 'active', label: 'active' }, { value: 'suspended', label: 'suspended' }],
        hint: 'Suspending a project rejects new requests on it.'
      }) +
      C.field('logContent', 'Store conversation content logs', 'checkbox', isEdit ? !!pr.logContent : false) +
      C.field('retention', 'Content retention (days)', 'number', (isEdit && pr.retention !== null && pr.retention !== undefined) ? pr.retention : '', {
        min: 0,
        hint: 'Leave empty for no retention; enter 0 or more.'
      }) +
      (isEdit ? C.field('clearRetention', 'Clear content retention', 'checkbox', false) : '') +
    '</div>';

    C.modal({
      title: isEdit ? 'Edit project — ' + pr.name : 'Add project',
      body: body,
      submit: 'Save',
      loading: 'Saving…',
      onOpen: function (dialog) {
        wireDialog(dialog, function () { }, function (ev) {
          if (ev.target && ev.target.name === 'clearRetention') {
            var r = dialog.querySelector('[name="retention"]');
            if (r) r.disabled = ev.target.checked;
          }
        });
      },
      validate: function (form) { return trim(form.elements.name.value) ? '' : 'Name is required.'; },
      onSubmit: function (form) {
        var clear = isEdit ? form.elements.clearRetention.checked : false;
        var retRaw = trim(form.elements.retention.value);
        var retention = (clear || retRaw === '') ? null : Number(retRaw);
        if (isEdit) {
          pr.name = trim(form.elements.name.value);
          pr.status = form.elements.status.value;
          pr.logContent = form.elements.logContent.checked;
          pr.retention = retention;
          GW.record('project.update', 'project', pr.id, { name: pr.name });
        } else {
          var created = {
            id: GW.nextId(GW.data.projects),
            name: trim(form.elements.name.value),
            status: form.elements.status.value,
            logContent: form.elements.logContent.checked,
            retention: retention,
            created: GW.now
          };
          GW.data.projects.push(created);
          GW.record('project.create', 'project', created.id, { name: created.name });
        }
        GW.render();
      }
    });
  }

  /* ============================= S8 — API Keys ============================= */

  function renderKeys() {
    var st = GW.viewState('/keys', { project: 'all' });
    var filterOpts = [{ value: 'all', label: 'All projects' }].concat(GW.data.projects.map(function (p) {
      return { value: p.id, label: p.name };
    }));
    var toolbar = '<div class="toolbar">' +
      toolbarSelect('keyProject', 'Filter by project', filterOpts, st.project, function (value) {
        st.project = value;
        GW.render();
      }) +
    '</div>';
    var rows = GW.data.keys.filter(function (k) {
      return st.project === 'all' || String(k.projectId) === String(st.project);
    }).map(function (k) {
      var p = byId(GW.data.projects, k.projectId);
      return [
        esc(k.name),
        dash(p ? p.name : null),
        k.prefix ? '<span class="mono">' + esc(k.prefix) + '…</span>' : '—',
        (k.ips && k.ips.length) ? esc(k.ips.join(', ')) : 'all',
        k.expires ? C.date(k.expires) : '—',
        k.lastUsed ? C.date(k.lastUsed) : '—',
        k.revoked
          ? C.badge('revoked ' + String(k.revoked).slice(0, 10))
          : C.badge('active'),
        keyActions(k)
      ];
    });
    var action = GW.canWrite() ? C.button('Issue key', function () { openKeyDialog(null); }, 'primary') : '';
    return C.page('API Keys', 'Keys are grouped by project; the full value is shown only once when created.', action) +
      toolbar +
      tableCard(C.table(['Name', 'Project', 'Prefix', 'Allowed IPs', 'Expires', 'Last used', 'Status', 'Actions'], rows, { empty: 'No data yet.' }));
  }

  function keyActions(k) {
    if (!GW.canWrite()) return readOnlyCell();
    if (k.revoked) return '<span class="muted">revoked</span>';
    return rowMenu([
      menuBtn('Edit', function () { openKeyDialog(k); }),
      menuBtn('Revoke', function () {
        C.confirm('Revoke key "' + k.name + '"? Clients using it will be rejected immediately.', function () {
          k.revoked = GW.now;
          GW.record('key.revoke', 'key', k.id, { name: k.name });
          GW.render();
        }, 'Revoke');
      }, 'danger')
    ]);
  }

  function openKeyDialog(k) {
    var isEdit = !!k;
    var body = '<div class="form-grid">';
    if (!isEdit) {
      body += C.field('project', 'Project', 'select', '', {
        options: [{ value: '', label: 'Select project…' }].concat(GW.data.projects.map(function (p) {
          return { value: p.id, label: p.name };
        })),
        required: true
      });
    }
    body += C.field('name', 'Key name', 'text', isEdit ? k.name : '', { required: true }) +
      C.field('expires', 'Expires', 'datetime-local', (isEdit && k.expires) ? toLocalInput(k.expires) : '', {
        hint: isEdit ? ('Currently: ' + (k.expires ? C.date(k.expires) : '—')) : 'Leave empty for no expiry.'
      }) +
      (isEdit ? C.field('noExpiry', 'Remove expiry', 'checkbox', false) : '') +
      C.field('ips', 'Allowed IPs', 'textarea', (isEdit && k.ips && k.ips.length) ? k.ips.join('\n') : '', {
        rows: 4,
        hint: 'One IP per line (or comma-separated). Empty = all IPs.'
      }) +
    '</div>';

    C.modal({
      title: isEdit ? 'Edit key — ' + k.name : 'Issue API key',
      body: body,
      submit: isEdit ? 'Save changes' : 'Issue key',
      loading: 'Saving…',
      onOpen: function (dialog) {
        wireDialog(dialog, function () { }, function (ev) {
          if (ev.target && ev.target.name === 'noExpiry') {
            var exp = dialog.querySelector('[name="expires"]');
            if (exp) exp.disabled = ev.target.checked;
          }
        });
      },
      validate: function (form) {
        if (!trim(form.elements.name.value)) return 'Key name is required.';
        if (!isEdit && !form.elements.project.value) return 'Select a project.';
        if (parseIps(form.elements.ips.value).bad) return 'Allowed IPs must be valid IPv4 or IPv6 addresses, one per line or comma-separated.';
        return '';
      },
      onSubmit: function (form) {
        var ips = parseIps(form.elements.ips.value).ips;
        var noExp = isEdit && form.elements.noExpiry.checked;
        var expRaw = form.elements.expires.value;
        var expires = (noExp || expRaw === '') ? null : new Date(expRaw).toISOString();
        var name = trim(form.elements.name.value);
        if (isEdit) {
          k.name = name;
          k.ips = ips;
          k.expires = expires;
          GW.record('key.update', 'key', k.id, { name: name });
          GW.render();
        } else {
          var prefix = 'gw_' + rnd10();
          var full = GW.secret(prefix);
          var created = {
            id: GW.nextId(GW.data.keys),
            name: name,
            projectId: Number(form.elements.project.value),
            prefix: prefix,
            ips: ips,
            expires: expires,
            lastUsed: null,
            revoked: null
          };
          GW.data.keys.push(created);
          GW.record('key.create', 'key', created.id, { name: name });
          GW.render();
          C.secret('New API key — copy it now; this value is shown only once.', full);
        }
      }
    });
  }

  /* ============================= S9 — Policies ============================= */

  function policyScopeText(pol) {
    if (pol.scope === 'tenant') return 'tenant';
    if (pol.scope === 'project') {
      var p = byId(GW.data.projects, pol.scopeId);
      return 'project: ' + (p ? esc(p.name) : '—');
    }
    var k = byId(GW.data.keys, pol.scopeId);
    return 'key: ' + (k ? esc(k.name) + ' (' + esc(k.prefix) + '…)' : '—');
  }

  function policyFields(pol, isNew, ro) {
    var d = pol || {};
    function nv(v) { return (v === null || v === undefined) ? '' : v; }
    return C.field('maxTokens', 'Max tokens per request', 'number', nv(d.maxTokens), { min: 0, hint: 'Empty = no limit.', disabled: ro }) +
      C.field('rpm', 'Requests per minute', 'number', nv(d.rpm), { min: 0, disabled: ro }) +
      C.field('daily', 'Daily token quota', 'number', nv(d.daily), { min: 0, disabled: ro }) +
      C.field('monthly', 'Monthly token quota', 'number', nv(d.monthly), { min: 0, disabled: ro }) +
      C.field('budget', 'Monthly budget', 'number', nv(d.budget), { min: 0, step: 'any', disabled: ro }) +
      C.field('allowed', 'Allowed models', 'textarea', (d.allowed && d.allowed.length) ? d.allowed.join('\n') : '', {
        rows: 4,
        hint: 'One alias per line. Empty = all models.',
        disabled: ro
      }) +
      C.field('enabled', 'Policy enabled', 'checkbox', isNew ? true : (d.enabled !== false), { disabled: ro });
  }

  function savePolicy(scope, scopeId, existing, form) {
    var data = {
      maxTokens: optInt(form.elements.maxTokens.value),
      rpm: optInt(form.elements.rpm.value),
      daily: optInt(form.elements.daily.value),
      monthly: optInt(form.elements.monthly.value),
      budget: optNum(form.elements.budget.value),
      allowed: String(form.elements.allowed.value || '').split(/[\n,]+/).map(function (x) { return trim(x); }).filter(function (x) { return x !== ''; }),
      enabled: form.elements.enabled.checked
    };
    if (existing) {
      existing.maxTokens = data.maxTokens;
      existing.rpm = data.rpm;
      existing.daily = data.daily;
      existing.monthly = data.monthly;
      existing.budget = data.budget;
      existing.allowed = data.allowed;
      existing.enabled = data.enabled;
      GW.record('policy.update', 'policy', existing.id, { scope: scope, scopeId: scopeId });
    } else {
      var created = {
        id: GW.nextId(GW.data.policies),
        scope: scope,
        scopeId: scopeId,
        maxTokens: data.maxTokens,
        rpm: data.rpm,
        daily: data.daily,
        monthly: data.monthly,
        budget: data.budget,
        allowed: data.allowed,
        enabled: data.enabled
      };
      GW.data.policies.push(created);
      GW.record('policy.create', 'policy', created.id, { scope: scope, scopeId: scopeId });
    }
    GW.viewState('/policies', {}).flash = 'Policy saved.';
    GW.render();
  }

  function renderPolicies() {
    var st = GW.viewState('/policies', { scope: 'tenant', project: '', key: '' });
    var canWrite = GW.canWrite();
    var ro = !canWrite;
    var flash = st.flash;
    st.flash = null;

    var toolbar = '<div class="toolbar">' +
      toolbarSelect('policyScope', 'Scope', [
        { value: 'tenant', label: 'Tenant' },
        { value: 'project', label: 'Project' },
        { value: 'key', label: 'API key' }
      ], st.scope, function (value) {
        st.scope = value;
        GW.render();
      });
    if (st.scope === 'project') {
      toolbar += toolbarSelect('policyProject', 'Project', [{ value: '', label: 'Select project…' }].concat(GW.data.projects.map(function (p) {
        return { value: p.id, label: p.name };
      })), st.project, function (value) {
        st.project = value;
        GW.render();
      });
    } else if (st.scope === 'key') {
      toolbar += toolbarSelect('policyKey', 'API key', [{ value: '', label: 'Select key…' }].concat(GW.data.keys.map(function (k) {
        return { value: k.id, label: keyLabel(k) };
      })), st.key, function (value) {
        st.key = value;
        GW.render();
      });
    }
    toolbar += '</div>';

    var scopeId = null;
    if (st.scope === 'tenant') scopeId = GW.state.tenantId || 1;
    else if (st.scope === 'project') scopeId = st.project === '' ? null : Number(st.project);
    else scopeId = st.key === '' ? null : Number(st.key);

    var scopeLabel = st.scope === 'tenant' ? 'Tenant' : (st.scope === 'project' ? 'Project' : 'API key');
    var policy = null;
    if (scopeId !== null && !isNaN(scopeId)) {
      for (var i = 0; i < GW.data.policies.length; i++) {
        var pol = GW.data.policies[i];
        if (pol.scope === st.scope && pol.scopeId === scopeId) { policy = pol; break; }
      }
    }

    var editorBody;
    if (scopeId === null || isNaN(scopeId)) {
      editorBody = '<p class="muted">Select a ' + (st.scope === 'project' ? 'project' : 'key') + ' to see its policy.</p>';
    } else if (ro) {
      editorBody = '<p class="hint">Only owners/admins can change policies.</p>' +
        '<div class="form-grid">' + policyFields(policy, !policy, true) + '</div>';
    } else {
      editorBody = (policy ? '' : C.alert('info', 'No policy for this scope yet — fill in the form to create one.')) +
        C.form(policyFields(policy, !policy, false), 'Save policy', function (form) {
          savePolicy(st.scope, scopeId, policy, form);
        });
    }
    var editorCard = '<section class="card"><div class="card-header"><h2>' + esc(scopeLabel + ' policy') +
      (policy ? ' <span class="mono">' + policy.id + '</span>' : '') + '</h2></div>' +
      '<div class="card-body">' + editorBody + '</div></section>';

    var allRows = GW.data.policies.map(function (pol2) {
      var actions = canWrite
        ? C.button('Delete', function () {
            C.confirm('Delete this policy?', function () {
              var idx = GW.data.policies.indexOf(pol2);
              if (idx >= 0) GW.data.policies.splice(idx, 1);
              GW.record('policy.delete', 'policy', pol2.id, { scope: pol2.scope, scopeId: pol2.scopeId });
              GW.render();
            });
          }, 'danger')
        : readOnlyCell();
      return [
        policyScopeText(pol2),
        '<span class="mono">' + pol2.id + '</span>',
        numDash(pol2.maxTokens),
        numDash(pol2.rpm),
        numDash(pol2.daily),
        numDash(pol2.monthly),
        numDash(pol2.budget),
        (pol2.allowed && pol2.allowed.length) ? esc(pol2.allowed.join(', ')) : 'all',
        C.badge(pol2.enabled !== false ? 'active' : 'inactive'),
        actions
      ];
    });

    return C.page('Policies', 'Quota limits and allowed models per tenant, project, or API key.') +
      toolbar +
      (flash ? C.alert('success', flash) : '') +
      editorCard +
      C.card('All policies', C.table(
        ['Scope', 'ID', 'Max tokens/req', 'Req/min', 'Daily quota', 'Monthly quota', 'Monthly budget', 'Allowed models', 'Status', ''],
        allRows,
        { empty: 'No policies yet.' }
      ));
  }

  GW.createProject = function () {
    if (!['owner','admin'].includes(GW.state.role)) return;
    openProjectDialog(null);
  };

  /* ------------------------------ routes ------------------------------ */

  GW.views['/providers'] = renderProviders;
  GW.views['/models'] = renderModels;
  GW.views['/projects'] = renderProjects;
  GW.views['/keys'] = renderKeys;
  GW.views['/policies'] = renderPolicies;
})();
