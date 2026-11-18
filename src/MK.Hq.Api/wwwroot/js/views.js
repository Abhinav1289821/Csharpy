// Read-side screens: dashboard, document list/view, reports, HQ health, drafts.
import { get, post, del, opId } from './api.js';
import { el, money, qty, dmy, today, addDays, toast, confirmBox, lookup, csv, debounce, newest } from './util.js';
import { state, firmId, searchParties, party, onChange } from './state.js';
import { printDocument, invoice } from './print.js';

// A print that fails (document gone, service down) says so instead of doing nothing.
const print = (id) => printDocument(id).catch((e) => toast(`Could not print: ${e.message}`, true));

// Lists refresh themselves in place when HQ (or another MK window) saves: the operator's search text, dates and the row
// he is on stay where they are. The subscription retires with the screen (its <main> is replaced on navigation).
export function live(root, refresh) {
  const off = onChange((what) => {
    if (!root.isConnected) return off();
    if (what === 'changed') keepPlace(root, refresh).catch(() => { /* the live indicator already shows HQ offline */ });
  });
  return off;
}

// Re-render without losing the place: the scroll of each list panel, and the focused row (by position) if the
// refresh took focus away. A row he moved to meanwhile, or a box he is typing in, is left alone.
export async function keepPlace(root, refresh) {
  const rows = () => [...root.querySelectorAll('tr.click')];
  const active = document.activeElement;
  const at = active && root.contains(active) && active.matches?.('tr.click') ? rows().indexOf(active) : -1;
  const scrolls = [...root.querySelectorAll('.scroll')].map((s) => s.scrollTop);
  await refresh();
  if (!root.isConnected) return;
  root.querySelectorAll('.scroll').forEach((s, i) => { if (scrolls[i] !== undefined) s.scrollTop = scrolls[i]; });
  const lost = !document.activeElement || document.activeElement === document.body || !document.activeElement.isConnected;
  if (at >= 0 && lost) {
    const list = rows();
    const tr = list[Math.min(at, list.length - 1)];
    if (tr) { tr.tabIndex = -1; tr.focus({ preventScroll: true }); }
  }
}

const KIND_TT = { all: '', sales: '506,507', estimates: '249', purchases: '500,501', orders: '248', receipts: '204,205', payments: '206,207', notes: '208,209' };
const isVoucher = (tt) => tt >= 204 && tt <= 209;
// Colour of a document type's tag: sales, purchases, orders/estimates, money in, money out, notes.
// The dashboard's recent list has no ttId, so fall back to HQ's type name there.
const typeTone = (tt, name = '') => ({ 506: 'sale', 507: 'sale', 500: 'purchase', 501: 'purchase', 249: 'order', 248: 'order', 204: 'in', 205: 'in', 206: 'out', 207: 'out' })[tt]
  ?? (/order|estimate/i.test(name) ? 'order' : /purchase/i.test(name) ? 'purchase' : /sale/i.test(name) ? 'sale' : /receipt/i.test(name) ? 'in' : /payment/i.test(name) ? 'out' : 'other');
const modeName = (m) => state.firm.paymentModes.find((x) => x.id === m)?.caption ?? m;

// ------------------------------------------------------------------ dashboard
export async function dashboardView(root) {
  const ticket = newest();
  const refresh = async () => {
    const current = ticket();
    const [d, drafts] = await Promise.all([get(`/${firmId()}/reports/dashboard?day=${today()}`), get(`/${firmId()}/drafts`).catch(() => [])]);
    if (current()) paintDashboard(root, d, drafts);
  };
  await refresh();
  live(root, refresh);
}

