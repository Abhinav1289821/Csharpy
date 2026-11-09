// Read-side screens: dashboard, document list/view, reports, HQ health, drafts.
import { get, post, del, opId } from './api.js';
import { el, money, qty, dmy, today, addDays, toast, confirmBox, lookup, csv, debounce } from './util.js';
import { state, firmId, searchParties, party } from './state.js';
import { printDocument, invoice } from './print.js';

const KIND_TT = { all: '', sales: '506,507', estimates: '249', purchases: '500,501', orders: '248', receipts: '204,205', payments: '206,207', notes: '208,209' };
const isVoucher = (tt) => tt >= 204 && tt <= 209;
const modeName = (m) => state.firm.paymentModes.find((x) => x.id === m)?.caption ?? m;

// ------------------------------------------------------------------ dashboard
export async function dashboardView(root) {
  const d = await get(`/${firmId()}/reports/dashboard?day=${today()}`);
  const card = (label, value, sub) => el('div', { class: 'card' }, el('div', { class: 'label' }, label), el('div', { class: 'value' }, value), sub ? el('div', { class: 'sub' }, sub) : null);
  root.replaceChildren(
    el('h1', {}, state.firm.name), el('p', { class: 'muted' }, `Today ${dmy(d.day)} · live from HQ`),
    el('div', { class: 'cards' },
      card('Sales today', '₹ ' + money(d.salesToday), `${d.billsToday} bills · cash ₹ ${money(d.cashSalesToday)}`),
      card('Receipts today', '₹ ' + money(d.receiptsToday), `Payments ₹ ${money(d.paymentsToday)}`),
      card('Cash in hand', '₹ ' + money(d.cashInHand)),
      card('To receive', '₹ ' + money(d.receivable), 'customers'),
      card('To pay', '₹ ' + money(d.payable), 'suppliers'),
      card('Sales this month', '₹ ' + money(d.salesMonth), `Year ₹ ${money(d.salesYear)}`)),
    el('div', { class: 'row', style: { margin: '14px 0' } },
      el('a', { class: 'btn primary', href: '#/new/sale' }, 'New sale (F2)'), el('a', { class: 'btn', href: '#/new/salesOrder' }, 'Estimate (F3)'),
      el('a', { class: 'btn', href: '#/new/purchase' }, 'Purchase (F4)'), el('a', { class: 'btn', href: '#/voucher/cashReceipt' }, 'Receipt (F5)')),
    el('h2', {}, 'Recent'), docTable(d.recent.map((r) => ({ ...r, reference: r.reference, party: r.party, amount: r.amount, typeName: r.typeName }))));
}

