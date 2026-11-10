/* Native family metadata is supplied by inventory/meta; values always use gear grade. */
(function () {
  'use strict';
  const GW=window.GW;
  GW.attributeFamilies={
    amount(a,grade) {
      const values=a && (a.values || a.levels);
      return values && values.length ? Number(values[Math.min(values.length,Math.max(1,Number(grade)||1))-1]) : null;
    },
    format(a,grade) {
      const v=this.amount(a,grade);
      return v===null?'?':a.percent || a.flag ? '+'+(v*100).toFixed(2).replace(/\.?0+$/,'')+'%' : '+'+GW.fmt(v);
    },
    canRoll(a,allowed) {
      return !!a && (allowed.has(Number(a.id)) || ((a.family_policy || {}).chain || []).some(id=>allowed.has(Number(id))));
    },
    choices(attrs,allowed,only) {
      const groups=new Map();
      for(const a of Object.values(attrs)) {
        if(a.dedicated || (only && !this.canRoll(a,allowed))) continue;
        const f=a.family_policy || {family:'unknown:'+a.id,chain:[Number(a.id)],group:'unknown'};
        const prev=groups.get(f.family);
        if(!prev || f.chain.indexOf(Number(a.id)) > f.chain.indexOf(Number(prev.id))) groups.set(f.family,a);
      }
      return [...groups.values()].sort((a,b)=>Number(a.id)-Number(b.id));
    },
    levelFor(a,originalId,originalLevel) {
      if(Number(a.id)===Number(originalId) && originalId!==null && originalId!==undefined) return originalLevel;
      return a.family_policy && a.family_policy.rank || null;
    }
  };
})();
