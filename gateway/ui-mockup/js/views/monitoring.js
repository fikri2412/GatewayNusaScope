/* AI Gateway — monitoring screens (S10 Usage, S11 Alerts, S12 Webhooks, S13 Users, S14 Audit).
   Classic-script IIFE; registers renderers on GW.views. Data is mutated in place (GW.data). */
(function () {
  'use strict';

  var GW = window.GW;
  var C = GW.C;
  GW.views = GW.views || {};

  /* ------------------------------------------------------------- utilities */

  function find(list, id) {
    for (var i = 0; i < list.length; i++) {
      if (list[i].id === id) return list[i];
    }
    return null;
  }

  function nameOf(list, id) {
    var item = find(list, id);
    return item ? item.name : '—';
  }

  function aliasOf(list, id) {
    var item = find(list, id);
    return item ? item.alias : '—';
  }

  function dt(value) {
    return value ? C.date(value) : '—';
  }

  function optsHTML(items, selected) {
    return items.map(function (item) {
      var value = typeof item === 'object' ? item.value : item;
      var label = typeof item === 'object' ? item.label : item;
      return '<option value="' + C.e(String(value)) + '"' +
        (String(value) === String(selected) ? ' selected' : '') + '>' +
        C.e(String(label)) + '</option>';
    }).join('');
  }

  function pretty(value) {
    if (value === null || value === undefined || value === '') return '—';
    if (typeof value === 'string') {
      try { return JSON.stringify(JSON.parse(value), null, 2); } catch (err) { return value; }
    }
    try { return JSON.stringify(value, null, 2); } catch (err) { return String(value); }
  }

  function parseContent(raw) {
    if (!raw) return null;
    if (typeof raw === 'string') {
      try { return JSON.parse(raw); } catch (err) { return { request: raw }; }
    }
    return raw;
  }

  function csvCell(value) {
    var text = value === null || value === undefined ? '' : String(value);
    return /[",\r\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
  }

  function downloadCsv(filename, lines) {
    var blob = new Blob([lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }

  /* datetime-local values ('YYYY-MM-DDTHH:mm') compared against ISO timestamps. */
  function inTimes(iso, from, to) {
    var at = String(iso).slice(0, 16);
    return (!from || at >= from) && (!to || at <= to);
  }

  function byTimeDesc(a, b) { return Date.parse(b.time) - Date.parse(a.time); }
  function byCreatedDesc(a, b) { return Date.parse(b.created) - Date.parse(a.created); }

  /* One-time secret panel is imperative: rerender the mutated data first, then display it. */
  function showSecret(title, value) {
    GW.render();
    C.secret(title, value);
  }

  function inlineUpdate(event, apply) {
    var control = event.target;
    if (!control || control.disabled) return;
    control.disabled = true;
    var row = control.closest ? control.closest('tr') : null;
    if (row) {
      row.setAttribute('aria-busy', 'true');
      var note = document.createElement('span');
      note.className = 'muted';
      note.textContent = 'Saving…';
      (control.parentNode || row).appendChild(note);
    }
    setTimeout(function () {
      apply();
      GW.render();
    }, 400);
  }

  /* ------------------------------------------ webhook URL boundary checks */

  function isPrivateHost(host) {
    var h = String(host).toLowerCase();
    if (h.charAt(0) === '[' && h.charAt(h.length - 1) === ']') h = h.slice(1, -1);
    if (h === 'localhost') return true;
    var v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(h);
    if (v4) {
      var a = Number(v4[1]);
      var b = Number(v4[2]);
      return a === 0 || a === 10 || a === 127 ||
        (a === 172 && b >= 16 && b <= 31) ||
        (a === 192 && b === 168) ||
        (a === 169 && b === 254);
    }
    if (h.indexOf(':') >= 0) {
      if (h === '::1' || h === '::') return true;
      if (/^f[cd][0-9a-f]{0,2}:/.test(h)) return true;      /* fc00::/7 unique local */
      if (/^fe[89ab][0-9a-f]{0,2}:/.test(h)) return true;   /* fe80::/10 link local */
      var mappedDotted = /^::ffff:(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})$/.exec(h);
      if (mappedDotted) return isPrivateHost(mappedDotted[1]);
      var mappedHex = /^::ffff:([0-9a-f]{1,4}):([0-9a-f]{1,4})$/.exec(h);
      if (mappedHex) {
        var hi = parseInt(mappedHex[1], 16);
        var lo = parseInt(mappedHex[2], 16);
        return isPrivateHost([hi >> 8, hi & 255, lo >> 8, lo & 255].join('.'));
      }
      return false;
    }
    return false;
  }

  function validateWebhookUrl(raw) {
    var value = String(raw || '').trim();
    if (!value) return 'Enter a URL.';
    var url;
    try { url = new URL(value); } catch (err) { return 'Enter a valid URL.'; }
    if (url.protocol !== 'https:') return 'URL must use https.';
    if (url.username || url.password) return 'URL must not contain user information (user@).';
    if (url.search) return 'URL must not contain a query string (?).';
    if (url.hash) return 'URL must not contain a fragment (#).';
    if (isPrivateHost(url.hostname)) return 'Private and loopback hosts are rejected.';
    return '';
  }

  /* ------------------------------------------------------------------ S10 */

  GW.inspectRequest = function (row) {
    if (!row || !GW.state.role || GW.state.role === 'platform_admin' || row.tenantId !== GW.state.tenantId) {
      C.toast('Request not found in this workspace.'); return;
    }
    var measured = row.status !== 'denied';
    var ms = function (value) { return measured && Number.isFinite(value) ? C.num(value) + ' ms' : '—'; };
    var details = [
      ['Timestamp', dt(row.time)], ['Project', nameOf(GW.data.projects, row.projectId)],
      ['API key', nameOf(GW.data.keys, row.keyId)], ['Model', aliasOf(GW.data.models, row.modelId)],
      ['Provider', measured ? nameOf(GW.data.providers, row.providerId) : 'Not forwarded'],
      ['HTTP status', row.http], ['Upstream status', row.upstreamStatus || '—'],
      ['Latency', ms(row.latency)], ['Time to first byte', ms(row.ttfb)],
      ['Input tokens', C.num(row.input)], ['Output tokens', C.num(row.output)],
      ['Cached tokens', C.num(row.cache)], ['Cost', C.money(row.cost)],
      ['Attempts', measured ? row.attempt : 0], ['Fallback', row.fallbackUsed ? 'Used' : 'Not used'],
      ['Finish reason', row.finishReason || '—']
    ];
    var body = '<div class="request-overview">' + C.badge(row.status) +
      '<span class="muted">Simulated request metadata</span></div>' +
      '<div class="request-meta"><span class="hint">Request ID</span><code>' + C.e(row.request) +
      '</code>' + C.button('Copy request ID', function () { C.copy(row.request); }) + '</div>' +
      (row.reason ? C.alert(row.status === 'denied' ? 'warning' : 'error', row.reason) : '') +
      '<dl class="detail-grid">' + details.map(function (item) {
        return '<div class="detail-row"><dt>' + C.e(item[0]) + '</dt><dd>' + C.e(item[1]) + '</dd></div>';
      }).join('') + '</dl>';
    if (GW.canWrite()) {
      var project = find(GW.data.projects, row.projectId);
      var stored = project && project.logContent && parseContent(row.content);
      var expired = row.expires && new Date(row.expires) <= new Date(GW.now);
      if (stored && !expired) {
        var request = stored === true ? {model:aliasOf(GW.data.models,row.modelId),stream:true,request_id:row.request} : stored.request !== undefined ? stored.request : stored;
        var response = stored === true ? {id:row.request,model:aliasOf(GW.data.models,row.modelId),status:row.status,usage:{input_tokens:row.input,output_tokens:row.output,cached_tokens:row.cache},latency_ms:row.latency} : stored.response !== undefined ? stored.response : stored.output;
        body += '<details class="request-content"><summary>Stored content</summary><p class="hint">Simulated payload · expires ' + C.e(dt(row.expires)) +
          '</p><h3>Request</h3><pre>' + C.e(pretty(request)) + '</pre><h3>Response</h3><pre>' + C.e(pretty(response)) + '</pre></details>';
      } else body += C.alert('info', 'Content is only available if the project stores conversation content and the retention period has not passed.');
    } else body += '<p class="hint">Viewer access: metadata only. Stored conversation content is restricted.</p>';
    C.modal({title:'Request details',drawer:true,body:body});
  };

  GW.openRequest = async function (requestId) {
    var row = GW.usageRows().find(function (item) { return item.request === requestId; });
    if (!row || GW.state.role === 'platform_admin') { C.toast('Request not found in this workspace.'); return; }
    if (GW.location().path !== '/usage') {
      await new Promise(function (resolve) {
        window.addEventListener('hashchange', resolve, {once:true});
        GW.navigate('/usage');
      });
    }
    GW.inspectRequest(row);
  };

  GW.views['/usage'] = function () {
    var vs = GW.viewState('/usage', {
      sumFrom: '', sumTo: '', group: 'day',
      logFrom: '', logTo: '',
      project: 'all', model: 'all', key: 'all', status: 'all',
      size: 25, page: 1
    });
    var tenantUsage = GW.usageRows();

    function summaryRows() {
      var filtered = tenantUsage.filter(function (row) {
        var day = String(row.time).slice(0, 10);
        return (!vs.sumFrom || day >= vs.sumFrom) && (!vs.sumTo || day <= vs.sumTo);
      });
      var keyFn = vs.group === 'project' ? function (row) { return nameOf(GW.data.projects, row.projectId); }
        : vs.group === 'model' ? function (row) { return aliasOf(GW.data.models, row.modelId); }
          : vs.group === 'key' ? function (row) { return nameOf(GW.data.keys, row.keyId); }
            : function (row) { return String(row.time).slice(0, 10); };
      var rows = GW.aggregate(filtered, keyFn) || [];
      if (vs.group === 'day') {
        rows.sort(function (a, b) { return a.name < b.name ? -1 : a.name > b.name ? 1 : 0; });
      } else {
        rows.sort(function (a, b) { return b.requests - a.requests; });
      }
      return rows;
    }

    function logRows() {
      return tenantUsage.filter(function (row) {
        if (!inTimes(row.time, vs.logFrom, vs.logTo)) return false;
        if (vs.project !== 'all' && String(row.projectId) !== vs.project) return false;
        if (vs.model !== 'all' && String(row.modelId) !== vs.model) return false;
        if (vs.key !== 'all' && String(row.keyId) !== vs.key) return false;
        if (vs.status !== 'all' && row.status !== vs.status) return false;
        return true;
      }).sort(byTimeDesc);
    }

    function resetPage() { vs.page = 1; GW.render(); }
    function setSumFrom(value) { vs.sumFrom = String(value || ''); GW.render(); }
    function setSumTo(value) { vs.sumTo = String(value || ''); GW.render(); }
    function setGroup(value) { vs.group = String(value || 'day'); GW.render(); }
    function setLogFrom(value) { vs.logFrom = String(value || ''); resetPage(); }
    function setLogTo(value) { vs.logTo = String(value || ''); resetPage(); }
    function setProject(value) { vs.project = String(value || 'all'); resetPage(); }
    function setModel(value) { vs.model = String(value || 'all'); resetPage(); }
    function setKey(value) { vs.key = String(value || 'all'); resetPage(); }
    function setStatus(value) { vs.status = String(value || 'all'); resetPage(); }
    function setSize(value) { vs.size = Number(value) || 25; resetPage(); }
    function onPage(page) {
      vs.page = page;
      GW.render();
    }

    function exportCsv() {
      var head = ['Time', 'Request', 'Status', 'HTTP', 'Reason', 'Input', 'Output', 'Cache', 'Cost', 'Latency', 'Attempt', 'Project', 'Model', 'Key'];
      var lines = [head.join(',')];
      logRows().forEach(function (row) {
        lines.push([
          row.time, row.request, row.status, row.http, row.reason || '',
          row.input, row.output, row.cache, row.cost, row.latency, row.attempt,
          nameOf(GW.data.projects, row.projectId),
          aliasOf(GW.data.models, row.modelId),
          nameOf(GW.data.keys, row.keyId)
        ].map(csvCell).join(','));
      });
      downloadCsv('usage-log.csv', lines);
    }

    var summary = summaryRows();
    var summaryHeaders = [vs.group === 'day' ? 'Day' : 'Name', 'Requests', 'Denied', 'Errors', 'Input tokens', 'Output tokens', 'Cost'];
    var summaryBody = summary.map(function (row) {
      return [C.e(row.name), C.num(row.requests), C.num(row.denied), C.num(row.errors), C.num(row.input), C.num(row.output), C.money(row.cost)];
    });

    var summaryToolbar = '<div class="toolbar">' +
      C.field('usageSumFrom', 'From', 'date', vs.sumFrom, { onChange: setSumFrom }) +
      C.field('usageSumTo', 'To', 'date', vs.sumTo, { onChange: setSumTo }) +
      C.field('usageGroup', 'Group by', 'select', vs.group, {
        options: [{ value: 'day', label: 'Day' }, { value: 'project', label: 'Project' }, { value: 'model', label: 'Model' }, { value: 'key', label: 'API key' }],
        onChange: setGroup
      }) +
      '</div>';

    var allLog = logRows();
    var pages = Math.max(1, Math.ceil(allLog.length / vs.size));
    vs.page = Math.max(1, Math.min(vs.page, pages));
    var pageRows = allLog.slice((vs.page - 1) * vs.size, vs.page * vs.size);

    var logToolbar = '<div class="toolbar">' +
      C.field('usageLogFrom', 'From', 'datetime-local', vs.logFrom, { onChange: setLogFrom }) +
      C.field('usageLogTo', 'To', 'datetime-local', vs.logTo, { onChange: setLogTo }) +
      C.field('usageProject', 'Project', 'select', vs.project, {
        options: [{ value: 'all', label: 'All' }].concat(GW.data.projects.map(function (p) { return { value: String(p.id), label: p.name }; })),
        onChange: setProject
      }) +
      C.field('usageModel', 'Model', 'select', vs.model, {
        options: [{ value: 'all', label: 'All' }].concat(GW.data.models.map(function (m) { return { value: String(m.id), label: m.alias }; })),
        onChange: setModel
      }) +
      C.field('usageKey', 'API key', 'select', vs.key, {
        options: [{ value: 'all', label: 'All' }].concat(GW.data.keys.map(function (k) { return { value: String(k.id), label: k.name }; })),
        onChange: setKey
      }) +
      C.field('usageStatus', 'Status', 'select', vs.status, {
        options: [{ value: 'all', label: 'All' }, { value: 'ok', label: 'ok' }, { value: 'error', label: 'error' }, { value: 'denied', label: 'denied' }],
        onChange: setStatus
      }) +
      C.field('usageSize', 'Rows per page', 'select', String(vs.size), {
        options: [25, 50, 100, 200].map(function (n) { return { value: String(n), label: String(n) }; }),
        onChange: setSize
      }) +
      C.button('Export CSV', exportCsv, 'ghost') +
      '</div>';

    var logBody = pageRows.map(function (row) {
      return [
        dt(row.time),
        C.button(String(row.request).slice(0, 8), function () { GW.inspectRequest(row); }, 'ghost', 'aria-label="Inspect request ' + C.e(row.request) + '"'),
        C.badge(row.status),
        C.e(String(row.http)),
        row.reason ? C.e(row.reason) : '—',
        C.num(row.input),
        C.num(row.output),
        C.num(row.cache),
        C.money(row.cost),
        row.latency == null ? '—' : C.num(row.latency) + ' ms',
        C.e(String(row.attempt)),
        C.e(nameOf(GW.data.projects, row.projectId)),
        '<span class="mono">' + C.e(aliasOf(GW.data.models, row.modelId)) + '</span>',
        C.e(nameOf(GW.data.keys, row.keyId)),
        C.button('Inspect', function () { GW.inspectRequest(row); }, 'ghost')
      ];
    });

    return C.page('Usage', 'Aggregate summary and per-request log.') +
      C.card('Summary', summaryToolbar + C.table(summaryHeaders, summaryBody, { empty: 'No data yet.' })) +
      C.card('Request log',
        logToolbar +
        C.table(['Time', 'Request', 'Status', 'HTTP', 'Reason', 'Input', 'Output', 'Cache', 'Cost', 'Latency', 'Attempt', 'Project', 'Model', 'Key', 'Details'], logBody, { empty: 'No data yet.' }) +
        C.pager(allLog.length, vs, onPage));
  };

  /* ------------------------------------------------------------------ S11 */

  GW.views['/alerts'] = function () {
    var alerts = GW.data.alerts;
    var webhooks = GW.data.webhooks;
    var canWrite = GW.canWrite();

    function scopeLabel(scope, scopeId) {
      if (scope === 'project') return 'project: ' + nameOf(GW.data.projects, scopeId);
      if (scope === 'key') return 'key: ' + nameOf(GW.data.keys, scopeId);
      return 'tenant';
    }

    function webhookLabel(id) {
      return id === null || id === undefined ? '—' : nameOf(webhooks, id);
    }

    function toggleScope(value) {
      var projectField = document.getElementById('alertScopeProjectField');
      var keyField = document.getElementById('alertScopeKeyField');
      if (projectField) projectField.hidden = value !== 'project';
      if (keyField) keyField.hidden = value !== 'key';
    }

    function deleteAlert(alert) {
      C.confirm('Delete alert "' + alert.name + '"?', function () {
        var index = GW.data.alerts.indexOf(alert);
        if (index >= 0) GW.data.alerts.splice(index, 1);
        GW.record('alert.delete', 'alert', alert.id, { name: alert.name });
        GW.render();
      }, 'Delete');
    }

    function openDialog(alert) {
      var editing = !!alert;
      var scope = editing ? alert.scope : 'tenant';
      var scopeId = editing ? alert.scopeId : null;
      var projectOptions = [{ value: '', label: 'Select project…' }].concat(GW.data.projects.map(function (p) { return { value: String(p.id), label: p.name }; }));
      var keyOptions = [{ value: '', label: 'Select API key…' }].concat(GW.data.keys.map(function (k) { return { value: String(k.id), label: k.name }; }));
      var webhookOptions = [{ value: '', label: '(no webhook)' }].concat(webhooks.map(function (w) { return { value: String(w.id), label: w.name }; }));

      var body =
        C.field('name', 'Name', 'text', editing ? alert.name : '', { required: true }) +
        C.field('metric', 'Metric', 'select', editing ? alert.metric : 'monthly_tokens', { options: ['daily_tokens', 'monthly_tokens', 'monthly_budget'] }) +
        C.field('scope', 'Scope', 'select', scope, { options: ['tenant', 'project', 'key'] }) +
        '<div id="alertScopeProjectField"' + (scope === 'project' ? '' : ' hidden') + '>' +
          C.field('scopeProject', 'Project', 'select', scope === 'project' && scopeId != null ? String(scopeId) : '', { options: projectOptions }) +
        '</div>' +
        '<div id="alertScopeKeyField"' + (scope === 'key' ? '' : ' hidden') + '>' +
          C.field('scopeKey', 'API key', 'select', scope === 'key' && scopeId != null ? String(scopeId) : '', { options: keyOptions }) +
        '</div>' +
        C.field('threshold', 'Threshold (%)', 'number', editing ? alert.threshold : 80, {
          min: 1, max: 100, step: 1, required: true,
          hint: '1–100. 80 = warning, 100 = critical.'
        }) +
        C.field('webhook', 'Webhook', 'select', editing && alert.webhookId != null ? String(alert.webhookId) : '', {
          options: webhookOptions,
          hint: webhooks.length ? undefined : 'No webhooks yet; alerts are still recorded in the event list.'
        }) +
        (editing ? C.field('detach', 'Detach webhook from this rule', 'checkbox', false) : '') +
        C.field('enabled', 'Rule enabled', 'checkbox', editing ? alert.enabled : true);

      C.modal({
        title: editing ? 'Edit alert — ' + alert.name : 'Add alert',
        body: body,
        submit: 'Save alert',
        validate: function (form) {
          var threshold = Number(form.elements['threshold'].value);
          if (!(threshold >= 1 && threshold <= 100)) return 'Threshold must be between 1 and 100.';
          var value = form.elements['scope'].value;
          if (value === 'project' && !form.elements['scopeProject'].value) return 'Select a project for the rule scope.';
          if (value === 'key' && !form.elements['scopeKey'].value) return 'Select an API key for the rule scope.';
          return '';
        },
        onOpen: function (dialog) {
          var select = dialog.querySelector('select[name="scope"]');
          if (select) {
            select.addEventListener('change', function () { toggleScope(select.value); });
          }
        },
        onSubmit: function (form) {
          var scopeValue = form.elements['scope'].value;
          var data = {
            name: String(form.elements['name'].value || '').trim(),
            metric: form.elements['metric'].value,
            scope: scopeValue,
            scopeId: scopeValue === 'project' ? Number(form.elements['scopeProject'].value)
              : scopeValue === 'key' ? Number(form.elements['scopeKey'].value) : null,
            threshold: Number(form.elements['threshold'].value),
            webhookId: form.elements['webhook'].value ? Number(form.elements['webhook'].value) : null,
            enabled: form.elements['enabled'].checked
          };
          var detach = form.elements['detach'];
          if (detach && detach.checked) data.webhookId = null;
          if (editing) {
            for (var key in data) alert[key] = data[key];
            GW.record('alert.update', 'alert', alert.id, { name: data.name, threshold: data.threshold });
            C.toast('Alert saved.');
          } else {
            var id = GW.nextId(GW.data.alerts);
            data.id = id;
            data.lastTriggered = null;
            GW.data.alerts.push(data);
            GW.record('alert.create', 'alert', id, { name: data.name, threshold: data.threshold });
            C.toast('Alert created.');
          }
          GW.render();
        }
      });
    }

    var ruleRows = alerts.map(function (alert) {
      return [
        C.e(alert.name),
        '<span class="mono">' + C.e(alert.metric) + '</span>',
        C.e(scopeLabel(alert.scope, alert.scopeId)),
        C.e(String(alert.threshold)) + '%',
        C.e(webhookLabel(alert.webhookId)),
        dt(alert.lastTriggered),
        C.badge(alert.enabled ? 'active' : 'inactive'),
        '<div class="row-actions">' + (canWrite
          ? C.button('Edit', function () { openDialog(alert); }, 'ghost') + ' ' + C.button('Delete', function () { deleteAlert(alert); }, 'danger')
          : '<span class="muted">read-only</span>') + '</div>'
      ];
    });

    var events = GW.data.events.slice().sort(byTimeDesc).slice(0, 50);
    var eventRows = events.map(function (event) {
      var rule = find(alerts, event.ruleId);
      var limit = Number(event.limit) > 0 ? Number(event.limit) : 100;
      var used = Number(event.used) || 0;
      return [
        dt(event.time),
        C.e(rule ? rule.name : '—'),
        '<span class="mono">' + C.e(event.metric) + '</span>',
        C.e(scopeLabel(event.scope, event.scopeId)),
        C.e(String(event.threshold)) + '%',
        C.e(String(event.period)),
        '<div class="stack">' + C.num(used) + '<progress max="' + limit + '" value="' + Math.min(used, limit) + '"></progress></div>',
        event.limit === null || event.limit === undefined ? '—' : C.num(event.limit)
      ];
    });

    return C.page('Alerts', 'Quota notifications based on usage percentage. Two rules are recommended: 80 (warning) and 100 (critical).',
        canWrite ? C.button('Add alert', function () { openDialog(null); }, 'primary') : '') +
      C.table(['Name', 'Metric', 'Scope', 'Threshold', 'Webhook', 'Last triggered', 'Status', 'Actions'], ruleRows, { empty: 'No data yet.' }) +
      C.card('Recent events', C.table(['Time', 'Rule', 'Metric', 'Scope', 'Threshold', 'Period', 'Used', 'Limit'], eventRows, { empty: 'No events yet.' }));
  };

  /* ------------------------------------------------------------------ S12 */

  GW.views['/webhooks'] = function () {
    var canWrite = GW.canWrite();

    function secretLabel(webhook) {
      var hint = webhook.secretHint ? String(webhook.secretHint) : '';
      if (!hint) return '—';
      return hint.charAt(0) === '…' ? hint : '…' + hint;
    }

    function openDialog(webhook) {
      var editing = !!webhook;
      var body =
        C.field('name', 'Name', 'text', editing ? webhook.name : '', { required: true }) +
        C.field('url', 'URL', 'text', editing ? webhook.url : '', {
          required: true,
          placeholder: 'https://example.com/hooks/gateway',
          hint: 'https://host/path only — no ?query, user@, or #fragment. Private/loopback IP hosts are rejected.'
        }) +
        C.field('secret', editing ? 'New signing secret (optional)' : 'Signing secret', 'password', '', {
          required: !editing,
          hint: '16–200 characters. The secret is never shown again after saving.'
        }) +
        C.field('enabled', 'Webhook enabled', 'checkbox', editing ? webhook.enabled : true);

      C.modal({
        title: editing ? 'Edit webhook — ' + webhook.name : 'Add webhook',
        body: body,
        submit: 'Save webhook',
        validate: function (form) {
          var urlError = validateWebhookUrl(form.elements['url'].value);
          if (urlError) return urlError;
          var secret = String(form.elements['secret'].value || '');
          if (!(editing && secret === '') && (secret.length < 16 || secret.length > 200)) {
            return 'Signing secret must be 16–200 characters.';
          }
          return '';
        },
        onSubmit: function (form) {
          var name = String(form.elements['name'].value || '').trim();
          var url = String(form.elements['url'].value || '').trim();
          var secret = String(form.elements['secret'].value || '');
          var enabled = form.elements['enabled'].checked;
          if (editing) {
            webhook.name = name;
            webhook.url = url;
            webhook.enabled = enabled;
            webhook.updated = GW.now;
            if (secret) webhook.secretHint = secret.slice(-4);
            GW.record('webhook.update', 'webhook', webhook.id, { name: name, url: url });
            C.toast('Webhook saved.');
          } else {
            var id = GW.nextId(GW.data.webhooks);
            GW.data.webhooks.push({
              id: id,
              name: name,
              url: url,
              secretHint: secret.slice(-4),
              enabled: enabled,
              updated: GW.now
            });
            GW.record('webhook.create', 'webhook', id, { name: name, url: url });
            C.toast('Webhook created.');
          }
          GW.render();
        }
      });
    }

    function deleteWebhook(webhook) {
      C.confirm('Delete webhook "' + webhook.name + '"?', function () {
        var index = GW.data.webhooks.indexOf(webhook);
        if (index >= 0) GW.data.webhooks.splice(index, 1);
        GW.record('webhook.delete', 'webhook', webhook.id, { name: webhook.name });
        GW.render();
      }, 'Delete');
    }

    function openDeliveries(webhook) {
      var items = GW.data.deliveries
        .filter(function (item) { return item.webhookId === webhook.id; })
        .sort(byCreatedDesc);
      var rows = items.map(function (item) {
        return [
          dt(item.created),
          C.badge(item.status),
          item.attempt === null || item.attempt === undefined ? '—' : C.e(String(item.attempt)),
          item.code === null || item.code === undefined ? '—' : C.e(String(item.code)),
          item.nextAttempt ? C.e(C.date(item.nextAttempt)) : '—',
          typeof item.completed === 'string' && item.completed ? C.e(C.date(item.completed)) : (item.completed === true ? 'yes' : '—'),
          item.error ? C.e(item.error) : '—'
        ];
      });
      C.modal({
        title: 'Deliveries — ' + webhook.name,
        wide: true,
        body: C.table(['Created', 'Status', 'Attempt', 'Code', 'Next attempt', 'Completed', 'Error'], rows, { empty: 'No deliveries yet.' })
      });
    }

    var rows = GW.data.webhooks.map(function (webhook) {
      var actions = [C.button('Deliveries', function () { openDeliveries(webhook); }, 'ghost')];
      if (canWrite) {
        actions.push(C.button('Edit', function () { openDialog(webhook); }, 'ghost'));
        actions.push(C.button('Delete', function () { deleteWebhook(webhook); }, 'danger'));
      }
      return [
        C.e(webhook.name),
        '<span class="mono">' + C.e(webhook.url) + '</span>',
        '<span class="mono">' + C.e(secretLabel(webhook)) + '</span>',
        C.badge(webhook.enabled ? 'active' : 'inactive'),
        dt(webhook.updated),
        '<div class="row-actions">' + actions.join(' ') + '</div>'
      ];
    });

    return C.page('Webhooks', 'Outgoing notifications when an alert threshold is exceeded; payloads are signed with HMAC-SHA256 (X-Gateway-Signature header).',
        canWrite ? C.button('Add webhook', function () { openDialog(null); }, 'primary') : '') +
      C.table(['Name', 'URL', 'Secret', 'Status', 'Updated', 'Actions'], rows, { empty: 'No data yet.' });
  };

  /* ------------------------------------------------------------------ S13 */

  GW.views['/users'] = function () {
    if (GW.state.role !== 'owner') {
      return C.page('Users', 'Manage tenant members. Only owners can change roles and invite users.') +
        C.alert('error', 'Page “/users” was not found or is not available for your role.');
    }

    function roleCell(user) {
      var action = GW.action(function (event) {
        var value = event.target.value;
        inlineUpdate(event, function () {
          user.role = value;
          GW.record('user.role', 'user', user.id, { email: user.email, role: value });
        });
      });
      return '<select data-change="' + action + '" aria-label="' + C.e('Role for ' + user.email) + '">' +
        optsHTML(['owner', 'admin', 'viewer'], user.role) + '</select>';
    }

    function statusCell(user) {
      var action = GW.action(function (event) {
        var checked = event.target.checked;
        inlineUpdate(event, function () {
          user.enabled = checked;
          GW.record('user.status', 'user', user.id, { email: user.email, enabled: checked });
        });
      });
      return '<label class="check-row"><input type="checkbox" data-change="' + action + '"' +
        (user.enabled ? ' checked' : '') + ' aria-label="' + C.e('Status for ' + user.email) + '">' +
        '<span>' + (user.enabled ? 'active' : 'inactive') + '</span></label>';
    }

    function resetPassword(user) {
      GW.record('user.reset', 'user', user.id, { email: user.email });
      showSecret('Password reset token — ' + user.email, GW.secret('rst'));
    }

    function openInvite() {
      C.modal({
        title: 'Invite user',
        submit: 'Create invitation',
        loading: 'Sending…',
        body: C.field('email', 'Email', 'email', '', { required: true }) +
          C.field('displayName', 'Display name', 'text', '') +
          C.field('role', 'Role', 'select', 'viewer', { options: ['owner', 'admin', 'viewer'] }),
        onSubmit: function (form) {
          var email = String(form.elements['email'].value || '').trim();
          var user = {
            id: GW.nextId(GW.data.users),
            email: email,
            name: String(form.elements['displayName'].value || '').trim() || email,
            role: form.elements['role'].value,
            enabled: false,
            lastLogin: null,
            created: GW.now
          };
          GW.data.users.push(user);
          GW.record('user.invite', 'user', user.id, { email: email, role: user.role });
          showSecret('Invitation token — ' + email, GW.secret('inv'));
        }
      });
    }

    var rows = GW.data.users.map(function (user) {
      return [
        C.e(user.email),
        C.e(user.name),
        roleCell(user),
        statusCell(user),
        dt(user.lastLogin),
        dt(user.created),
        '<div class="row-actions">' + C.button('Reset password', function () { resetPassword(user); }, 'ghost') + '</div>'
      ];
    });

    return C.page('Users', 'Manage tenant members. Only owners can change roles and invite users.',
        C.button('Invite user', openInvite, 'primary')) +
      C.table(['Email', 'Name', 'Role', 'Status', 'Last login', 'Created', 'Actions'], rows, { empty: 'No data yet.' });
  };

  /* ------------------------------------------------------------------ S14 */

  GW.views['/audit'] = function () {
    var vs = GW.viewState('/audit', { from: '', to: '', size: 25, page: 1 });
    var tenantRows = GW.data.audit.filter(function (row) { return row.tenantId === GW.state.tenantId; });

    function setFrom(value) { vs.from = String(value || ''); vs.page = 1; GW.render(); }
    function setTo(value) { vs.to = String(value || ''); vs.page = 1; GW.render(); }
    function setSize(value) { vs.size = Number(value) || 25; vs.page = 1; GW.render(); }
    function onPage(page) {
      vs.page = page;
      GW.render();
    }

    var rows = tenantRows.filter(function (row) { return inTimes(row.time, vs.from, vs.to); }).sort(byTimeDesc);
    var pages = Math.max(1, Math.ceil(rows.length / vs.size));
    vs.page = Math.max(1, Math.min(vs.page, pages));
    var pageRows = rows.slice((vs.page - 1) * vs.size, vs.page * vs.size);

    var body = pageRows.map(function (row) {
      return [
        dt(row.time),
        row.userId === null || row.userId === undefined ? '—' : C.e(String(row.userId)),
        '<span class="mono">' + C.e(row.action) + '</span>',
        row.entity ? C.e(row.entity) : '—',
        row.entityId === null || row.entityId === undefined ? '—' : '<span class="mono">' + C.e(String(row.entityId)) + '</span>',
        row.ip ? C.e(row.ip) : '—',
        row.detail === null || row.detail === undefined || row.detail === '' ? '—' :
          '<details><summary>view</summary><pre class="mono">' + C.e(pretty(row.detail)) + '</pre></details>'
      ];
    });

    var toolbar = '<div class="toolbar">' +
      C.field('auditFrom', 'From', 'datetime-local', vs.from, { onChange: setFrom }) +
      C.field('auditTo', 'To', 'datetime-local', vs.to, { onChange: setTo }) +
      C.field('auditSize', 'Rows per page', 'select', String(vs.size), {
        options: [25, 50, 100, 200].map(function (n) { return { value: String(n), label: String(n) }; }),
        onChange: setSize
      }) +
      '</div>';

    return C.page('Audit', 'Tenant configuration change trail.') +
      toolbar +
      C.table(['Time', 'User', 'Action', 'Entity', 'Entity ID', 'IP', 'Detail'], body, { empty: 'No data yet.' }) +
      C.pager(rows.length, vs, onPage);
  };
})();