function docTable(rows) {
  return el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
    el('thead', {}, el('tr', {}, ['Date', 'Type', 'No.', 'Party', 'Mode', 'Amount', 'Open'].map((h, i) => el('th', { class: i >= 5 ? 'n' : '' }, h)))),
    el('tbody', {}, rows.map((r) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${r.documentId}`) },
      el('td', {}, dmy(r.date)), el('td', {}, r.typeName), el('td', {}, r.reference), el('td', {}, r.party, r.town ? el('small', { class: 'muted' }, ' · ' + r.town) : null),
      el('td', {}, r.mode === undefined ? '' : isVoucher(r.ttId) ? '' : modeName(r.mode)), el('td', { class: 'n' }, money(r.amount)),
      el('td', { class: 'n' }, r.open ? money(r.open) : ''))))));
}

// ------------------------------------------------------------------ documents
export async function documentsView(root) {
  const f = { kind: 'all', from: addDays(today(), -30), to: today(), q: '' };
  const out = el('div');
  const load = async () => {
    const qs = new URLSearchParams({ take: '300', from: f.from, to: f.to });
    if (KIND_TT[f.kind]) qs.set('tt', KIND_TT[f.kind]);
    if (f.q) qs.set('q', f.q);
    out.replaceChildren(el('p', { class: 'muted' }, 'Loading…'));
    const rows = await get(`/${firmId()}/documents?${qs}`);
    out.replaceChildren(el('p', { class: 'muted' }, `${rows.length} documents · total ₹ ${money(rows.reduce((s, r) => s + r.amount, 0))}`), docTable(rows));
  };
  const reload = debounce(() => load().catch((e) => toast(e.message, true)), 250);
  const input = (k, attrs) => { const x = el('input', { value: f[k], ...attrs }); x.addEventListener('input', () => { f[k] = x.value; reload(); }); return x; };
  root.replaceChildren(
    el('h1', {}, 'Bills & vouchers'),
    el('div', { class: 'panel row' },
      el('select', { onchange: (e) => { f.kind = e.target.value; reload(); } }, Object.keys(KIND_TT).map((k) => el('option', { value: k }, k[0].toUpperCase() + k.slice(1)))),
      el('label', { class: 'field' }, el('span', {}, 'From'), input('from', { type: 'date' })), el('label', { class: 'field' }, el('span', {}, 'To'), input('to', { type: 'date' })),
      el('label', { class: 'field grow' }, el('span', {}, 'Search party / number / supplier bill'), input('q', { placeholder: 'e.g. CS/879 or ramu' }))),
    out);
  await load();
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
        !voucher ? el('button', { class: 'btn', onclick: () => printDocument(id) }, 'Print') : null,
        s.ttId === 249 ? el('a', { class: 'btn', href: `#/from-order/${id}` }, 'Make sale bill') : null,
        editable ? el('a', { class: 'btn', href: `#/edit/${kindOf}/${id}` }, 'Edit') : null,
        voucher ? el('a', { class: 'btn', href: `#/edit-voucher/${s.ttId}/${id}` }, 'Correct') : null,
        el('button', { class: 'btn danger', onclick: remove }, 'Delete'))),
    voucher ? null : el('div', { class: 'panel paper', style: { marginTop: '12px', maxWidth: '900px' } }, invoice(d)),
    el('h2', {}, 'HQ ledger'),
    el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, el('th', {}, 'Account'), el('th', {}, 'Type'), el('th', { class: 'n' }, 'Amount'))),
      el('tbody', {}, d.ledger.map((r) => el('tr', {}, el('td', {}, r.accountName), el('td', {}, { D: 'Debit', C: 'Credit', P: 'Cash paid', R: 'Cash received' }[r.type] ?? r.type), el('td', { class: 'n' }, money(r.amount))))))),
    d.settlements.length ? [el('h2', {}, 'Settled with'), el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('tbody', {},
      d.settlements.map((x) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${x.documentId}`) }, el('td', {}, x.reference), el('td', {}, dmy(x.date)), el('td', { class: 'n' }, money(x.amount)))))))] : null);
}

// ------------------------------------------------------------------ ledger
export async function ledgerView(root, accountId) {
  const f = { from: state.firm.periodStart, to: today() };
  const out = el('div');
  const pick = lookup({ placeholder: 'Search any account…', value: accountId ? party(accountId)?.name ?? '' : '', autofocus: !accountId,
    search: (t) => searchParties(t).map((p) => ({ label: p.name, hint: `${p.type} · ₹${money(p.balance)}`, value: p })),
    onPick: (p) => { location.hash = `#/ledger/${p.accountId}`; } });
  const load = async () => {
    if (!accountId) return;
    const r = await get(`/${firmId()}/reports/ledger/${accountId}?from=${f.from}&to=${f.to}`);
    out.replaceChildren(el('div', { class: 'row spread', style: { margin: '10px 0' } }, el('h2', {}, `${r.accountName} · ${r.accountType}`),
      el('button', { class: 'btn small', onclick: () => csv(r.lines, [{ title: 'Date', get: 'date' }, { title: 'No', get: 'reference' }, { title: 'Type', get: 'typeName' }, { title: 'Narration', get: 'narration' }, { title: 'Debit', get: 'debit' }, { title: 'Credit', get: 'credit' }, { title: 'Balance', get: 'balance' }], `ledger-${r.accountName}.csv`) }, 'Export CSV')),
      el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
        el('thead', {}, el('tr', {}, ['Date', 'No.', 'Type', 'Particulars', 'Debit', 'Credit', 'Balance'].map((h, i) => el('th', { class: i >= 4 ? 'n' : '' }, h)))),
        el('tbody', {}, el('tr', {}, el('td', {}, dmy(r.from)), el('td', { colspan: 5 }, 'Opening balance'), el('td', { class: 'n' }, bal(r.opening))),
          r.lines.map((l) => el('tr', { class: 'click', onclick: () => (location.hash = `#/doc/${l.documentId}`) }, el('td', {}, dmy(l.date)), el('td', {}, l.reference), el('td', {}, l.typeName),
            el('td', {}, l.contra || l.narration, l.contra && l.narration ? el('small', { class: 'muted' }, ' · ' + l.narration) : null),
            el('td', { class: 'n' }, l.debit ? money(l.debit) : ''), el('td', { class: 'n' }, l.credit ? money(l.credit) : ''), el('td', { class: 'n' }, bal(l.balance))))),
        el('tfoot', {}, el('tr', {}, el('td', { colspan: 4 }, 'Closing'), el('td', { class: 'n' }, money(r.debit)), el('td', { class: 'n' }, money(r.credit)), el('td', { class: 'n' }, bal(r.closing)))))));
  };
  const date = (k) => { const x = el('input', { type: 'date', value: f[k] }); x.addEventListener('change', () => { f[k] = x.value; load(); }); return x; };
  root.replaceChildren(el('h1', {}, 'Account ledger'), el('div', { class: 'panel row' }, el('div', { class: 'grow' }, pick.wrap),
    el('label', { class: 'field' }, el('span', {}, 'From'), date('from')), el('label', { class: 'field' }, el('span', {}, 'To'), date('to'))), out);
  await load();
}
const bal = (v) => money(Math.abs(v)) + (v >= 0 ? ' Dr' : ' Cr');

