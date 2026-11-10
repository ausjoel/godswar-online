'use strict';
(async () => {
  try {
    const r = await fetch('/static/build-analysis.json', {cache:'no-store'});
    if (!r.ok) throw Error('Could not load measured results. Sign in to the editor if your session expired.');
    const d = await r.json(), pct = bp => (bp / 100).toFixed(2) + '%';
    const row = (target, values) => {
      const tr = document.createElement('tr');
      for (const v of values) { const td = document.createElement('td'); td.textContent = String(v); tr.append(td); }
      target.append(tr);
    };
    document.getElementById('inputs').textContent = 'Measured at level ' + d.Level + ', attacker hit ' + d.AttackerHit + ', attacker critical ' + d.AttackerCritical + ', base target critical resistance ' + d.BaseTargetCriticalResistance + '. Generated ' + new Date(d.GeneratedAt).toLocaleString() + '.';
    const dodge = document.getElementById('dodge');
    for (const x of d.DodgeScenarios) {
      const reduction = 100 * (1 - x.Gaia90SecondAverageIncoming / x.ExpectedIncomingNormalHitUnits);
      row(dodge, [x.Dodge, pct(x.PvpHitBasisPoints), pct(x.PveHitBasisPoints), pct(x.GaiaPvpHitBasisPoints), reduction.toFixed(2) + '%']);
    }
    const select = document.getElementById('healing-filter'), healing = document.getElementById('healing');
    function renderHealing() {
      healing.replaceChildren();
      for (const x of d.LifeAbsorptionScenarios) {
        const flat = x.FlatHealingPerSuccessfulHit > 0, percent = x.PercentageHealingBasisPoints > 0;
        if (select.value === 'flat' && (!flat || percent) || select.value === 'percent' && (flat || !percent) || select.value === 'both' && (!flat || !percent)) continue;
        row(healing, [x.Targets, x.FlatHealingPerSuccessfulHit, pct(x.PercentageHealingBasisPoints), x.RequestedHealingPerPulse.toLocaleString()]);
      }
    }
    select.addEventListener('change', renderHealing); renderHealing();
  } catch (e) { document.getElementById('error').textContent = e.message; }
})();
