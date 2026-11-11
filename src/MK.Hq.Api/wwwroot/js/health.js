// Data health: every audited check, grouped by area, as ruled lines. Automatic repairs go through the same write path as
// bills (one transaction, the check re-run inside it, everything rolled back unless it finds nothing).
// Keyboard: ↑/↓ move, Enter opens the line, R repairs it, C checks again.
import { get, post, opId } from './api.js';
import { el, toast, confirmBox } from './util.js';
import { firmId, state } from './state.js';

const AREAS = {
  ledger: 'Ledger and settlements', config: 'Charges, tax heads and account mappings', parties: 'Customers and suppliers',
  transactions: 'Bills, orders and stock', crossfirm: 'Between the two firms', gst: 'GST returns', audit: 'HQ change history',
  structure: 'Database structure', other: 'Other',
};
const FIX = { auto: 'Repaired automatically', owner: 'Needs your decision', report: 'For information' };
const n = (v) => Number(v).toLocaleString('en-IN');

export async function healthView(root) {
  root.replaceChildren(el('div', { class: 'page-head' }, el('div', {}, el('h1', {}, 'Data health'), el('p', { class: 'muted' }, 'Checking HQ…'))));
  const [contract, report, log, ops] = await Promise.all([
    get(`/${firmId()}/integrity`), get(`/${firmId()}/health/checks`), get(`/${firmId()}/health/log`), get(`/operations?firm=${firmId()}&take=30`),
  ]);
  const findings = report.findings.filter((f) => f.count > 0 || f.error);
  const repairs = findings.filter((f) => f.check.fix === 'auto' && f.count > 0);
  const decisions = findings.filter((f) => f.check.fix === 'owner' && f.count > 0);
  const rows = [];
  let selected = 0;

  const open = new Set();
  // Automatic repairs confirm the count; decision repairs put the question first and need an explicit yes.
  const repairable = (f) => f.count > 0 && (f.check.fix === 'auto' || (f.check.fix === 'owner' && f.check.fixSql));
  const repair = async (f) => {
    try {
      const p = await post(`/${firmId()}/health/${encodeURIComponent(f.check.id)}/preview`);
      if (!p.findings) { toast('Nothing left to repair here'); return healthView(root); }
      const decide = f.check.fix === 'owner';
      const ok = await confirmBox(decide ? `Your decision: ${f.check.title}` : `Repair: ${f.check.title}`, [
        decide ? el('p', { class: 'issue warn' }, p.what) : el('p', {}, `${n(p.findings)} found. ${p.what}`),
        decide ? el('p', {}, `${n(p.findings)} found. Choose “Yes, repair” only if that is what you want; otherwise close this and leave it.`) : null,
        el('p', { class: 'muted' }, 'MK makes the repair in one step and checks it straight after; if anything is left over, nothing is changed.'),
        p.examples.length ? el('ol', { class: 'muted' }, p.examples.slice(0, 8).map((x) => el('li', {}, x))) : null], decide ? 'Yes, repair' : `Repair ${n(p.findings)}`, decide);
      if (!ok) return;
      const r = await post(`/${firmId()}/health/${encodeURIComponent(f.check.id)}/repair`, { operationId: opId(), expectedHash: p.hash, decided: decide });
      toast(`${r.message}: ${f.check.title}`);
      healthView(root);
    } catch (e) { toast(e.message, true); }
  };

  const line = (f) => {
    const detail = el('tr', { hidden: !open.has(f.check.id) }, el('td', { colspan: 4 },
      el('p', {}, f.check.why), f.check.advice ? el('p', { class: f.check.fix === 'owner' ? 'issue warn' : 'muted' }, f.check.advice) : null,
      f.error ? el('p', { class: 'issue err' }, `The check could not run: ${f.error}`) : null,
      f.examples.length ? el('ol', { class: 'muted', style: { fontFamily: 'var(--mono)', fontSize: '13px' } }, f.examples.map((x) => el('li', {}, x))) : null,
      f.count > f.examples.length ? el('p', { class: 'muted' }, `… and ${n(f.count - f.examples.length)} more`) : null));
    const toggle = () => { if (open.has(f.check.id)) open.delete(f.check.id); else open.add(f.check.id); detail.hidden = !open.has(f.check.id); };
    const tr = el('tr', { class: 'click', tabindex: '-1', onclick: toggle },
      el('td', {}, f.check.title, f.check.severity === 'error' ? el('small', { class: 'muted' }, ' · affects money or GST') : null),
      el('td', { class: 'n' }, f.error ? '—' : n(f.count)),
      el('td', {}, FIX[f.check.fix] ?? f.check.fix),
      el('td', { class: 'n' }, repairable(f) ? el('button', { class: 'btn small' + (f.check.fix === 'auto' ? ' primary' : ''), onclick: (e) => { e.stopPropagation(); repair(f); } },
        f.check.fix === 'auto' ? 'Repair' : 'Decide') : null));
    rows.push({ tr, f, toggle });
    return [tr, detail];
  };

  const byArea = {};
  for (const f of report.findings) (byArea[f.check.area] ??= []).push(f);
  const sections = Object.entries(byArea).map(([area, list]) => {
    const bad = list.filter((f) => f.count > 0 || f.error).sort((a, b) => ({ error: 0, warning: 1, info: 2 }[a.check.severity] - { error: 0, warning: 1, info: 2 }[b.check.severity]) || b.count - a.count);
    const passed = list.length - bad.length;
    return [el('div', { class: 'section-head' }, el('h2', {}, AREAS[area] ?? area), el('span', { class: 'muted' }, bad.length ? `${bad.length} to look at · ${passed} passed` : `All ${passed} checks passed`)),
      bad.length ? el('div', { class: 'panel' }, el('table', { class: 'grid' },
        el('thead', {}, el('tr', {}, el('th', {}, 'What'), el('th', { class: 'n' }, 'Found'), el('th', {}, 'Next step'), el('th', {}))),
        el('tbody', {}, bad.flatMap(line)))) : null];
  });

  const marker = el('span', { hidden: true });
  root.replaceChildren(
    el('div', { class: 'page-head' },
      el('div', {}, el('h1', {}, 'Data health'),
        el('p', { class: 'muted' }, `${state.firm.name} · checked ${new Date(report.checkedAt).toLocaleTimeString('en-IN', { hour: 'numeric', minute: '2-digit' })} · `
          + `${n(repairs.length)} kinds of problem MK can repair · ${n(decisions.length)} need your decision`)),
      el('button', { class: 'btn', onclick: () => healthView(root) }, 'Check again (C)')),
    contract.findings.filter((f) => f.severity !== 'ok' && f.title === 'HQ procedure contract').map((f) => el('div', { class: 'issue err' }, `${f.title}: ${f.detail} `,
      el('button', { class: 'btn small danger', onclick: async () => {
        if (await confirmBox('Accept the new HQ procedures?', [el('p', {}, 'Only do this after the parity tests pass against the updated HQ.')], 'Accept', true)) { await post(`/${firmId()}/contract/accept`); healthView(root); }
      } }, 'Accept updated HQ'))),
    marker, ...sections.flat(),
    log.length ? [el('div', { class: 'section-head' }, el('h2', {}, 'Repairs made')), el('div', { class: 'panel' }, el('table', { class: 'grid' },
      el('thead', {}, el('tr', {}, el('th', {}, 'When'), el('th', {}, 'What'), el('th', { class: 'n' }, 'Repaired'))),
      el('tbody', {}, log.map((l) => el('tr', {}, el('td', {}, new Date(l.at).toLocaleString('en-IN')), el('td', {}, l.title), el('td', { class: 'n' }, n(l.before - l.after)))))))] : null,
    el('div', { class: 'section-head' }, el('h2', {}, 'Recent saves by MK')),
    el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['When', 'What', 'Result', 'Document'].map((h) => el('th', {}, h)))),
      el('tbody', {}, ops.map((o) => el('tr', { class: o.documentId ? 'click' : '', onclick: () => o.documentId && (location.hash = `#/doc/${o.documentId}`) },
        el('td', {}, new Date(o.createdAt).toLocaleString('en-IN')), el('td', {}, o.kind),
        el('td', {}, o.status === 'committed' ? 'Saved' : o.status === 'rejected' ? `Refused: ${o.errorMessage ?? ''}` : o.status), el('td', {}, o.documentId ?? '')))))),
    el('div', { class: 'keyhelp' }, el('span', {}, '↑ ↓ move'), el('span', {}, 'Enter opens'), el('span', {}, 'R repairs or decides'), el('span', {}, 'C checks again')));

  const focus = () => rows[selected]?.tr.focus();
  const keys = (e) => {
    if (!marker.isConnected) return document.removeEventListener('keydown', keys);
    if (e.target.matches('input, textarea, select') || document.querySelector('#modal-root .modal')) return;
    const k = e.key.toLowerCase();
    if (e.key === 'ArrowDown') { e.preventDefault(); selected = Math.min(rows.length - 1, selected + 1); focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); selected = Math.max(0, selected - 1); focus(); }
    else if (e.key === 'Enter' && rows[selected]) { e.preventDefault(); rows[selected].toggle(); }
    else if (k === 'r' && rows[selected] && repairable(rows[selected].f)) { e.preventDefault(); repair(rows[selected].f); }
    else if (k === 'c') { e.preventDefault(); healthView(root); }
  };
  document.addEventListener('keydown', keys);
}
