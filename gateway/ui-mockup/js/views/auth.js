(function(G) {
  'use strict';
  const C=G.C;
  const authCard = (title, subtitle, body) => `<div class="auth-theme">${G.themeField()}</div><main id="content" class="auth-page" tabindex="-1"><section class="auth-card"><div class="auth-brand">${G.logo()}<h1>${C.e(title)}</h1><p class="muted">${C.e(subtitle)}</p></div>${body}</section></main>`;
  const mismatch = form => new FormData(form).get('password') !== new FormData(form).get('repeat') ? 'Password confirmation does not match.' : '';
  G.views['/login'] = () => {
    const error=G.demo==='locked'?'Account temporarily locked. Try again later.':G.demo==='error'?'Invalid email or password.':'';
    const message=G.state.loginMessage; G.state.loginMessage=null;
    return authCard('Sign in','Sign in with your tenant or platform account.',
      (error?C.alert('error',error):'')+(message?C.alert('success',message):'')+
      C.form(C.field('email','Email','email','',{required:true})+C.field('password','Password','password','',{required:true}), 'Sign in', form=>G.signIn('owner',new FormData(form).get('email')), {loading:'Signing in…'})+
      '<p class="auth-footer">Have an invitation token? <a href="#/invite">Accept invitation</a>. Forgot your password? <a href="#/reset">Reset password</a>.</p>'+
      `<div class="demo-shortcuts"><span class="hint">Explore with a demo account</span><div class="inline">${[['platform_admin','Platform admin'],['owner','Owner'],['admin','Admin'],['viewer','Viewer']].map(([role,label])=>C.button(label,()=>G.signIn(role))).join('')}</div></div>`);
  };
  for(const path of ['/invite','/reset']) G.views[path]=()=>{
    const invite=path==='/invite', state=G.viewState(path,{done:false});
    const form=C.form(C.field('token','Token','text',G.location().query.get('token')||'',{required:true})+C.field('password','New password','password','',{required:true,hint:'At least as required by the gateway password policy.'})+C.field('repeat','Repeat password','password','',{required:true}),invite?'Activate account':'Save password',()=>{state.done=true;G.render();},{validate:mismatch,loading:'Saving…'});
    return authCard(invite?'Accept Invitation':'Reset Password',invite?'Paste the invitation token from the platform/owner to create your account password.':'Paste the password reset token from your owner to set a new password.',
      (state.done?'<div class="alert alert-success" role="status">Done. Sign in with your new password. <a href="#/login">Sign in</a></div>':form)+'<p class="auth-footer"><a href="#/login">Back to sign in</a></p>');
  };
  G.views['/account']=()=>C.page('Account','Change your account password.')+C.card('Change password', C.form(C.field('current','Current password','password','',{required:true})+C.field('password','New password','password','',{required:true})+C.field('repeat','Repeat new password','password','',{required:true}),'Change password',()=>{G.state.loginMessage='Password changed. Session ended — please sign in again.';G.signOut();},{validate:mismatch,loading:'Saving…'}));
})(window.GW);
