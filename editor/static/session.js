"use strict";
fetch('/auth/session').then(r => r.ok ? r.json() : null).then(s => {
  if (!s || !s.username) return;
  const b = document.getElementById('gw-signout');
  b.hidden = false; b.title = 'Signed in as ' + s.username;
  b.onclick = async () => {
    const r = await fetch('/auth/logout', {method:'POST', redirect:'manual', headers:{'X-CSRF-Token':GW.CSRF}});
    if (r.type === 'opaqueredirect' || r.ok) location.replace('/login');
  };
}).catch(() => {});
