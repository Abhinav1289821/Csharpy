// Data health: every audited check, grouped by area, as ruled lines. Automatic repairs go through the same write path as
// bills (one transaction, the check re-run inside it, everything rolled back unless it finds nothing).
// Keyboard: ↑/↓ move, Enter opens the line, R repairs it, C checks again.
import { get, post, opId } from './api.js';
import { el, toast, confirmBox } from './util.js';
import { firmId, state } from './state.js';

const AREAS = {
  ledger: 'Ledger and settlements', config: 'Charges, tax heads and account mappings', parties: 'Customers and suppliers',
  transactions: 'Bills, orders and stock', crossfirm: 'Between the two firms', gst: 'GST returns', audit: 'HQ change history',
  structure: 'Database structure', security: 'Passwords and access', other: 'Other',
};
const FIX = { auto: 'MK can repair', owner: 'Needs your decision', report: 'For information' };
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
  // A failed re-check (service stopped) says why instead of leaving "Checking HQ…" with no word.
  const recheck = () => healthView(root).catch((e) => {
    toast(e.message, true);
    if (root.isConnected) root.replaceChildren(el('h1', {}, 'Data health'), el('p', { class: 'issue err' }, e.message));
  });

  const open = new Set();
  // Automatic repairs confirm the count; decision repairs put the question first and need an explicit yes.
  const count = Object.fromEntries(report.findings.map((x) => [x.check.id, x]));
  const waitsFor = (f) => (f.check.after ?? []).map((id) => count[id]).find((x) => x && (x.count > 0 || x.error));
  const repairable = (f) => f.count > 0 && !waitsFor(f) && (f.check.fix === 'auto' || (f.check.fix === 'owner' && f.check.fixSql));
  const repair = async (f) => {
    try {
      const p = await post(`/${firmId()}/health/${encodeURIComponent(f.check.id)}/preview`);
      if (!p.findings) { toast('Nothing here can be repaired yet: it waits for the answer to the question'); return; }
      const decide = f.check.fix === 'owner';
      // Some decisions are taken one line at a time (which line), and some need a value from the owner (the choice);
      // nothing is preselected, so a repair is never made with a value nobody picked.
      const keyed = p.items.length > 0;
      // A typed value (e.g. a GSTIN from the party's bill) is offered the suggestion but starts empty: he types or picks it.
      const free = f.check.choice?.free;
      const optionsOf = (i) => (keyed ? p.items[i].options ?? p.options : p.options);
      const optionList = (opts) => free ? opts.map((o) => el('option', { value: o.value }, o.label))
        : [el('option', { value: '' }, '— choose —'), ...opts.map((o) => el('option', { value: o.value }, o.label))];
      const suggestions = el('datalist', { id: `choices-${f.check.id}` }, optionList(optionsOf(0)));
      const choice = !p.choiceLabel ? null : free
        ? el('input', { list: suggestions.id, autocomplete: 'off', spellcheck: 'false', placeholder: optionsOf(0)[0] ? `Suggested: ${optionsOf(0)[0].value}` : 'Type it from the party’s bill' })
        : el('select', {}, optionList(optionsOf(0)));
      const which = keyed ? el('select', { onchange: () => {
        const opts = optionsOf(which.selectedIndex);
        if (free) { suggestions.replaceChildren(...optionList(opts)); choice.value = ''; choice.placeholder = opts[0] ? `Suggested: ${opts[0].value}` : 'Type it from the party’s bill'; }
        else choice?.replaceChildren(...optionList(opts));
      } }, p.items.map((i) => el('option', { value: i.key }, i.text))) : null;
      const ok = await confirmBox(decide ? `Your decision: ${f.check.title}` : `Repair: ${f.check.title}`, [
        el('p', {}, f.check.say),
        decide ? el('p', { class: 'issue warn' }, p.what) : el('p', {}, `${n(p.findings)} found. ${p.what}`),
        keyed ? el('label', { class: 'field' }, el('span', {}, `Which one (${n(p.findings)} found; one at a time)`), which)
          : decide ? el('p', {}, `${n(p.findings)} found. Choose “Yes, repair” only if that is what you want; otherwise close this and leave it.`) : null,
        choice ? el('label', { class: 'field' }, el('span', {}, p.choiceLabel), choice, free ? suggestions : null) : null,
        el('p', { class: 'muted' }, 'MK makes the repair in one step and checks it straight after; if anything is left over, nothing is changed.'),
        f.check.reveals?.length ? el('p', { class: 'muted' }, 'Afterwards MK may list two accounts as the same party: the correction can show a duplicate that was hidden.') : null,
        !keyed && p.examples.length ? el('ol', { class: 'muted' }, p.examples.slice(0, 8).map((x) => el('li', {}, x))) : null], decide ? 'Yes, repair' : `Repair ${n(p.findings)}`, decide);
      if (!ok) return;
      if (choice && !choice.value) { toast(`Nothing changed: choose ${p.choiceLabel.toLowerCase()} first`, true); return; }
      const r = await post(`/${firmId()}/health/${encodeURIComponent(f.check.id)}/repair`,
        { operationId: opId(), expectedHash: p.hash, decided: decide, key: which?.value ?? null, choice: choice?.value || null });
      toast(`${r.message}: ${f.check.title}`);
      recheck();
    } catch (e) { toast(e.message, true); }
  };

  const line = (f) => {
    // Plain words first (what is wrong; the question, or what the repair changes); the audit's technical notes on request.
    const tech = el('div', { hidden: true }, el('p', { class: 'muted' }, f.check.why), f.check.advice ? el('p', { class: 'muted' }, f.check.advice) : null);
    const detail = el('tr', { hidden: !open.has(f.check.id) }, el('td', { colspan: 4 },
      el('p', {}, f.check.say),
      f.check.fix === 'owner' && f.check.ask ? el('p', { class: 'issue warn' }, f.check.ask) : null,
      repairable(f) && f.check.will ? el('p', {}, `The repair: ${f.check.will}`) : null,
      waitsFor(f) ? el('p', { class: 'muted' }, `Do this first: ${waitsFor(f).check.title}.`) : null,
      f.error ? el('p', { class: 'issue err' }, `The check could not run: ${f.error}`) : null,
      f.examples.length ? el('ol', { class: 'muted', style: { fontFamily: 'var(--mono)', fontSize: '13px' } }, f.examples.map((x) => el('li', {}, x))) : null,
      f.count > f.examples.length ? el('p', { class: 'muted' }, `… and ${n(f.count - f.examples.length)} more`) : null,
      el('a', { href: '#', class: 'muted', onclick: (e) => { e.preventDefault(); e.stopPropagation(); tech.hidden = !tech.hidden; } }, 'Technical notes (for the accountant)'), tech));
    const toggle = () => { if (open.has(f.check.id)) open.delete(f.check.id); else open.add(f.check.id); detail.hidden = !open.has(f.check.id); };
    const tr = el('tr', { class: 'click', tabindex: '-1', onclick: toggle },
      el('td', {}, f.check.title, f.check.severity === 'error' ? el('small', { class: 'muted' }, ' · affects money or GST') : null),
      el('td', { class: 'n' }, f.error ? '—' : n(f.count)),
      el('td', {}, waitsFor(f) ? `After: ${waitsFor(f).check.title}` : FIX[f.check.fix] ?? f.check.fix),
      el('td', { class: 'n' }, repairable(f) ? el('button', { class: 'btn small' + (f.check.fix === 'auto' ? ' primary' : ''), onclick: (e) => { e.stopPropagation(); repair(f); } },
        f.check.fix === 'auto' ? 'Repair' : 'Decide') : null));
    rows.push({ tr, f, toggle });
    return [tr, detail];
  };

  const byArea = {};
  for (const f of report.findings) (byArea[f.check.area] ??= []).push(f);
  const order = Object.keys(AREAS);
  const rank = (a) => (order.indexOf(a) < 0 ? order.length : order.indexOf(a));
  const sections = Object.entries(byArea).sort(([a], [b]) => rank(a) - rank(b)).map(([area, list]) => {
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
      el('button', { class: 'btn', onclick: () => recheck() }, 'Check again (C)')),
    contract.findings.filter((f) => f.severity !== 'ok' && f.title === 'HQ procedure contract').map((f) => el('div', { class: 'issue err' }, `${f.title}: ${f.detail} `,
      el('button', { class: 'btn small danger', onclick: async () => {
        try {
          if (!await confirmBox('Accept the new HQ procedures?', [el('p', {}, 'Only do this after the parity tests pass against the updated HQ.')], 'Accept', true)) return;
          await post(`/${firmId()}/contract/accept`);
          toast('Updated HQ procedures accepted');
          recheck();
        } catch (e) { toast(e.message, true); }
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
  // This screen moves through its findings itself; the shell's generic list keys (app.js rowKeys) stay out, or Enter
  // would open a row and toggle it shut again, and the first ↓ would skip a row.
  root.dataset.ownKeys = '1';
  const keys = (e) => {
    if (!marker.isConnected) return document.removeEventListener('keydown', keys);
    // Ctrl+C (copying an example) must not re-run every check, nor Ctrl+R open a repair instead of reloading.
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (e.target.matches('input, textarea, select') || document.querySelector('#modal-root .modal')) return;
    const k = e.key.toLowerCase();
    if (e.key === 'ArrowDown') { e.preventDefault(); selected = Math.min(rows.length - 1, selected + 1); focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); selected = Math.max(0, selected - 1); focus(); }
    else if (e.key === 'Enter' && rows[selected]) { e.preventDefault(); rows[selected].toggle(); }
    else if (k === 'r' && rows[selected] && repairable(rows[selected].f)) { e.preventDefault(); repair(rows[selected].f); }
    else if (k === 'c') { e.preventDefault(); recheck(); }
  };
  document.addEventListener('keydown', keys);
}
