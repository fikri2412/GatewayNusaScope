/* Classic scripts intentionally support file:// without a server. */
window.GW = { views: {}, mounts: {}, handlers: {}, data: {}, state: { role: null, tenantId: 1, views: {} }, now: '2026-10-07T16:00:00Z' };
(function (G) {
  'use strict';
  let serial = 0;
  const C = G.C = {};
  C.e = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  C.num = n => Number(n || 0).toLocaleString('en-US');
  C.money = n => 'USD ' + Number(n || 0).toFixed(4);
  C.date = value => value ? new Date(value).toISOString().replace('T', ' ').slice(0, 16) + ' UTC' : '—';
  G.action = fn => { const id = 'a' + ++serial; G.handlers[id] = fn; return id; };
  C.button = (label, fn, variant = 'ghost', attrs = '') => `<button type="button" class="btn btn-${C.e(variant)}" data-action="${G.action(fn)}" ${attrs}>${C.e(label)}</button>`;
  C.alert = (kind, text) => `<div class="alert alert-${C.e(kind)}" role="${kind === 'error' ? 'alert' : 'status'}">${C.e(text)}</div>`;
  C.badge = status => { const good = ['active','ok','succeeded'].includes(status), bad = ['inactive','suspended','error','denied','failed'].includes(status) || String(status).startsWith('revoked'); return `<span class="badge ${good ? 'badge-success' : bad ? 'badge-danger' : 'badge-neutral'}">${C.e(status)}</span>`; };
  C.page = (title, description, action = '') => `<header class="page-header"><div><h1>${C.e(title)}</h1><p class="page-description">${C.e(description)}</p></div>${action}</header>`;
  C.card = (title, body) => `<section class="card">${title ? `<div class="card-header"><h2>${C.e(title)}</h2></div>` : ''}<div class="card-body">${body}</div></section>`;
  C.table = (headers, rows, opts = {}) => {
    const mode = G.demo;
    const empty = mode === 'error' ? 'Data unavailable.' : opts.empty || 'No data yet.';
    const body = ['empty','loading','error'].includes(mode) ? [] : rows;
    const contents = mode === 'loading'
      ? Array.from({length:4}, () => `<tr aria-hidden="true">${headers.map(() => '<td><span class="skeleton skeleton-cell"></span></td>').join('')}</tr>`).join('')
      : body.length ? body.map(cells => `<tr>${cells.map(cell => `<td>${cell ?? '—'}</td>`).join('')}</tr>`).join('')
      : `<tr><td colspan="${headers.length}" class="empty-row"><div class="empty-state"><span class="empty-icon" aria-hidden="true">${G.icon?.('states') || '—'}</span><strong>${C.e(empty)}</strong><span>${mode === 'error' ? 'Reload the page to try again.' : 'Results will appear here when available.'}</span></div></td></tr>`;
    return `${mode === 'error' ? C.alert('error', 'Unable to load data. Please try again.') : ''}${mode === 'loading' ? '<span class="sr-only" role="status">Loading data.</span>' : ''}<div class="table-wrap" ${mode === 'loading' ? 'aria-busy="true"' : ''}><table>${opts.caption ? `<caption class="sr-only">${C.e(opts.caption)}</caption>` : ''}<thead><tr>${headers.map(x => `<th scope="col">${C.e(x)}</th>`).join('')}</tr></thead><tbody>${contents}</tbody></table></div>`;
  };
  C.options = (items, value = 'id', label = 'name') => items.map(x => ({ value: x[value], label: x[label] }));
  C.field = (name, label, type, value, opts = {}) => {
    const id = 'field-' + ++serial;
    const attrs = ['required','disabled','readonly'].filter(x => opts[x]).map(x => x).join(' ') + ['min','max','step','placeholder'].filter(x => opts[x] != null).map(x => ` ${x}="${C.e(opts[x])}"`).join('') + (opts.hint ? ` aria-describedby="${id}-hint"` : '');
    const change = opts.onChange ? ` data-change="${G.action(event => opts.onChange(event.target.type === 'checkbox' ? event.target.checked : event.target.value, event))}"` : '';
    let input;
    if (type === 'select') input = `<select id="${id}" name="${C.e(name)}" ${attrs}${change}>${(opts.options || []).map(o => { const v = typeof o === 'object' ? o.value : o, text = typeof o === 'object' ? o.label : o; return `<option value="${C.e(v)}" ${String(v) === String(value ?? '') ? 'selected' : ''}>${C.e(text)}</option>`; }).join('')}</select>`;
    else if (type === 'textarea') input = `<textarea id="${id}" name="${C.e(name)}" rows="${opts.rows || 4}" ${attrs}${change}>${C.e(value)}</textarea>`;
    else input = `<input id="${id}" name="${C.e(name)}" type="${C.e(type)}" ${type === 'checkbox' ? (value ? 'checked' : '') : `value="${C.e(value)}"`} ${attrs}${change}>`;
    return `<div class="field ${type === 'checkbox' ? 'check-row' : ''}">${type === 'checkbox' ? input : ''}<label class="field-label" for="${id}">${C.e(label)}</label>${type !== 'checkbox' ? input : ''}${opts.hint ? `<span class="hint" id="${id}-hint">${C.e(opts.hint)}</span>` : ''}</div>`;
  };
  const delay = () => new Promise(resolve => setTimeout(resolve, 400));
  async function save(event, opts, dialog) {
    event.preventDefault();
    const form = event.target;
    if (form.dataset.busy || !form.reportValidity()) return;
    const errorBox = form.querySelector('[data-form-error]');
    errorBox.innerHTML = '';
    const message = opts.validate?.(form);
    if (message) { errorBox.innerHTML = C.alert('error', message); return; }
    const button = form.querySelector('[type="submit"]'), original = button.textContent;
    form.dataset.busy = 'true'; button.disabled = true; button.textContent = opts.loading || 'Saving…';
    try {
      await delay();
      const keep = await opts.onSubmit(form, dialog);
      if (dialog && keep !== false && dialog.open) { dialog.close(); dialog.remove(); }
    } catch (error) { errorBox.innerHTML = C.alert('error', error.message || 'Unable to save changes.'); }
    finally { delete form.dataset.busy; button.disabled = false; button.textContent = original; }
  }
  C.form = (body, submit, onSubmit, opts = {}) => `<form data-submit="${G.action(event => save(event, { ...opts, onSubmit }))}"><div data-form-error></div><div class="form-grid">${body}</div><div class="form-actions"><button class="btn btn-primary" type="submit">${C.e(submit)}</button></div></form>`;
  C.modal = opts => {
    const dialog = document.createElement('dialog');
    dialog.className = 'modal' + (opts.drawer ? ' request-drawer' : opts.wide ? ' modal-wide' : '');
    const titleId = 'dialog-title-' + ++serial;
    dialog.setAttribute('aria-labelledby', titleId);
    dialog.innerHTML = `<header class="modal-header"><h2 id="${titleId}">${C.e(opts.title)}</h2><button type="button" class="btn btn-ghost modal-close" aria-label="Close dialog">✕</button></header><form><div class="modal-body"><div data-form-error></div>${opts.body}</div><footer class="modal-footer"><button type="button" class="btn btn-ghost" data-close>${C.e(opts.cancel || (opts.submit ? 'Cancel' : 'Close'))}</button>${opts.submit ? `<button type="submit" class="btn btn-${opts.danger ? 'danger' : 'primary'}">${C.e(opts.submit)}</button>` : ''}</footer></form>`;
    document.body.append(dialog);
    dialog.querySelector('.modal-close').onclick = () => dialog.close();
    dialog.querySelector('[data-close]').onclick = () => dialog.close();
    dialog.addEventListener('close', () => { dialog.remove(); opts.onClose?.(); }, { once: true });
    if (opts.submit) dialog.querySelector('form').addEventListener('submit', event => save(event, opts, dialog));
    else dialog.querySelector('form').addEventListener('submit', event => event.preventDefault());
    dialog.showModal(); opts.onOpen?.(dialog); return dialog;
  };
  C.confirm = (message, onConfirm, label = 'Delete') => C.modal({ title: 'Confirm action', body: `<p>${C.e(message)}</p>`, submit: label, danger: true, onSubmit: onConfirm });
  C.toast = text => {
    const region = document.getElementById('toasts');
    region.showPopover();
    const node = document.createElement('div'); node.className = 'toast'; node.textContent = text; region.append(node);
    setTimeout(() => { node.remove(); if (!region.children.length) region.hidePopover(); }, 3500);
  };
  C.copy = async value => {
    const active = document.activeElement;
    try {
      let copied = false;
      try { if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(value); copied = true; } } catch {}
      if (!copied) {
        const textarea = document.createElement('textarea');
        textarea.value = value; textarea.className = 'clipboard-fallback';
        (document.querySelector('dialog[open]') || document.body).append(textarea);
        textarea.select(); copied = document.execCommand('copy'); textarea.remove();
        active?.focus();
        if (!copied) throw new Error('Copy unavailable');
      }
      C.toast('Copied to clipboard.'); return true;
    } catch { C.toast('Copy unavailable. Select and copy the value.'); return false; }
  };
  C.secret = (title, value) => {
    const old = document.getElementById('one-time-secret'); old?.remove();
    const node = document.createElement('section'); node.id = 'one-time-secret'; node.className = 'secret-panel';
    node.innerHTML = `<strong>${C.e(title)}</strong><code>${C.e(value)}</code><div class="inline"><button type="button" class="btn btn-ghost" data-copy>Copy</button><button type="button" class="btn btn-ghost" data-secret-close>Close</button></div>`;
    document.getElementById('content').prepend(node);
    node.querySelector('[data-copy]').onclick = () => C.copy(value);
    node.querySelector('[data-secret-close]').onclick = () => node.remove();
    node.scrollIntoView({ block: 'nearest' });
  };
  C.pager = (total, state, onPage) => {
    const pages = Math.max(1, Math.ceil(total / Number(state.size || 25))); state.page = Math.min(pages, Math.max(1, state.page || 1));
    return `<nav class="pager" aria-label="Pagination">${C.button('Previous', () => onPage(state.page - 1), 'ghost', state.page <= 1 ? 'disabled' : '')}<span>Page ${state.page} of ${pages} (${C.num(total)} rows)</span>${C.button('Next', () => onPage(state.page + 1), 'ghost', state.page >= pages ? 'disabled' : '')}</nav>`;
  };
  G.canWrite = () => G.state.role !== 'viewer';
  G.nextId = rows => rows.reduce((max, row) => Math.max(max, row.id), 0) + 1;
  G.viewState = (path, defaults) => G.state.views[path] || (G.state.views[path] = { ...defaults });
  G.secret = (prefix = 'gw') => prefix + '_' + Array.from(crypto.getRandomValues(new Uint8Array(24)), n => n.toString(16).padStart(2, '0')).join('');
  G.record = (action, entity, id, detail = {}) => {
    const row = { id: G.nextId(G.data.audit), tenantId: G.state.role === 'platform_admin' ? null : G.state.tenantId, time: G.now, userId: G.state.role === 'platform_admin' ? 99 : 1, action, entity, entityId: id, ip: '203.0.113.10', detail };
    G.data.audit.unshift(row); G.data.platformAudit.unshift({ ...row, id: G.nextId(G.data.platformAudit) });
  };
  document.addEventListener('click', event => { if(event.target.closest('.skip-link')) { event.preventDefault(); const content=document.getElementById('content'); content?.focus(); content?.scrollIntoView(); return; } const node = event.target.closest('[data-action]'); if (node && !node.disabled) G.handlers[node.dataset.action]?.(event); });
  document.addEventListener('change', event => { const node = event.target.closest('[data-change]'); if (node && !node.disabled) G.handlers[node.dataset.change]?.(event); });
  document.addEventListener('submit', event => { const key = event.target.dataset.submit; if (key) G.handlers[key]?.(event); });
})(window.GW);