function paintDashboard(root, d, drafts) {
  const card = (label, value, sub, tone = '') => el('div', { class: 'card ' + tone }, el('div', { class: 'label' }, label), el('div', { class: 'value' }, value), sub ? el('div', { class: 'sub' }, sub) : null);
  const action = (href, key, label, hint, primary = false) => el('a', { class: 'action' + (primary ? ' primary' : ''), href },
    el('span', { class: 'action-key' }, key), el('strong', {}, label), el('small', {}, hint));
  root.replaceChildren(
    el('div', { class: 'page-head' }, el('div', {}, el('h1', {}, 'Today'), el('p', { class: 'muted' }, `${dmy(d.day)} · figures live from HQ`)),
      drafts.length ? el('a', { class: 'drafts-waiting', href: '#/drafts' }, `${drafts.length} unsaved ${drafts.length === 1 ? 'bill' : 'bills'} in Drafts →`) : null),
    el('div', { class: 'actions' },
      action('#/new/sale', 'F2', 'New sale', 'Cash, credit, card or UPI', true),
      action('#/new/salesOrder', 'F3', 'Estimate', 'Quotation / sales order'),
      action('#/new/purchase', 'F4', 'Purchase', 'Supplier bill'),
      action('#/voucher/cashReceipt', 'F5', 'Receipt', 'Money received'),
      action('#/capture', 'F11', 'Scan bill photo', 'Read a bill with AI')),
    el('div', { class: 'cards' },
      card('Sales today', '₹ ' + money(d.salesToday), `${d.billsToday} bills · cash ₹ ${money(d.cashSalesToday)}`, 'tone-accent'),
      card('Receipts today', '₹ ' + money(d.receiptsToday), `Payments ₹ ${money(d.paymentsToday)}`, 'tone-ok'),
      card('Cash in hand', '₹ ' + money(d.cashInHand), null, 'tone-ink'),
      card('To receive', '₹ ' + money(d.receivable), 'from customers', 'tone-ok'),
      card('To pay', '₹ ' + money(d.payable), 'to suppliers', 'tone-danger'),
      card('Sales this month', '₹ ' + money(d.salesMonth), `Year ₹ ${money(d.salesYear)}`, 'tone-accent')),
    el('div', { class: 'section-head' }, el('h2', {}, 'Recent bills & vouchers'), el('a', { href: '#/documents', class: 'link' }, 'See all (F7) →')),
    docTable(d.recent.map((r) => ({ ...r, reference: r.reference, party: r.party, amount: r.amount, typeName: r.typeName }))));
}

