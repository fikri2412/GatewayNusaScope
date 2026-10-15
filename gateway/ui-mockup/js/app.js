(function (G) {
  'use strict';
  const C = G.C;
  const paths = {
    dashboard:'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    providers:'M6 3v5 M18 3v5 M4 8h16v5a8 8 0 0 1-16 0z M12 21v-4',
    models:'M12 3 3 8l9 5 9-5-9-5z M3 12l9 5 9-5 M3 16l9 5 9-5',
    projects:'M3 7h7l2-3h9v16H3z',
    keys:'M8 15a5 5 0 1 1 4-8l9 0v4h-3v3h-4l-2-2 M6 8h.01',
    policies:'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6z M8 12l3 3 5-6',
    usage:'M4 20V10 M10 20V4 M16 20v-9 M22 20H2',
    alerts:'M6 9a6 6 0 0 1 12 0v6l3 3H3l3-3z M10 21h4',
    webhooks:'M7 7h10v10H7z M3 12h4 M17 12h4 M12 3v4 M12 17v4',
    audit:'M6 3h12v18H6z M9 7h6 M9 11h6 M9 15h4',
    users:'M15 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M5 21v-3a7 7 0 0 1 14 0v3 M20 5v6 M17 8h6',
    tenants:'M4 21V3h11v18 M15 10h5v11 M8 7h3 M8 11h3 M8 15h3 M1 21h22',
    plans:'M4 5h16v14H4z M4 10h16 M8 15h3',
    templates:'M5 3h10l4 4v14H5z M15 3v5h4 M8 12h8 M8 16h5',
    catalog:'M3 4h8v16H3z M13 4h8v16h-8z M6 8h2 M16 8h2',
    states:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 7v6 M12 17h.01',
    search:'M21 21l-5-5 M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
    collapse:'M9 3v18 M16 8l-4 4 4 4 M3 3h18v18H3z',
    menu:'M4 6h16 M4 12h16 M4 18h16',
  };
  G.icon = name => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${paths[name] || paths.states}"></path></svg>`;
  G.logo = () => '<span class="brand-logo" role="img" aria-label="GateWay NusaScope"><span class="brand-lockup"><img class="logo-dark" src="assets/logo-dark.png" alt="" width="520" height="139"><img class="logo-light" src="assets/logo-light.png" alt="" width="520" height="139"></span><img class="brand-symbol" src="assets/logo-mark.png" alt="" width="128" height="128"></span>';
  const tenantGroups = [
    {label:'Overview',items:[['/','Dashboard','dashboard']]},
    {label:'Configuration',items:[['/providers','Providers','providers'],['/models','Models','models'],['/projects','Projects','projects'],['/keys','API Keys','keys'],['/policies','Policies','policies']]},
    {label:'Monitoring',items:[['/usage','Usage','usage'],['/alerts','Alerts','alerts'],['/webhooks','Webhooks','webhooks'],['/audit','Audit','audit']]},
    {label:'Team',items:[['/users','Users','users']]}
  ];
  const platformGroups = [{label:'Platform',items:[['/platform/tenants','Tenants','tenants'],['/platform/plans','Plans','plans'],['/platform/templates','Templates','templates'],['/platform/catalog','Catalog','catalog'],['/platform/usage','Platform usage','usage'],['/platform/audit','Platform audit','audit']]}];
  G.location = () => { const raw = location.hash.slice(1) || '/login', split = raw.indexOf('?'); return {path:split < 0 ? raw : raw.slice(0,split),query:new URLSearchParams(split<0?'':raw.slice(split+1))}; };
  G.navigate = path => { if (location.hash === '#' + path) G.render(); else location.hash = path; };
  function storedTheme() { try { return localStorage.getItem('gw-demo-theme') || 'dark'; } catch { return 'dark'; } }
  G.theme = storedTheme();
  G.applyTheme = value => { G.theme = ['light','dark','system'].includes(value) ? value : 'system'; const dark=G.theme==='dark'||G.theme==='system'&&matchMedia('(prefers-color-scheme: dark)').matches; document.documentElement.dataset.theme=dark?'dark':'light'; try {localStorage.setItem('gw-demo-theme',G.theme);} catch {} G.chart?.destroy(); G.chart=null; if(G.location().path==='/') G.mounts['/']?.(); };
  G.themeField = () => C.field('theme','Theme','select',G.theme,{options:[{value:'light',label:'Light'},{value:'dark',label:'Dark'},{value:'system',label:'System'}],onChange:G.applyTheme});
  G.signIn = (role, name) => { G.state.role=role; G.state.displayName=name||({platform_admin:'Platform admin',owner:'Rina Putri',admin:'Dimas Pratama',viewer:'Sari Wulandari'}[role]); G.state.views={}; G.navigate(role==='platform_admin'?'/platform/tenants':'/'); };
  G.signOut = () => { G.state.role=null; G.state.displayName=null; G.state.views={}; G.navigate('/login'); };
  function permitted(path) {
    if (path === '/states') return true;
    if (G.state.role === 'platform_admin') return path.startsWith('/platform/');
    return !path.startsWith('/platform/') && (path !== '/users' || G.state.role === 'owner');
  }
  function nav(groups,path) { return groups.filter(group=>group.label!=='Team'||G.state.role==='owner').map(group=>`<div class="nav-group"><div class="nav-label">${C.e(group.label)}</div>${group.items.map(([route,label,icon])=>`<a class="nav-link" href="#${route}" title="${C.e(label)}" ${path===route?'aria-current="page"':''}>${G.icon(icon)}<span>${C.e(label)}</span></a>`).join('')}</div>`).join(''); }
  const commandShortcut = /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘ K' : 'Ctrl K';
  G.openCommand = () => {
    if (!G.state.role || document.querySelector('dialog[open]')) return;
    document.querySelector('.avatar-menu')?.removeAttribute('open');
    const platform = G.state.role === 'platform_admin';
    const commands = (platform ? platformGroups : tenantGroups).flatMap(group => group.items)
      .filter(([route]) => permitted(route))
      .map(([route,label,icon]) => ({label:`Go to ${label}`,icon,hint:'Navigate',run:()=>G.navigate(route)}));
    if (!platform) {
      commands.push({label:'Go to Account',icon:'users',hint:'Account',run:()=>G.navigate('/account')});
      if (G.canWrite()) commands.push({label:'Create project',icon:'projects',hint:'Action',run:async()=>{
        if (G.location().path !== '/projects') await new Promise(resolve => {
          window.addEventListener('hashchange', resolve, {once:true}); G.navigate('/projects');
        });
        G.createProject();
      }});
    }
    commands.push({label:'Go to State sheet',icon:'states',hint:'Prototype',run:()=>G.navigate('/states')});
    for (const theme of ['dark','light','system']) commands.push({label:`Use ${theme} theme`,icon:'states',hint:'Appearance',run:()=>G.applyTheme(theme)});
    const dialog = document.createElement('dialog');
    dialog.className = 'command-palette';
    dialog.setAttribute('aria-label','Command palette');
    dialog.innerHTML = `<div class="command-search">${G.icon('search')}<input type="search" role="combobox" aria-label="Search commands or request ID" aria-autocomplete="list" aria-expanded="true" aria-controls="command-results" placeholder="Search commands or request ID…" autocomplete="off"><button type="button" class="btn btn-ghost" aria-label="Close command palette"><kbd>Esc</kbd></button></div><div id="command-results" class="command-results" role="listbox" aria-label="Commands"></div><footer class="command-hint"><span>↑ ↓ to navigate · Enter to open</span><span>Current workspace only</span></footer>`;
    const input = dialog.querySelector('input'), results = dialog.querySelector('.command-results');
    let matches = [], selected = 0;
    function paintSelection() {
      [...results.children].forEach((node,index) => node.setAttribute('aria-selected',String(index===selected)));
      if (matches.length) {
        input.setAttribute('aria-activedescendant',`command-option-${selected}`);
        results.children[selected]?.scrollIntoView({block:'nearest'});
      } else input.removeAttribute('aria-activedescendant');
    }
    function activate(index) {
      const command = matches[index]; if (!command) return;
      dialog.close(); command.run();
    }
    function filter() {
      const query = input.value.trim().toLowerCase();
      matches = commands.filter(command => command.label.toLowerCase().includes(query));
      if (!platform && query.length >= 3) {
        const requests = G.usageRows().filter(row => row.request.toLowerCase().includes(query)).slice(0,8);
        matches.push(...requests.map(row => ({label:row.request,icon:'usage',hint:`Request · ${row.status}`,run:()=>G.openRequest(row.request)})));
      }
      selected = 0;
      results.innerHTML = matches.length ? matches.map((command,index) => `<button type="button" id="command-option-${index}" class="command-item" role="option" tabindex="-1" aria-selected="${index===0}">${G.icon(command.icon)}<span>${C.e(command.label)}</span><small>${C.e(command.hint)}</small></button>`).join('') : '<p class="empty-state" role="status">No matching commands or requests.</p>';
      results.querySelectorAll('.command-item').forEach((node,index) => node.addEventListener('click',()=>activate(index)));
      paintSelection();
    }
    input.addEventListener('input',filter);
    input.addEventListener('keydown',event => {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        if (matches.length) selected = (selected + (event.key==='ArrowDown'?1:matches.length-1)) % matches.length;
        paintSelection();
      } else if (event.key === 'Enter') { event.preventDefault(); activate(selected); }
    });
    dialog.querySelector('[aria-label="Close command palette"]').onclick = () => dialog.close();
    dialog.addEventListener('close',()=>dialog.remove(),{once:true});
    document.body.append(dialog); dialog.showModal(); filter(); input.focus();
  };

  const mobileQuery = matchMedia('(max-width:700px)');
  function syncNavigation() {
    const shell = document.querySelector('.app-shell'); if (!shell) return;
    const open = mobileQuery.matches && shell.classList.contains('drawer-open');
    shell.classList.toggle('drawer-open',open);
    shell.querySelector('.sidebar').inert = mobileQuery.matches && !open;
    shell.querySelector('.workspace').inert = open;
  }
  function setMobileNavigation(open) {
    const shell = document.querySelector('.app-shell'); if (!shell) return;
    shell.classList.toggle('drawer-open',open);
    syncNavigation();
    document.querySelector('[aria-label="Toggle navigation"]')?.setAttribute('aria-expanded',String(open));
    if (open) document.querySelector('.sidebar .nav-link')?.focus();
    else document.querySelector('[aria-label="Toggle navigation"]')?.focus();
  }
  G.render = () => {
    const {path,query}=G.location(); G.demo=query.get('demo')||'';
    G.chart?.destroy(); G.chart=null; G.handlers={};
    document.querySelectorAll('dialog').forEach(dialog=>{dialog.close();dialog.remove();});
    const root=document.getElementById('app');
    const auth=['/login','/invite','/reset'].includes(path);
    if(!auth&&!G.state.role) { G.navigate('/login'); return; }
    document.title=(path==='/'?'Dashboard':path.split('/').pop().replaceAll('-',' '))+' · GateWay NusaScope';
    if(auth) { root.innerHTML=G.views[path]?.()||''; G.mounts[path]?.(); return; }
    const platform=G.state.role==='platform_admin', groups=platform?platformGroups:tenantGroups;
    const view=G.views[path];
    const body=view&&permitted(path)?view():C.alert('warning',`Page “${path}” was not found or is not available for your role.`);
    const subtitle=platform?'platform':G.data.tenants.find(t=>t.id===G.state.tenantId)?.name||'Acme Studio';
    const routes = groups.flatMap(group => group.items), current = routes.find(([route])=>route===path);
    const label = current?.[1] || (path==='/account'?'Account':path==='/states'?'State sheet':'Page not found');
    const tenant = G.data.tenants.find(t=>t.id===G.state.tenantId), plan = G.data.plans.find(p=>p.id===tenant?.planId);
    const initials = (G.state.displayName || 'Demo').split(/[\s@.]+/).slice(0,2).map(part=>part[0]).join('').toUpperCase();
    root.innerHTML = `<div class="app-shell${G.state.rail?' rail-mode':''}">
      <aside class="sidebar" id="primary-navigation" aria-label="Primary navigation">
        <div class="sidebar-brand">${G.logo()}</div>
        <div class="sidebar-context"><span class="workspace-avatar">${platform?'G':'A'}</span><div><strong>${C.e(subtitle)}</strong><span>${platform?'Installation console':C.e(plan?.name||'Default')+' workspace'}</span></div></div>
        <nav>${nav(groups,path)}</nav>
        <footer class="sidebar-footer"><div class="demo-note"><span class="demo-dot"></span>Local prototype</div><div class="stack"><a href="#/states" class="muted">State sheet</a>${C.button('Reset demo data',()=>C.confirm('Reset all demo data? Changes made in this session will be discarded.',()=>{G.resetData();G.render();C.toast('Demo data reset.');},'Reset demo data'))}</div></footer>
      </aside>
      <button type="button" class="nav-scrim" aria-label="Close navigation" data-action="${G.action(()=>setMobileNavigation(false))}"></button>
      <div class="workspace">
        <header class="topbar">
          <span class="mobile-menu"><button type="button" class="btn btn-ghost" aria-label="Toggle navigation" aria-controls="primary-navigation" aria-expanded="false" data-action="${G.action(()=>setMobileNavigation(!document.querySelector('.app-shell').classList.contains('drawer-open')))}">${G.icon('menu')}</button></span>
          <button type="button" class="btn btn-ghost rail-expand" aria-label="${G.state.rail?'Expand':'Collapse'} sidebar" data-action="${G.action(()=>{G.state.rail=!G.state.rail;G.render();})}">${G.icon('collapse')}</button>
          <nav class="breadcrumbs" aria-label="Breadcrumb"><span>${platform?'Platform':'Workspace'}</span><span aria-hidden="true">/</span><strong>${C.e(label)}</strong></nav>
          <div class="topbar-controls">
            <button type="button" class="shell-search" data-action="${G.action(G.openCommand)}" aria-label="Open command palette">${G.icon('search')}<span>Search anything…</span><kbd>${commandShortcut}</kbd></button>
            <span class="shell-environment">Demo</span>
            <details class="avatar-menu"><summary class="avatar-button" aria-label="Account and appearance menu" title="${C.e(G.state.displayName)}">${C.e(initials)}</summary>
              <div class="avatar-popover"><strong>${C.e(G.state.displayName)}</strong><span class="hint">${C.e(G.state.role)}</span>
                ${C.field('role','Demo role','select',G.state.role,{options:[{value:'platform_admin',label:'Platform admin'},{value:'owner',label:'Owner'},{value:'admin',label:'Admin'},{value:'viewer',label:'Viewer'}],onChange:role=>G.signIn(role)})}${G.themeField()}
                ${!platform?C.button('Account',()=>G.navigate('/account')):''}${C.button('Sign out',G.signOut)}
              </div>
            </details>
          </div>
        </header>
        <main id="content" tabindex="-1">${body}</main>
      </div>
    </div>`;
    syncNavigation();
    if(view&&permitted(path)) G.mounts[path]?.();
  };
  document.addEventListener('keydown',event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase()==='k' && G.state.role) {
      event.preventDefault();
      const palette = document.querySelector('.command-palette[open]');
      if (palette) palette.close(); else G.openCommand();
      return;
    }
    const avatar = document.querySelector('.avatar-menu[open]');
    if (event.key==='Escape' && avatar) { avatar.open=false; avatar.querySelector('summary').focus(); }
    const mobileOpen = matchMedia('(max-width:700px)').matches && document.querySelector('.drawer-open');
    if (!mobileOpen || document.querySelector('dialog[open]')) return;
    if (event.key==='Escape') { event.preventDefault(); setMobileNavigation(false); }
    if (event.key==='Tab') {
      const controls = [...document.querySelectorAll('.sidebar a,.sidebar button')].filter(node=>node.getClientRects().length);
      const first=controls[0], last=controls[controls.length-1];
      if (event.shiftKey && (document.activeElement===first || !document.activeElement.closest('.sidebar'))) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && (document.activeElement===last || !document.activeElement.closest('.sidebar'))) { event.preventDefault(); first?.focus(); }
    }
  });
  document.addEventListener('click',event => {
    const avatar=document.querySelector('.avatar-menu[open]');
    if (avatar && !avatar.contains(event.target)) avatar.open=false;
    if (mobileQuery.matches && event.target.closest('.sidebar .nav-link')) setMobileNavigation(false);
  });
  mobileQuery.addEventListener('change',syncNavigation);
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change',()=>{if(G.theme==='system')G.applyTheme('system');});
  window.addEventListener('hashchange',G.render);
  G.applyTheme(G.theme);
  G.render();
})(window.GW);