// ------------------------------------------------------------------ outstanding
export async function outstandingView(root, side = 'receivable') {
  const rows = await get(`/${firmId()}/reports/outstanding?side=${side}`);
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
  const load = async () => {
    const r = await get(`/${firmId()}/reports/register?kind=${f.kind}&from=${f.from}&to=${f.to}`);
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
  const input = (k, attrs) => { const x = el('input', { value: f[k], ...attrs }); x.addEventListener('change', () => { f[k] = x.value; load(); }); return x; };
  root.replaceChildren(el('h1', {}, 'Registers & GST'),
    el('div', { class: 'panel row' }, el('select', { onchange: (e) => { f.kind = e.target.value; load(); } }, el('option', { value: 'sales' }, 'Sales'), el('option', { value: 'purchases' }, 'Purchases')),
      el('label', { class: 'field' }, el('span', {}, 'From'), input('from', { type: 'date' })), el('label', { class: 'field' }, el('span', {}, 'To'), input('to', { type: 'date' }))), out);
  await load();
}

// ------------------------------------------------------------------ stock
export async function stockView(root) {
  const rows = await get(`/${firmId()}/reports/stock`);
  let shown = rows;
  const body = el('tbody');
  const render = () => body.replaceChildren(...shown.slice(0, 600).map((r) => el('tr', {}, el('td', {}, r.name), el('td', {}, r.group), el('td', {}, r.unit),
    el('td', { class: 'n' }, qty(r.stock)), el('td', { class: 'n' }, money(r.purchaseRate)), el('td', { class: 'n' }, money(r.saleRate)), el('td', { class: 'n' }, money(r.value)), el('td', {}, dmy(r.lastSale)))));
  const search = el('input', { placeholder: 'Filter items…' });
  search.addEventListener('input', () => { const t = search.value.toLowerCase(); shown = rows.filter((r) => (r.name + ' ' + r.group + ' ' + r.code).toLowerCase().includes(t)); render(); });
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, 'Stock'), el('div', { class: 'row' }, search,
      el('button', { class: 'btn small', onclick: () => csv(shown, [{ title: 'Item', get: 'name' }, { title: 'Group', get: 'group' }, { title: 'Stock', get: 'stock' }, { title: 'Purchase rate', get: 'purchaseRate' }, { title: 'Sale rate', get: 'saleRate' }, { title: 'Value', get: 'value' }], 'stock.csv') }, 'Export CSV'))),
    el('p', { class: 'muted' }, `${rows.length} items · stock value at purchase rate ₹ ${money(rows.reduce((s, r) => s + Math.max(0, r.value), 0))} · negative stock: ${rows.filter((r) => r.stock < 0).length}`),
    el('div', { class: 'panel scroll' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['Item', 'Group', 'Unit', 'Stock', 'Purchase', 'Sale', 'Value', 'Last sold'].map((h, i) => el('th', { class: i >= 3 && i <= 6 ? 'n' : '' }, h)))), body)));
  render();
}