function docTable(rows) {
  return el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
    el('thead', {}, el('tr', {}, ['Date', 'Type', 'No.', 'Party', 'Mode', 'Amount', 'Open'].map((h, i) => el('th', { class: i >= 5 ? 'n' : '' }, h)))),
    el('tbody', {}, rows.map((r) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${r.documentId}`) },
      el('td', {}, dmy(r.date)), el('td', {}, el('span', { class: 'tag ' + typeTone(r.ttId, r.typeName) }, r.typeName)), el('td', { class: 'ref' }, r.reference), el('td', {}, r.party, r.town ? el('small', { class: 'muted' }, ' · ' + r.town) : null),
      el('td', {}, r.mode === undefined ? '' : isVoucher(r.ttId) ? '' : modeName(r.mode)), el('td', { class: 'n' }, money(r.amount)),
      el('td', { class: 'n' }, r.open ? money(r.open) : ''))))));
}

// ------------------------------------------------------------------ documents
export async function documentsView(root) {
  const f = { kind: 'all', from: addDays(today(), -30), to: today(), q: '' };
  const out = el('div');
  // Typing "ramu" sends r, ra, ram, ramu (debounced, but a slow query still overlaps the next): only the newest
  // answer may land, or an older, slower one leaves the list showing bills for "ra" under the box saying "ramu".
  const ticket = newest();
  const load = async (quiet = false) => {
    const current = ticket();
    const qs = new URLSearchParams({ take: '300', from: f.from, to: f.to });
    if (KIND_TT[f.kind]) qs.set('tt', KIND_TT[f.kind]);
    if (f.q) qs.set('q', f.q);
    // A live refresh keeps the list on screen (and the row he is on) until the new one arrives.
    if (!quiet) out.replaceChildren(el('p', { class: 'muted' }, 'Loading…'));
    const rows = await get(`/${firmId()}/documents?${qs}`);
    if (!current()) return;
    out.replaceChildren(el('p', { class: 'muted' }, `${rows.length} documents · total ₹ ${money(rows.reduce((s, r) => s + r.amount, 0))}`), docTable(rows));
  };
  const reload = debounce(() => load().catch((e) => toast(e.message, true)), 250);
  const input = (k, attrs) => { const x = el('input', { value: f[k], ...attrs }); x.addEventListener('input', () => { f[k] = x.value; reload(); }); return x; };
  root.replaceChildren(
    el('h1', {}, 'Bills & vouchers'),
    el('div', { class: 'panel row' },
      el('select', { onchange: (e) => { f.kind = e.target.value; reload(); } }, Object.keys(KIND_TT).map((k) => el('option', { value: k }, k[0].toUpperCase() + k.slice(1)))),
      el('label', { class: 'field' }, el('span', {}, 'From'), input('from', { type: 'date' })), el('label', { class: 'field' }, el('span', {}, 'To'), input('to', { type: 'date' })),
      el('label', { class: 'field grow' }, el('span', {}, 'Search party / number / supplier bill'), input('q', { placeholder: 'e.g. CS/879 or ramu  ·  Enter opens the top bill', 'data-rows': '', autofocus: true }))),
    out);
  await load();
  root.querySelector('[data-rows]')?.focus();
  live(root, () => load(true));
}

export async function documentView(root, id) {
  const d = await get(`/${firmId()}/documents/${id}`);
  const s = d.summary;
  const voucher = isVoucher(s.ttId);
  const editable = [506, 507, 500, 501, 249, 248].includes(s.ttId);
  const kindOf = { 506: 'sale', 507: 'sale', 500: 'purchase', 501: 'purchase', 249: 'salesOrder', 248: 'purchaseOrder' }[s.ttId];
  const remove = async () => {
    try {
      const p = await post(`/${firmId()}/${voucher ? 'vouchers' : 'documents'}/${id}/delete/preview`);
      const ok = await confirmBox(`Delete ${s.reference}?`, [el('p', {}, 'HQ will mark it deleted in its change log, exactly as HQ does.'), ...p.warnings.map((w) => el('div', { class: 'issue warn' }, w))], 'Delete', true);
      if (!ok) return;
      await post(`/${firmId()}/${voucher ? 'vouchers' : 'documents'}/${id}/delete`, { operationId: opId(), documentId: id, expectedHash: p.hash });
      toast(`${s.reference} deleted`);
      location.hash = '#/documents';
    } catch (e) { toast(e.message, true); }
  };
  root.replaceChildren(
    el('div', { class: 'row spread' },
      el('div', {}, el('h1', {}, `${s.typeName} ${s.reference}`), el('div', { class: 'muted' }, `${dmy(s.date)} · ${d.party.name} · ${voucher ? '' : d.paymentCaption + ' · '}entered on ${s.enteredOn || 'HQ'}${s.modifyCnt > 2 ? ` · edited ${s.modifyCnt - 2}×` : ''}`)),
      el('div', { class: 'row' },
        !voucher ? el('button', { class: 'btn primary', onclick: () => print(id) }, 'Print (P)') : null,
        s.ttId === 249 ? el('a', { class: 'btn', href: `#/from-order/${id}` }, 'Make sale bill (S)') : null,
        editable ? el('a', { class: 'btn', href: `#/edit/${kindOf}/${id}` }, 'Edit (E)') : null,
        voucher ? el('a', { class: 'btn', href: `#/edit-voucher/${s.ttId}/${id}` }, 'Correct (E)') : null,
        el('button', { class: 'btn danger btn-apart', onclick: remove }, 'Delete'))),
    voucher ? null : el('div', { class: 'panel paper', style: { marginTop: '12px', maxWidth: '900px' } }, invoice(d)),
    el('h2', {}, 'HQ ledger'),
    el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, el('th', {}, 'Account'), el('th', {}, 'Type'), el('th', { class: 'n' }, 'Amount'))),
      el('tbody', {}, d.ledger.map((r) => el('tr', {}, el('td', {}, r.accountName), el('td', {}, { D: 'Debit', C: 'Credit', P: 'Cash paid', R: 'Cash received' }[r.type] ?? r.type), el('td', { class: 'n' }, money(r.amount))))))),
    d.settlements.length ? [el('h2', {}, 'Settled with'), el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('tbody', {},
      d.settlements.map((x) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${x.documentId}`) }, el('td', {}, x.reference), el('td', {}, dmy(x.date)), el('td', { class: 'n' }, money(x.amount)))))))] : null);
  // Letter keys for the common next steps (a customer asking for a copy is P).
  const alive = el('span', { hidden: true });
  root.append(alive);
  const keys = (e) => {
    if (!alive.isConnected) return document.removeEventListener('keydown', keys);
    if (e.ctrlKey || e.altKey || document.querySelector('.modal-back') || /INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) return;
    const k = e.key.toLowerCase();
    if (k === 'p' && !voucher) { e.preventDefault(); print(id); }
    else if (k === 'e' && (editable || voucher)) { e.preventDefault(); location.hash = editable ? `#/edit/${kindOf}/${id}` : `#/edit-voucher/${s.ttId}/${id}`; }
    else if (k === 's' && s.ttId === 249) { e.preventDefault(); location.hash = `#/from-order/${id}`; }
  };
  document.addEventListener('keydown', keys);
}

// ------------------------------------------------------------------ ledger
export async function ledgerView(root, accountId) {
  const f = { from: state.firm.periodStart, to: today() };
  const out = el('div');
  const pick = lookup({ placeholder: 'Search any account…', value: accountId ? party(accountId)?.name ?? '' : '', autofocus: !accountId,
    search: (t) => searchParties(t).map((p) => ({ label: p.name, hint: `${p.type} · ₹${money(p.balance)}`, value: p })),
    onPick: (p) => { location.hash = `#/ledger/${p.accountId}`; } });
  const ticket = newest(); // changing From then To quickly: the answer for the newer dates wins
  const load = async () => {
    if (!accountId) return;
    const current = ticket();
    const r = await get(`/${firmId()}/reports/ledger/${accountId}?from=${f.from}&to=${f.to}`);
    if (!current()) return;
    // The balance is the question at the counter ("how much do I owe?"), so it leads.
    out.replaceChildren(el('div', { class: 'ledger-head' },
      el('div', {}, el('h2', {}, r.accountName), el('div', { class: 'muted' }, `${r.accountType} · ${dmy(r.from)} to ${dmy(r.to)}`)),
      el('div', { class: 'ledger-balance' }, el('span', {}, r.closing >= 0 ? 'Balance due (Dr)' : 'Balance in credit (Cr)'), el('strong', {}, '₹ ' + money(Math.abs(r.closing))))),
      el('div', { class: 'row spread', style: { margin: '6px 0 10px' } }, el('span', { class: 'muted' }, `${r.lines.length} entries · ↓ to move, Enter opens`),
      el('button', { class: 'btn small', onclick: () => csv(r.lines, [{ title: 'Date', get: 'date' }, { title: 'No', get: 'reference' }, { title: 'Type', get: 'typeName' }, { title: 'Narration', get: 'narration' }, { title: 'Debit', get: 'debit' }, { title: 'Credit', get: 'credit' }, { title: 'Balance', get: 'balance' }], `ledger-${r.accountName}.csv`) }, 'Export CSV')),
      el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
        el('thead', {}, el('tr', {}, ['Date', 'No.', 'Type', 'Particulars', 'Debit', 'Credit', 'Balance'].map((h, i) => el('th', { class: i >= 4 ? 'n' : '' }, h)))),
        el('tbody', {}, el('tr', {}, el('td', {}, dmy(r.from)), el('td', { colspan: 5 }, 'Opening balance'), el('td', { class: 'n' }, bal(r.opening))),
          r.lines.map((l) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${l.documentId}`) }, el('td', {}, dmy(l.date)), el('td', {}, l.reference), el('td', {}, l.typeName),
            el('td', {}, l.contra || l.narration, l.contra && l.narration ? el('small', { class: 'muted' }, ' · ' + l.narration) : null),
            el('td', { class: 'n' }, l.debit ? money(l.debit) : ''), el('td', { class: 'n' }, l.credit ? money(l.credit) : ''), el('td', { class: 'n' }, bal(l.balance))))),
        el('tfoot', {}, el('tr', {}, el('td', { colspan: 4 }, 'Closing'), el('td', { class: 'n' }, money(r.debit)), el('td', { class: 'n' }, money(r.credit)), el('td', { class: 'n' }, bal(r.closing)))))));
  };
  const date = (k) => { const x = el('input', { type: 'date', value: f[k] }); x.addEventListener('change', () => { f[k] = x.value; load().catch((e) => toast(e.message, true)); }); return x; };
  root.replaceChildren(el('h1', {}, 'Account ledger'), el('div', { class: 'panel row' }, el('div', { class: 'grow' }, pick.wrap),
    el('label', { class: 'field' }, el('span', {}, 'From'), date('from')), el('label', { class: 'field' }, el('span', {}, 'To'), date('to'))), out);
  await load();
  if (accountId) live(root, load);
}
const bal = (v) => money(Math.abs(v)) + (v >= 0 ? ' Dr' : ' Cr');

// ------------------------------------------------------------------ outstanding
export async function outstandingView(root, side = 'receivable') {
  const ticket = newest();
  const refresh = async () => {
    const current = ticket();
    const rows = await get(`/${firmId()}/reports/outstanding?side=${side}`);
    if (current()) paintOutstanding(root, side, rows);
  };
  await refresh();
  live(root, refresh);
}

function paintOutstanding(root, side, rows) {
  const total = rows.reduce((s, r) => s + r.balance, 0);
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, side === 'receivable' ? 'To receive (customers)' : 'To pay (suppliers)'),
      el('div', { class: 'row' }, el('a', { class: 'btn small' + (side === 'receivable' ? ' primary' : ''), href: '#/outstanding/receivable' }, 'Receivable'),
        el('a', { class: 'btn small' + (side === 'payable' ? ' primary' : ''), href: '#/outstanding/payable' }, 'Payable'),
        el('button', { class: 'btn small', onclick: () => csv(rows, [{ title: 'Party', get: 'name' }, { title: 'Town', get: 'town' }, { title: 'Phone', get: 'phone' }, { title: 'Balance', get: 'balance' }, { title: '0-29', get: 'current' }, { title: '30-59', get: 'days30' }, { title: '60-89', get: 'days60' }, { title: '90-179', get: 'days90' }, { title: '180+', get: 'older' }], `outstanding-${side}.csv`) }, 'Export CSV'))),
    el('p', { class: 'muted' }, `${rows.length} parties · ₹ ${money(total)}`),
    el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
      el('thead', {}, el('tr', {}, ['Party', 'Town', 'Phone', 'Balance', '0–29 d', '30–59', '60–89', '90–179', '180+', 'Oldest bill'].map((h, i) => el('th', { class: i >= 3 && i <= 8 ? 'n' : '' }, h)))),
      el('tbody', {}, rows.map((r) => el('tr', { class: 'click', onclick: () => (location.hash = `#/ledger/${r.accountId}`) },
        el('td', {}, r.name), el('td', {}, r.town), el('td', {}, r.phone), el('td', { class: 'n' }, money(r.balance)),
        ...[r.current, r.days30, r.days60, r.days90, r.older].map((v) => el('td', { class: 'n' }, v ? money(v) : '')), el('td', {}, dmy(r.oldestBill))))))));
}

