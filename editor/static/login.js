'use strict';
for (const form of document.querySelectorAll('form')) form.addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.currentTarget, button = form.querySelector('button'), message = form.querySelector('[role=status]');
  button.disabled = true; message.textContent = 'Signing in…';
  try {
    const r = await fetch(form.id === 'recovery' ? '/auth/recovery' : '/auth/login', {method:'POST', redirect:'manual', headers:{'Content-Type':'application/json'}, body:JSON.stringify({username:form.username.value,password:form.password.value})});
    form.password.value = '';
    if (r.type !== 'opaqueredirect' && !r.ok) { const d = await r.json(); throw new Error(d.error || 'Sign-in failed.'); }
    const next = new URLSearchParams(location.search).get('next');
    let destination = new URL('/', location.origin);
    if (next) {
      try { const parsed = new URL(next, location.origin); if (parsed.origin === location.origin) destination = parsed; }
      catch (_) {}
    }
    location.replace(destination.href);
  } catch (e) { message.textContent = e.message; button.disabled = false; }
});