// ------------------------------------------------------------------ HQ health
export async function healthView(root) {
  root.replaceChildren(el('h1', {}, 'HQ health'), el('p', { class: 'muted' }, 'Checking the HQ database…'));
  const [r, ops] = await Promise.all([get(`/${firmId()}/integrity`), get(`/operations?firm=${firmId()}&take=50`)]);
  const pill = (s) => el('span', { class: 'pill ' + ({ ok: 'ok', warning: 'warn', error: 'err' }[s] ?? '') }, s === 'ok' ? 'OK' : s);
  root.replaceChildren(
    el('h1', {}, 'HQ health'),
    el('p', { class: 'muted' }, 'Read-only checks of the HQ database. MK never changes these on its own; each item says what to fix in HQ.'),
    el('div', { class: 'stack' }, r.findings.map((f) => el('div', { class: 'panel' },
      el('div', { class: 'row spread' }, el('strong', {}, f.title), el('div', { class: 'row' }, f.count ? el('span', { class: 'muted' }, f.count) : null, pill(f.severity))),
      el('div', { class: 'muted' }, f.detail),
      f.examples.length ? el('div', { style: { marginTop: '6px', fontFamily: 'var(--mono)', fontSize: '12px' } }, f.examples.join('  ·  ')) : null,
      f.title === 'HQ procedure contract' && f.severity !== 'ok' ? el('button', { class: 'btn danger', style: { marginTop: '8px' }, onclick: async () => {
        if (await confirmBox('Accept the new HQ procedures?', [el('p', {}, 'Only do this after the parity tests pass against the updated HQ.')], 'Accept', true)) { await post(`/${firmId()}/contract/accept`); healthView(root); }
      } }, 'Accept updated HQ') : null))),
    el('h2', {}, 'Recent saves (MK journal)'),
    el('div', { class: 'panel scroll' }, el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, ['When', 'What', 'Status', 'Document', 'Error'].map((h) => el('th', {}, h)))),
      el('tbody', {}, ops.map((o) => el('tr', { class: o.documentId ? 'click' : '', onclick: () => o.documentId && (location.hash = `#/doc/${o.documentId}`) },
        el('td', {}, new Date(o.createdAt).toLocaleString('en-IN')), el('td', {}, o.kind), el('td', {}, pill(o.status === 'committed' ? 'ok' : o.status === 'rejected' ? 'error' : 'warning')),
        el('td', {}, o.documentId ?? ''), el('td', { class: 'muted' }, o.errorMessage ?? '')))))));
}

// ------------------------------------------------------------------ drafts
export async function draftsView(root) {
  const rows = await get(`/${firmId()}/drafts`);
  root.replaceChildren(el('h1', {}, 'Drafts'), el('p', { class: 'muted' }, 'Bills being prepared. Nothing here is in HQ until saved.'),
    rows.length ? el('div', { class: 'panel' }, el('table', { class: 'grid' }, el('tbody', {}, rows.map((d) => el('tr', {},
      el('td', { class: 'click', onclick: () => (location.hash = `#/draft/${d.id}`) }, d.title), el('td', { class: 'muted' }, new Date(d.updatedAt).toLocaleString('en-IN')),
      el('td', { class: 'n' }, el('button', { class: 'btn small danger', onclick: async () => { await del(`/${firmId()}/drafts/${d.id}`); draftsView(root); } }, 'Discard')))))))
      : el('p', {}, 'No drafts.'));
}