// ------------------------------------------------------------------ registers & GST
export async function reportsView(root) {
  const f = { kind: 'sales', from: today().slice(0, 8) + '01', to: today() };
  const out = el('div');
  const ticket = newest(); // Sales then Purchases quickly: the register shown is the one now selected
  const load = async () => {
    const current = ticket();
    const r = await get(`/${firmId()}/reports/register?kind=${f.kind}&from=${f.from}&to=${f.to}`);
    if (!current()) return;
    const cols = [{ title: 'Date', get: 'date' }, { title: 'No', get: 'reference' }, { title: 'Supplier bill', get: 'supplierBill' }, { title: 'Party', get: 'party' }, { title: 'GSTIN', get: 'gstin' },
      { title: 'Taxable', get: 'taxable' }, { title: 'CGST', get: 'cgst' }, { title: 'SGST', get: 'sgst' }, { title: 'IGST', get: 'igst' }, { title: 'Round off', get: 'roundOff' }, { title: 'Total', get: 'total' }];
    out.replaceChildren(
      el('div', { class: 'cards', style: { margin: '10px 0' } }, [['Bills', r.rows.length], ['Taxable', money(r.taxable)], ['CGST', money(r.cgst)], ['SGST', money(r.sgst)], ['IGST', money(r.igst)], ['Total', money(r.total)]]
        .map(([k, v]) => el('div', { class: 'card' }, el('div', { class: 'label' }, k), el('div', { class: 'value' }, v)))),
      el('h2', {}, 'GST by rate (B2B = party has GSTIN)'),
      el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['Section', 'Rate', 'Bills', 'Taxable', 'CGST', 'SGST', 'IGST'].map((h, i) => el('th', { class: i >= 1 ? 'n' : '' }, h)))),
        el('tbody', {}, r.slabs.map((x) => el('tr', {}, el('td', {}, x.section), el('td', { class: 'n' }, qty(x.rate) + '%'), el('td', { class: 'n' }, x.bills), ...[x.taxable, x.cgst, x.sgst, x.igst].map((v) => el('td', { class: 'n' }, money(v)))))))),
      el('h2', {}, 'HSN summary'),
      el('div', { class: 'panel scroll', style: { maxHeight: '30vh' } }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['HSN', 'Rate', 'Qty', 'Taxable', 'Tax'].map((h, i) => el('th', { class: i >= 1 ? 'n' : '' }, h)))),
        el('tbody', {}, r.hsn.map((x) => el('tr', {}, el('td', {}, x.hsn || '—'), el('td', { class: 'n' }, qty(x.rate) + '%'), el('td', { class: 'n' }, qty(x.quantity)), el('td', { class: 'n' }, money(x.taxable)), el('td', { class: 'n' }, money(x.tax))))))),
      el('div', { class: 'row spread' }, el('h2', {}, 'Register'), el('button', { class: 'btn small', onclick: () => csv(r.rows, cols, `${f.kind}-register-${f.from}-${f.to}.csv`) }, 'Export CSV')),
      el('div', { class: 'panel scroll' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, cols.map((c, i) => el('th', { class: i >= 5 ? 'n' : '' }, c.title)))),
        el('tbody', {}, r.rows.map((x) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${x.documentId}`) },
          el('td', {}, dmy(x.date)), el('td', {}, x.reference), el('td', {}, x.supplierBill), el('td', {}, x.party), el('td', {}, x.gstin),
          ...[x.taxable, x.cgst, x.sgst, x.igst, x.roundOff, x.total].map((v) => el('td', { class: 'n' }, money(v)))))))));
  };
  const reload = () => load().catch((e) => toast(e.message, true));
  const input = (k, attrs) => { const x = el('input', { value: f[k], ...attrs }); x.addEventListener('change', () => { f[k] = x.value; reload(); }); return x; };
  root.replaceChildren(el('h1', {}, 'Registers & GST'),
    el('div', { class: 'panel row' }, el('select', { onchange: (e) => { f.kind = e.target.value; reload(); } }, el('option', { value: 'sales' }, 'Sales'), el('option', { value: 'purchases' }, 'Purchases')),
      el('label', { class: 'field' }, el('span', {}, 'From'), input('from', { type: 'date' })), el('label', { class: 'field' }, el('span', {}, 'To'), input('to', { type: 'date' }))), out);
  await load();
}

// ------------------------------------------------------------------ stock
export async function stockView(root) {
  let rows = await get(`/${firmId()}/reports/stock`);
  let shown = rows;
  const body = el('tbody');
  const summary = el('p', { class: 'muted' });
  const render = () => {
    summary.textContent = `${rows.length} items · stock value at purchase rate ₹ ${money(rows.reduce((s, r) => s + Math.max(0, r.value), 0))} · negative stock: ${rows.filter((r) => r.stock < 0).length}`;
    body.replaceChildren(...shown.slice(0, 600).map((r) => el('tr', {}, el('td', {}, r.name), el('td', {}, r.group), el('td', {}, r.unit),
      el('td', { class: 'n' }, qty(r.stock)), el('td', { class: 'n' }, money(r.purchaseRate)), el('td', { class: 'n' }, money(r.saleRate)), el('td', { class: 'n' }, money(r.value)), el('td', {}, dmy(r.lastSale)))));
  };
  const search = el('input', { placeholder: 'Filter items…', 'data-rows': '' });
  const filter = () => { const t = search.value.toLowerCase(); shown = rows.filter((r) => (r.name + ' ' + r.group + ' ' + r.code).toLowerCase().includes(t)); };
  search.addEventListener('input', () => { filter(); render(); });
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, 'Stock'), el('div', { class: 'row' }, search,
      el('button', { class: 'btn small', onclick: () => csv(shown, [{ title: 'Item', get: 'name' }, { title: 'Group', get: 'group' }, { title: 'Stock', get: 'stock' }, { title: 'Purchase rate', get: 'purchaseRate' }, { title: 'Sale rate', get: 'saleRate' }, { title: 'Value', get: 'value' }], 'stock.csv') }, 'Export CSV'))),
    summary,
    el('div', { class: 'panel scroll' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['Item', 'Group', 'Unit', 'Stock', 'Purchase', 'Sale', 'Value', 'Last sold'].map((h, i) => el('th', { class: i >= 3 && i <= 6 ? 'n' : '' }, h)))), body)));
  render();
  // A sale saved in HQ changes stock: new figures, same filter text.
  const ticket = newest();
  live(root, async () => {
    const current = ticket();
    const fresh = await get(`/${firmId()}/reports/stock`);
    if (!current()) return;
    rows = fresh;
    filter();
    render();
  });
}

// (HQ health lives in health.js; the older copy that stood here was never routed to.)

// ------------------------------------------------------------------ drafts
// "Today 11:51 pm" / "08-11-2026 12:25 pm": short enough to scan down a list.
const when = (iso) => {
  const d = new Date(iso);
  const t = d.toLocaleTimeString('en-IN', { hour: 'numeric', minute: '2-digit' });
  const day = new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  return (day === today() ? 'Today' : dmy(day)) + ' ' + t;
};
export async function draftsView(root) {
  const rows = await get(`/${firmId()}/drafts`);
  root.replaceChildren(el('h1', {}, 'Drafts'), el('p', { class: 'muted' }, 'Bills being prepared. Nothing here is in HQ until saved.'),
    rows.length ? el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('tbody', {}, rows.map((d) => el('tr', { class: 'click', onclick: () => (location.hash = `#/draft/${d.id}`) },
      el('td', {}, d.title), el('td', { class: 'muted' }, when(d.updatedAt)),
      el('td', { class: 'n' }, el('button', { class: 'btn small danger', onclick: async (e) => {
        e.stopPropagation();
        const btn = e.currentTarget;
        btn.disabled = true; // one discard per click: a double click must not send a second delete
        try { await del(`/${firmId()}/drafts/${d.id}`); await draftsView(root); } catch (x) { toast(x.message, true); btn.disabled = false; }
      } }, 'Discard')))))))
      : el('p', {}, 'No drafts. A bill you leave before saving is kept here automatically.'),
    rows.length ? el('p', { class: 'muted' }, '↓ to move, Enter opens. A bill you leave before saving is kept here automatically.') : null);
}
