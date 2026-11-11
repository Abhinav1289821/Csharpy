// Receipts, payments, credit and debit notes — new, or corrected in place — with bill-wise settlement against open bills.
import { post, get, opId } from './api.js';
import { el, money, today, dmy, debounce, toast, modal, lookup } from './util.js';
import { state, firmId, party, searchParties, loadCatalogue } from './state.js';

const TITLES = {
  cashReceipt: 'Cash receipt', bankReceipt: 'Bank receipt', cashPayment: 'Cash payment', bankPayment: 'Bank payment',
  creditNote: 'Credit note', debitNote: 'Debit note',
};
const KIND_OF_TT = { 204: 'cashReceipt', 205: 'bankReceipt', 206: 'cashPayment', 207: 'bankPayment', 208: 'creditNote', 209: 'debitNote' };
export const voucherKindOfTt = (tt) => KIND_OF_TT[tt];

/** @param {{ editId?: number }} [opts] */
export async function voucherView(root, kind = 'cashReceipt', opts = {}) {
  const note = kind === 'creditNote' || kind === 'debitNote';
  const receipt = kind.endsWith('Receipt');
  const bankKind = kind.startsWith('bank');
  // Which side of an open bill each row settles: receipts and debit-note lines settle customers' bills (debits);
  // a credit note's party row is a credit, so it settles the party's debits; a debit note's party row settles credits.
  const lineSide = receipt ? 'D' : kind === 'debitNote' ? 'D' : 'C';
  const headerSide = kind === 'creditNote' ? 'D' : 'C';
  const v = { kind, date: today(), headerAccountId: bankKind || note ? null : 6, notes: '', lines: [], header: { allocations: [], open: [] } };
  const edit = opts.editId ? await get(`/${firmId()}/vouchers/${opts.editId}`) : null;
  let preview = null;
  let busy = false;

  const newLine = () => ({ accountId: null, amount: '', notes: '', bank: bankKind ? { method: 2, reference: '', date: today() } : null, allocations: [], open: [] });
  const openItems = async (accountId, side) =>
    (await get(`/${firmId()}/accounts/${accountId}/open-items${edit ? `?except=${edit.documentId}` : ''}`)).filter((o) => o.side === side);

  if (edit) {
    const r = edit.voucher;
    Object.assign(v, { date: r.date, headerAccountId: r.headerAccountId, notes: r.notes ?? '' });
    for (const l of r.lines) {
      const p = party(l.accountId);
      v.lines.push({
        accountId: l.accountId, amount: String(l.amount), notes: l.notes ?? '',
        bank: l.bank ? { method: l.bank.method, reference: l.bank.reference ?? '', date: l.bank.date ?? '' } : null,
        allocations: (l.allocations ?? []).map((a) => ({ entryId: a.entryId, amount: String(a.amount) })),
        open: p?.billWise && !note ? await openItems(l.accountId, lineSide) : [],
      });
    }
    if (note) {
      v.header.allocations = (r.headerAllocations ?? []).map((a) => ({ entryId: a.entryId, amount: String(a.amount) }));
      if (party(r.headerAccountId)?.billWise) v.header.open = await openItems(r.headerAccountId, headerSide);
    }
  } else v.lines.push(newLine());

  const allocs = (list) => list.filter((a) => Number(a.amount) > 0).map((a) => ({ entryId: a.entryId, amount: String(a.amount) }));
  const request = () => ({
    kind: v.kind, date: v.date, headerAccountId: v.headerAccountId, notes: v.notes || undefined,
    lines: v.lines.filter((l) => l.accountId && Number(l.amount) > 0).map((l) => ({
      accountId: l.accountId, amount: String(l.amount), notes: l.notes || undefined,
      bank: l.bank ? { method: Number(l.bank.method), reference: l.bank.reference || undefined, date: l.bank.date || undefined } : undefined,
      allocations: allocs(l.allocations),
    })),
    headerAllocations: note ? allocs(v.header.allocations) : undefined,
  });
  const total = () => v.lines.reduce((s, l) => s + (Number(l.amount) || 0), 0);

  const status = el('div', { class: 'stack' });
  const ledger = el('div');
  async function refresh() {
    const req = request();
    if (!req.headerAccountId || !req.lines.length) { preview = null; status.replaceChildren(); ledger.replaceChildren(); return; }
    try {
      preview = await post(edit ? `/${firmId()}/vouchers/${edit.documentId}/amend/preview` : `/${firmId()}/vouchers/preview`, req);
      status.replaceChildren(...preview.warnings.map((w) => el('div', { class: 'issue warn' }, w)),
        el('div', { class: 'grand', 'data-label': note ? 'Note total' : receipt ? 'Received' : 'Paid' }, '₹ ' + money(preview.total)),
        el('div', { class: 'muted' }, edit ? `${edit.reference} (corrected in place)` : `${preview.series}/${preview.nextNumberHint} (next)`));
      ledger.replaceChildren(el('details', {}, el('summary', {}, 'HQ ledger entries'), el('table', { class: 'grid' }, el('tbody', {},
        preview.ledger.map((r) => el('tr', {}, el('td', {}, r.accountName), el('td', {}, r.type), el('td', { class: 'n' }, money(r.amount))))))));
    } catch (e) {
      preview = null;
      status.replaceChildren(el('div', { class: 'issue err' }, e.message));
      ledger.replaceChildren();
    }
  }
  const schedule = debounce(refresh, 300);

  // Bill-wise settlement table shared by lines and a note's party row.
  function settleTable(holder, amountOf) {
    if (!holder.open.length) return null;
    const allocated = holder.allocations.reduce((s, a) => s + (Number(a.amount) || 0), 0);
    const oldestFirst = () => {
      let left = amountOf();
      holder.allocations = holder.open.map((o) => { const take = Math.min(left, o.open); left = Math.round((left - take) * 100) / 100; return { entryId: o.entryId, amount: take > 0 ? take.toFixed(2) : '' }; });
      render();
      schedule();
    };
    return el('div', {},
      el('div', { class: 'row spread' }, el('strong', {}, 'Settle against bills'), el('div', { class: 'row' }, el('span', { class: 'muted' }, `Allocated ₹ ${money(allocated)} of ₹ ${money(amountOf())}`),
        el('button', { class: 'btn small', onclick: oldestFirst }, 'Oldest first'))),
      el('table', { class: 'grid' }, el('thead', {}, el('tr', {}, el('th', {}, 'Bill'), el('th', {}, 'Date'), el('th', { class: 'n' }, 'Days'), el('th', { class: 'n' }, 'Bill amount'), el('th', { class: 'n' }, 'Open'), el('th', { class: 'n' }, 'Settle now'))),
        el('tbody', {}, holder.open.map((o) => {
          let a = holder.allocations.find((x) => x.entryId === o.entryId);
          if (!a) { a = { entryId: o.entryId, amount: '' }; holder.allocations.push(a); }
          const x = el('input', { class: 'num', value: a.amount, style: { width: '110px' } });
          x.addEventListener('input', () => { a.amount = x.value.replace(/[^0-9.]/g, ''); schedule(); });
          return el('tr', {}, el('td', {}, o.reference), el('td', {}, dmy(o.date)), el('td', { class: 'n' }, o.ageDays), el('td', { class: 'n' }, money(o.amount)), el('td', { class: 'n' }, money(o.open)), el('td', { class: 'n' }, x));
        }))));
  }

  const linesBox = el('div', { class: 'stack' });
  const headerBox = el('div');
  function render() {
    linesBox.replaceChildren(...v.lines.map((l, i) => lineCard(l, i)), el('button', { class: 'btn', onclick: () => { v.lines.push(newLine()); render(); } }, '+ Another account'));
    if (note) renderHeader();
  }

  function renderHeader() {
    const p = v.headerAccountId ? party(v.headerAccountId) : null;
    const pick = lookup({
      placeholder: kind === 'creditNote' ? 'Credit note to… (customer / supplier)' : 'Debit note to… (customer / supplier)',
      value: p?.name ?? '',
      search: (t) => searchParties(t, (x) => x.isCustomer || x.isSupplier || ['Customer', 'Supplier', 'Customer-Supplier'].includes(x.type))
        .map((x) => ({ label: x.name, hint: [x.type, x.town, '₹' + money(x.balance)].join(' · '), value: x })),
      onPick: async (x) => {
        v.headerAccountId = x.accountId;
        v.header.allocations = [];
        v.header.open = x.billWise ? await openItems(x.accountId, headerSide) : [];
        render();
        schedule();
      },
      autofocus: !v.headerAccountId,
    });
    headerBox.replaceChildren(el('div', { class: 'panel stack' }, el('label', { class: 'field' }, el('span', {}, 'Party'), pick.wrap),
      p ? el('div', { class: 'muted' }, `${p.type}${p.town ? ' · ' + p.town : ''} · balance ₹ ${money(p.balance)}${p.billWise ? ' · bill-wise' : ''}`) : null,
      settleTable(v.header, total)));
  }

  function lineCard(l, i) {
    const p = l.accountId ? party(l.accountId) : null;
    const pick = lookup({
      placeholder: note ? 'Against account (discount, sales, purchase…)' : receipt ? 'Received from… (customer / account)' : 'Paid to… (supplier / expense)',
      value: p?.name ?? '',
      search: (t) => searchParties(t, (x) => !x.isBank && x.accountId !== 6 && x.accountId !== v.headerAccountId)
        .map((x) => ({ label: x.name, hint: [x.type, x.town, '₹' + money(x.balance)].join(' · '), value: x })),
      onPick: async (x) => {
        l.accountId = x.accountId;
        l.allocations = [];
        l.open = x.billWise && !note ? await openItems(x.accountId, lineSide) : [];
        render();
        linesBox.children[i]?.querySelector('input.amount')?.focus();
        schedule();
      },
      autofocus: !note && i === 0 && !l.accountId,
    });
    const amount = el('input', { class: 'num amount', value: l.amount, inputmode: 'decimal', placeholder: '0.00' });
    amount.addEventListener('input', () => { l.amount = amount.value.replace(/[^0-9.]/g, ''); schedule(); });
    // Enter after the amount: settle the oldest bills first (if nothing is settled by hand yet), then the note.
    amount.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      if (l.open.length && !l.allocations.some((a) => Number(a.amount) > 0) && Number(l.amount) > 0) {
        let left = Number(l.amount);
        l.allocations = l.open.map((o) => { const take = Math.min(left, o.open); left = Math.round((left - take) * 100) / 100; return { entryId: o.entryId, amount: take > 0 ? take.toFixed(2) : '' }; });
        render();
        schedule();
        linesBox.children[i]?.querySelector('input[data-note]')?.focus();
      } else noteBox.focus();
    });
    const noteBox = el('input', { value: l.notes, 'data-note': '', placeholder: note ? 'Reason (e.g. special discount, rate difference)' : 'Note (e.g. UTR / cheque details)' });
    noteBox.addEventListener('input', () => { l.notes = noteBox.value; schedule(); });
    const bankRow = l.bank ? el('div', { class: 'row' },
      el('label', { class: 'field' }, el('span', {}, 'Mode'), el('select', { onchange: (e) => { l.bank.method = Number(e.target.value); schedule(); } },
        [[2, 'Transfer / UPI'], [0, 'Cheque'], [4, 'Other']].map(([k, t]) => el('option', { value: k, selected: k === l.bank.method }, t)))),
      el('label', { class: 'field' }, el('span', {}, 'Ref / cheque no.'), (() => { const x = el('input', { value: l.bank.reference, maxlength: 50 }); x.addEventListener('input', () => { l.bank.reference = x.value; schedule(); }); return x; })()),
      el('label', { class: 'field' }, el('span', {}, 'Date'), (() => { const x = el('input', { type: 'date', value: l.bank.date }); x.addEventListener('input', () => { l.bank.date = x.value; schedule(); }); return x; })())) : null;
    return el('div', { class: 'panel stack' },
      el('div', { class: 'row' }, el('div', { class: 'grow' }, pick.wrap), el('label', { class: 'field' }, el('span', {}, 'Amount'), amount), el('div', { class: 'grow' }, el('label', { class: 'field' }, el('span', {}, note ? 'Reason' : 'Note'), noteBox)),
        v.lines.length > 1 ? el('button', { class: 'btn small', onclick: () => { v.lines.splice(i, 1); render(); schedule(); } }, '×') : null),
      p ? el('div', { class: 'muted' }, `${p.type}${p.town ? ' · ' + p.town : ''} · balance ₹ ${money(p.balance)}${p.billWise ? ' · bill-wise' : ''}`) : null,
      bankRow,
      settleTable(l, () => Number(l.amount) || 0));
  }

  async function save() {
    if (busy) return;
    busy = true;
    try {
      await refresh();
      if (!preview) return;
      const r = edit
        ? await post(`/${firmId()}/vouchers/${edit.documentId}/amend`, { operationId: opId(), expectedHash: preview.hash, voucher: request() })
        : await post(`/${firmId()}/vouchers`, { operationId: opId(), expectedHash: preview.hash, voucher: request() });
      loadCatalogue().catch(() => {});
      await modal((close) => [el('h1', {}, `${edit ? 'Corrected' : 'Saved'} ${r.reference}`),
        el('div', { class: 'saved-amount' }, el('span', {}, note ? TITLES[kind] : receipt ? 'Received' : 'Paid'), el('strong', {}, '₹ ' + money(r.total))), el('p', { class: 'muted' }, 'Now in HQ.'),
        el('div', { class: 'row', style: { justifyContent: 'flex-end' } }, el('button', { class: 'btn primary', onclick: () => close() }, edit ? 'Open voucher (Enter)' : 'Next (Enter)'))]);
      if (edit) location.hash = `#/doc/${edit.documentId}`;
      else voucherView(root, kind);
    } catch (e) {
      toast(e.message, true);
    } finally { busy = false; }
  }

  const banks = state.catalogue.parties.filter((p) => p.isBank && p.active);
  const headerField = note ? null : bankKind
    ? el('label', { class: 'field' }, el('span', {}, 'Bank'), el('select', { onchange: (e) => { v.headerAccountId = Number(e.target.value) || null; schedule(); } },
        el('option', { value: '' }, 'Choose bank…'), banks.map((b) => el('option', { value: b.accountId, selected: b.accountId === v.headerAccountId }, b.name))))
    : el('div', { class: 'field muted' }, el('span', {}, 'Account'), el('div', {}, 'Cash'));
  const date = el('input', { type: 'date', value: v.date });
  date.addEventListener('input', () => { v.date = date.value; schedule(); });
  const narration = el('input', { placeholder: 'Narration', maxlength: 255, value: v.notes });
  narration.addEventListener('input', () => { v.notes = narration.value; schedule(); });
  const kinds = ['cashReceipt', 'bankReceipt', 'cashPayment', 'bankPayment', 'creditNote', 'debitNote'];
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, edit ? `Correct ${TITLES[kind].toLowerCase()} ${edit.reference}` : TITLES[kind]),
      el('div', { class: 'row' },
        edit ? el('a', { class: 'btn small', href: `#/doc/${edit.documentId}` }, 'Cancel')
          : kinds.map((k) => el('a', { class: 'btn small' + (k === kind ? ' primary' : ''), href: `#/voucher/${k}` }, TITLES[k])),
        el('button', { class: 'btn primary', onclick: save }, `${edit ? 'Save correction' : 'Save'} to HQ (F10)`))),
    edit ? el('p', { class: 'muted' }, 'HQ keeps the number; its ledger rows are rewritten and the bills it settled are settled again as shown below.') : null,
    el('div', { class: 'bill-layout', style: { marginTop: '12px' } },
      el('div', { class: 'stack' }, el('div', { class: 'panel row' }, el('label', { class: 'field' }, el('span', {}, 'Date'), date), headerField, el('label', { class: 'field grow' }, el('span', {}, 'Narration'), narration)),
        headerBox, linesBox),
      el('div', { class: 'panel totals stack' }, status, ledger)));
  const keys = (e) => {
    // status belongs to this render only; a re-render ("Next") or another screen disconnects it.
    if (!status.isConnected) return document.removeEventListener('keydown', keys);
    if (e.key === 'F10' || (e.ctrlKey && e.key.toLowerCase() === 's')) { e.preventDefault(); save(); }
  };
  document.addEventListener('keydown', keys);
  render();
  if (edit) refresh();
}
