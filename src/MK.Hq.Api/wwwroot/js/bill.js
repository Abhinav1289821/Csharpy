// Bill editor for sales, estimates (sales orders), purchases and purchase orders — new bills and corrections.
// The server prices every change with HQ's exact GST arithmetic; the browser never computes money.
import { post, get, put, del, opId } from './api.js';
import { el, money, qty, today, addDays, debounce, toast, modal, lookup } from './util.js';
import { state, firmId, item, party, searchItems, searchParties, paymentModes, ttype, loadCatalogue } from './state.js';
import { printDocument } from './print.js';
import { pageUrl } from './capture.js';

const KINDS = {
  sale: { title: 'Sale bill', tt: 506, ttInter: 507, inward: false, order: false },
  salesOrder: { title: 'Estimate / sales order', tt: 249, inward: false, order: true },
  purchase: { title: 'Purchase', tt: 500, ttInter: 501, inward: true, order: false },
  purchaseOrder: { title: 'Purchase order', tt: 248, inward: true, order: true },
};

const blankLine = () => ({ itemId: null, unitId: null, name: '', quantity: '', rate: '', discountPercent: '', description: null, lineId: null, orderLineId: null });

export async function billView(root, { kind = 'sale', amendId = null, draft = null, fromOrder = null } = {}) {
  const meta = KINDS[kind];
  const doc = {
    kind, date: today(), paymentMode: meta.order ? 0 : kind === 'purchase' ? 1 : 0, accountId: null,
    party: { name: '', town: '', phone: '', mobile: '', gstin: '', address: '' },
    interstate: false, basis: null, dueDate: null, notes: '', message: null, salespersonId: null, numberSuffix: '',
    supplier: { number: '', date: today() }, sourceOrderId: null, lines: [blankLine()],
  };
  let original = null; // amend: the saved document
  let preview = null; // last good server preview
  let previewKey = '';
  let busy = false;
  let draftId = draft?.id ?? null;
  let draftVersion = draft?.version ?? 0;
  let saved = false; // set once this bill is in HQ, so leaving it keeps no draft
  let kept = null; // the bill as last kept in Drafts

  if (amendId) {
    original = await get(`/${firmId()}/documents/${amendId}`);
    loadFromView(original);
  } else if (fromOrder) {
    const order = await get(`/${firmId()}/documents/${fromOrder}`);
    loadFromView(order);
    doc.kind = 'sale';
    doc.date = today();
    doc.sourceOrderId = fromOrder;
    doc.lines = order.lines.filter((l) => (l.quantity - (l.supplied ?? 0)) > 0).map((l) => ({ ...lineFrom(l), quantity: String(l.quantity - (l.supplied ?? 0)), lineId: null, orderLineId: l.lineId }));
    doc.lines.push(blankLine());
  } else if (draft) {
    Object.assign(doc, JSON.parse(draft.payload));
    if (!doc.lines.length || doc.lines.at(-1).itemId) doc.lines.push(blankLine());
  }

  function lineFrom(l) {
    return { itemId: l.itemId, unitId: l.unitId, name: l.name, quantity: String(l.quantity), rate: String(l.rate), discountPercent: l.discountPercent ? String(l.discountPercent) : '',
      description: l.description && l.description !== item(l.itemId, l.unitId)?.name ? l.description : null, lineId: l.lineId, orderLineId: l.orderLineId ?? null };
  }

  function loadFromView(v) {
    doc.date = v.summary.date;
    doc.paymentMode = v.summary.mode;
    doc.accountId = v.summary.mode === 0 ? (v.party.headAccountId === 6 ? null : v.party.headAccountId) : v.party.accountId || v.summary.accountId;
    doc.party = { name: v.party.name, town: v.party.town, phone: v.party.phone, mobile: '', gstin: v.party.gstin, address: v.party.address };
    doc.interstate = v.interstate;
    doc.basis = v.basis;
    doc.dueDate = v.dueDate;
    doc.notes = v.notes;
    doc.message = v.message || null;
    doc.supplier = { number: v.supplierBillNumber ?? '', date: v.supplierBillDate ?? v.summary.date };
    doc.lines = v.lines.map(lineFrom);
    doc.lines.push(blankLine());
  }

  // ---------------------------------------------------------------- request shape
  const filled = () => doc.lines.filter((l) => l.itemId && l.quantity !== '' && Number(l.quantity) > 0);
  function request() {
    const credit = doc.paymentMode === 1;
    const bank = doc.paymentMode > 1;
    const p = doc.party;
    return {
      kind: doc.kind, date: doc.date, paymentMode: KINDS[doc.kind].order ? 0 : doc.paymentMode,
      accountId: doc.accountId ?? undefined,
      party: credit ? undefined : { name: p.name, town: p.town || undefined, phone: p.phone || undefined, mobile: p.mobile || undefined,
        gstin: !bank && p.gstin ? p.gstin.toUpperCase() : undefined, gstType: !bank && p.gstin ? 0 : undefined, address: p.address || undefined },
      interstate: doc.interstate, basis: doc.basis ?? undefined, dueDate: doc.dueDate ?? undefined,
      notes: doc.notes || undefined, message: doc.message ?? undefined, salespersonId: doc.salespersonId ?? undefined,
      numberSuffix: doc.numberSuffix || undefined,
      supplier: doc.kind === 'purchase' ? { number: doc.supplier.number, date: doc.supplier.date } : undefined,
      sourceOrderId: doc.sourceOrderId ?? undefined,
      lines: filled().map((l) => ({ itemId: l.itemId, unitId: l.unitId, quantity: String(l.quantity), rate: String(l.rate || 0),
        discountPercent: String(l.discountPercent || 0), description: l.description ?? undefined, orderLineId: l.orderLineId ?? undefined })),
    };
  }

  // ---------------------------------------------------------------- server preview
  const status = el('div', { class: 'stack' });
  const totals = el('div');
  const ledgerBox = el('div');
  const amounts = new Map();

  async function refreshPreview() {
    const req = request();
    const key = JSON.stringify(req);
    if (!req.lines.length) { preview = null; renderTotals(); status.replaceChildren(el('div', { class: 'muted' }, 'Add items to see the bill.')); return; }
    if (key === previewKey && preview) return;
    previewKey = key;
    const priced = filled(); // the line objects this preview prices, in request order
    try {
      const p = original
        ? await post(`/${firmId()}/documents/${original.summary.documentId}/amend/preview`, { operationId: '', documentId: original.summary.documentId, expectedHash: '', document: req, lineIds: lineIds(), allocations: 'preserve' })
        : await post(`/${firmId()}/documents/preview`, req);
      if (key !== previewKey) return;
      preview = original ? { ...p.replacement, hash: p.hash, warnings: p.warnings, released: p.releasedAllocations } : p;
      preview.priced = priced;
      // Stock shortfalls show on their own line; HQ's round-off rule is a setting (HQ health), not news on every bill.
      const shown = (preview.warnings ?? []).filter((w) => !/stock will go below zero|^Round-off .* is posted the way HQ posts it/.test(w));
      status.replaceChildren(checklist(shown));
    } catch (e) {
      if (key !== previewKey) return;
      preview = null;
      status.replaceChildren(el('div', { class: 'issue err' }, e.message));
    }
    renderTotals();
  }
  const schedulePreview = debounce(refreshPreview, 300);
  // One warning reads as a line; several read as a numbered checklist in one box.
  function checklist(items) {
    if (!items.length) return null;
    if (items.length === 1) return el('div', { class: 'issue warn' }, items[0]);
    return el('div', { class: 'checklist' }, el('strong', {}, `Check before saving · ${items.length}`), el('ol', {}, items.map((w) => el('li', {}, w))));
  }
  const lineIds = () => filled().map((l) => l.lineId ?? null);

  function renderTotals() {
    amounts.clear();
    const q = preview?.quote;
    if (q) (preview.priced ?? []).forEach((l, i) => { if (q.lines[i]) amounts.set(l, q.lines[i]); });
    for (const [l, cell] of amountCells) cell.textContent = amounts.has(l) ? money(amounts.get(l).net) : '';
    const rows = q ? [
      ['Items', filled().length], ['Quantity', qty(q.quantity)], ['Gross', money(q.gross)], ['Discount', money(q.discount)], ['Taxable', money(q.taxable)],
      ...(q.igst > 0 ? [['IGST', money(q.igst)]] : [['CGST', money(q.cgst)], ['SGST', money(q.sgst)]]), ['Round off', money(q.roundOff)],
    ] : [];
    totals.replaceChildren(
      el('dl', {}, rows.map(([k, v]) => [el('dt', {}, k), el('dd', {}, v)])),
      el('div', { class: q ? 'grand' : 'grand empty', 'data-label': KINDS[doc.kind].inward ? 'Bill amount' : 'Payable' }, '₹ ' + money(q ? q.payable : 0)),
      preview ? el('div', { class: 'muted' }, `${preview.typeName} · ${original ? original.summary.reference : preview.series + '/' + preview.nextNumberHint + ' (next)'} · ${preview.paymentCaption}`) : null,
      doc.capture?.printedTotal != null
        ? (q && Number(q.payable) === Number(doc.capture.printedTotal)
          ? el('div', { class: 'issue ok' }, `Matches the printed total ₹${money(doc.capture.printedTotal)} ✓`)
          : el('div', { class: 'issue warn' }, `Printed total ₹${money(doc.capture.printedTotal)}`, q ? ` · difference ₹${money(q.payable - doc.capture.printedTotal)}` : ''))
        : null);
    ledgerBox.replaceChildren(preview?.ledger?.length ? el('details', {}, el('summary', {}, 'HQ ledger entries'),
      el('table', { class: 'grid' }, el('tbody', {}, preview.ledger.map((r) => el('tr', {}, el('td', {}, r.accountName), el('td', {}, r.type), el('td', { class: 'n' }, money(r.amount))))))) : '');
  }

  // ---------------------------------------------------------------- header
  const credit = () => doc.paymentMode === 1;
  const bank = () => doc.paymentMode > 1;
  const header = el('div', { class: 'bill-head' });
  let partyFocused = false; // the customer box takes focus once, when the bill opens
  const firstItemInput = () => [...body.querySelectorAll('input.item')].find((i) => !i.value) ?? body.querySelector('input.item');
  // Enter in the bill head moves on to the items (a lookup that just picked something has already handled its Enter).
  header.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || e.defaultPrevented || e.target.tagName !== 'INPUT') return;
    e.preventDefault();
    firstItemInput()?.focus();
  });

  function partyFilter(p) {
    if (bank()) return p.isBank;
    if (KINDS[doc.kind].inward) return p.isSupplier || (!credit() && p.isCustomer);
    return p.isCustomer;
  }

  function renderHeader() {
    const modes = KINDS[doc.kind].order ? [] : paymentModes().filter((m) => (KINDS[doc.kind].inward ? m.id <= 1 : m.id !== 3));
    const field = (label, input) => el('label', { class: 'field' }, el('span', {}, label), input);
    const bind = (obj, key, attrs = {}) => {
      const i = el('input', { value: obj[key] ?? '', 'data-bind': key, ...attrs });
      i.addEventListener('input', () => { obj[key] = i.value; schedulePreview(); });
      return i;
    };
    const selected = doc.accountId ? party(doc.accountId) : null;
    const partyBox = lookup({
      placeholder: credit() ? 'Search account…' : bank() ? 'Search bank ledger…' : KINDS[doc.kind].inward ? 'Supplier name (or search an account)' : 'Customer name (or search an account)',
      value: credit() || bank() ? (selected?.name ?? '') : doc.party.name,
      search: (t) => searchParties(t, partyFilter).map((p) => ({ label: p.name, hint: [p.town, p.gstin, p.balance ? '₹' + money(p.balance) : ''].filter(Boolean).join(' · '), value: p })),
      onInput: (t) => {
        if (credit() || bank()) return;
        if (doc.accountId) {
          // Retyping after picking an account: that account's town/phone/GSTIN no longer apply.
          Object.assign(doc.party, { town: '', phone: '', gstin: '' });
          for (const k of ['town', 'phone', 'gstin']) { const box = header.querySelector(`[data-bind="${k}"]`); if (box) box.value = ''; }
        }
        doc.party.name = t; doc.accountId = null; schedulePreview();
      },
      // A walk-in name is kept as typed; an existing account is used only when chosen with ↓.
      explicit: !credit() && !bank(),
      onPick: (p) => {
        doc.accountId = p.accountId;
        if (!credit() && !bank()) Object.assign(doc.party, { name: p.name, town: p.town, phone: p.mobile || p.phone, gstin: p.gstin });
        renderHeader();
        schedulePreview();
        (header.querySelector('.after-party') ?? firstItemInput())?.focus();
      },
      autofocus: !original && !partyFocused && doc.lines.length === 1,
    });
    partyFocused = true;
    const cash = !credit() && !bank();
    // A bill dated other than today is easy to miss; mark it.
    const dateBox = bind(doc, 'date', { type: 'date' });
    const dateField = field(doc.date === today() ? 'Date' : 'Date · not today', dateBox);
    const markDate = () => { const off = doc.date !== today(); dateField.classList.toggle('off-today', off); dateField.firstChild.textContent = off ? 'Date · not today' : 'Date'; };
    dateBox.addEventListener('input', markDate);
    markDate();
    header.replaceChildren(
      el('label', { class: 'field party-field' }, el('span', {}, bank() ? 'Bank / UPI ledger' : KINDS[doc.kind].inward ? 'Supplier' : 'Customer'), partyBox.wrap),
      modes.length ? field('Payment', el('select', { onchange: (e) => { doc.paymentMode = Number(e.target.value); doc.accountId = null; renderHeader(); schedulePreview(); } },
        modes.map((m) => el('option', { value: m.id, selected: m.id === doc.paymentMode }, m.caption)))) : null,
      bank() ? field('Paid by (name)', bind(doc.party, 'name', { class: 'after-party' })) : null,
      cash || bank() ? field('Town', bind(doc.party, 'town', { class: cash ? 'after-party' : '' })) : null,
      cash || bank() ? field('Phone', bind(doc.party, 'phone')) : null,
      cash ? field('GSTIN (if registered)', bind(doc.party, 'gstin', { maxlength: 15, style: { textTransform: 'uppercase' } })) : null,
      credit() && selected ? el('div', { class: 'field muted' }, el('span', {}, 'Account'), el('div', {}, [selected.town, selected.gstin, 'Balance ₹ ' + money(selected.balance)].filter(Boolean).join(' · '))) : null,
      doc.kind === 'purchase' ? field('Supplier bill no.', bind(doc.supplier, 'number', { maxlength: 20, class: 'after-party' })) : null,
      doc.kind === 'purchase' ? field('Supplier bill date', bind(doc.supplier, 'date', { type: 'date' })) : null,
      // Fields that rarely change sit in a quieter second row.
      el('div', { class: 'bill-head-more' }, dateField,
      field('Prices', el('select', { onchange: (e) => { doc.basis = e.target.value || null; schedulePreview(); } },
        el('option', { value: '' }, 'HQ default'), el('option', { value: 'inclusive', selected: doc.basis === 'inclusive' }, 'Include GST'),
        el('option', { value: 'exclusive', selected: doc.basis === 'exclusive' }, 'Plus GST'))),
      KINDS[doc.kind].ttInter && !original ? el('label', { class: 'field' }, el('span', {}, 'Other state (IGST)'),
        el('input', { type: 'checkbox', checked: doc.interstate, onchange: (e) => { doc.interstate = e.target.checked; schedulePreview(); } })) : null,
      (state.firm.salespeople ?? []).length && !KINDS[doc.kind].inward ? field('Salesman', el('select', { onchange: (e) => { doc.salespersonId = e.target.value ? Number(e.target.value) : null; schedulePreview(); } },
        el('option', { value: '' }, '—'), state.firm.salespeople.map((s) => el('option', { value: s.id, selected: s.id === doc.salespersonId }, s.name)))) : null,
      field('Narration', bind(doc, 'notes', { maxlength: 1500 }))),
    );
  }

  // ---------------------------------------------------------------- lines
  const body = el('tbody');
  const amountCells = new Map();

  function renderLines() {
    amountCells.clear();
    body.replaceChildren(...doc.lines.map((l, i) => lineRow(l, i)));
    renderTotals();
  }

  function lineRow(l, i) {
    const it = l.itemId ? item(l.itemId, l.unitId) : null;
    const num = (key, cls) => {
      const input = el('input', { class: 'num ' + cls, value: l[key], inputmode: 'decimal' });
      input.addEventListener('input', () => { l[key] = input.value.replace(/[^0-9.]/g, ''); if (key === 'quantity') markStock(); schedulePreview(); });
      input.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        const next = input.closest('td').nextElementSibling?.querySelector('input');
        if (next && key !== 'discountPercent') next.focus();
        else nextRow(i);
      });
      return input;
    };
    const pick = lookup({
      placeholder: 'Type item name / code…',
      value: l.name,
      search: (t) => searchItems(t).map((x) => ({ label: x.name, hint: `${x.unit} · ₹${money(KINDS[doc.kind].inward ? x.purchaseRate : x.saleRate)} · stock ${qty(x.stock)} · GST ${x.cgst + x.sgst}%`, value: x })),
      onInput: (t) => { l.name = t; },
      onPick: (x, input) => {
        // A rate read from the bill photo stays; otherwise take HQ's rate for the item.
        Object.assign(l, { itemId: x.itemId, unitId: x.unitId, name: x.name, confirmed: true, rate: l.sourceText && l.rate ? l.rate : String(KINDS[doc.kind].inward ? x.purchaseRate : x.saleRate) });
        if (!l.quantity) l.quantity = '1';
        if (i === doc.lines.length - 1) doc.lines.push(blankLine());
        renderLines();
        body.children[i].querySelector('.col-q input')?.focus();
        body.children[i].querySelector('.col-q input')?.select();
        schedulePreview();
      },
      className: 'item',
    });
    const amount = el('td', { class: 'n col-a' });
    // Selling more than HQ shows in stock is flagged on the line itself, and follows the quantity as it is typed.
    const stockNote = el('span');
    const markStock = () => {
      if (!it) return;
      const short = !KINDS[doc.kind].inward && !KINDS[doc.kind].order && Number(it.stock) - Number(l.quantity || 0) < 0;
      stockNote.className = short ? 'short' : '';
      stockNote.textContent = `stock ${qty(it.stock)}${short ? ' · short' : ''}`;
    };
    markStock();
    amountCells.set(l, amount);
    return el('tr', {},
      el('td', { class: 'muted' }, i + 1),
      el('td', { class: 'col-item' }, pick.wrap, it ? el('small', { class: 'muted' }, `${it.unit} · HSN ${it.hsn || '—'} · `, stockNote, l.orderLineId ? ' · from order' : '') : null,
        l.sourceText ? photoHints(l, i) : null),
      el('td', { class: 'col-q' }, num('quantity', 'q')),
      el('td', { class: 'col-r' }, num('rate', 'r')),
      el('td', { class: 'col-d' }, num('discountPercent', 'd')),
      amount,
      el('td', {}, l.itemId ? el('button', { class: 'btn small', title: 'Remove line', onclick: () => { doc.lines.splice(i, 1); if (!doc.lines.length) doc.lines.push(blankLine()); renderLines(); schedulePreview(); } }, '×') : null));
  }

  // What the photo said, and the matcher's suggestions until the user confirms one.
  function photoHints(l, i) {
    const cands = l.match?.candidates ?? [];
    const pickCandidate = (cnd) => {
      Object.assign(l, { itemId: cnd.itemId, unitId: cnd.unitId, name: cnd.name, confirmed: true, rate: l.rate || String(KINDS[doc.kind].inward ? cnd.purchaseRate : cnd.saleRate) });
      renderLines();
      schedulePreview();
    };
    const sure = l.confirmed || (l.itemId && l.match?.reason === 'learned');
    return el('div', { style: { fontSize: '12px', marginTop: '2px' } },
      el('div', { class: 'muted' }, 'Photo: ', el('i', {}, l.sourceText), l.written?.amount ? ` · printed ₹${money(l.written.amount)}` : ''),
      !sure && cands.length ? el('div', { class: 'row', style: { gap: '4px', marginTop: '3px' } },
        el('span', { class: l.itemId ? 'muted' : 'pill warn' }, l.itemId ? 'Or:' : 'Choose:'),
        cands.filter((x) => x.itemId !== l.itemId).slice(0, 4).map((x) => el('button', { class: 'btn small', title: (x.notes ?? []).join('; '), onclick: () => pickCandidate(x) },
          x.name, el('small', { class: 'muted' }, ` ${Math.round(x.score * 100)}%`), x.notes?.length ? ' ⚠' : ''))) : null);
  }

  function nextRow(i) {
    if (i === doc.lines.length - 1 && doc.lines[i].itemId) { doc.lines.push(blankLine()); renderLines(); }
    body.children[i + 1]?.querySelector('input.item')?.focus();
  }

  // ---------------------------------------------------------------- save
  async function save() {
    if (busy) return;
    busy = true;
    saveBtn.disabled = true;
    try {
      previewKey = '';
      await refreshPreview();
      if (!preview) return;
      if (!original && !KINDS[doc.kind].order && doc.paymentMode === 0 && !doc.party.name.trim()) { toast('Enter the customer name', true); header.querySelector('.lookup input')?.focus(); return; }
      const req = request();
      let receipt;
      if (original) {
        const ok = await confirmAmend();
        if (!ok) return;
        receipt = await post(`/${firmId()}/documents/${original.summary.documentId}/amend`,
          { operationId: opId(), documentId: original.summary.documentId, expectedHash: preview.hash, document: req, lineIds: lineIds(), allocations: ok });
        saved = true;
        toast(`Saved changes to ${receipt.reference}`);
        location.hash = `#/doc/${receipt.documentId}`;
        return;
      }
      receipt = await post(`/${firmId()}/documents`, { operationId: opId(), expectedHash: preview.hash, document: req });
      saved = true;
      if (doc.capture?.id) {
        // Remember "this wording means this HQ item" so the next bill from the same supplier matches itself.
        const learned = filled().filter((l) => l.sourceText).map((l) => ({ sourceText: l.sourceText, name: l.written?.name ?? null, itemId: l.itemId, unitId: l.unitId }));
        post(`/${firmId()}/captures/${doc.capture.id}/learn`, { documentId: receipt.documentId, reference: receipt.reference, lines: learned }).catch(() => {});
      }
      if (draftId) { try { await del(`/${firmId()}/drafts/${draftId}`); } catch { /* already gone */ } }
      loadCatalogue().catch(() => {});
      await afterSave(receipt);
    } catch (e) {
      toast(e.message, true);
      if (e.code === 'REVIEW_CHANGED') { previewKey = ''; refreshPreview(); }
    } finally {
      busy = false;
      saveBtn.disabled = false;
    }
  }

  async function confirmAmend() {
    const settled = original.settlements?.length;
    return modal((close) => [
      el('h1', {}, `Save changes to ${original.summary.reference}?`),
      el('p', {}, `HQ will show this bill as edited (revision ${original.summary.modifyCnt + 1}). Total ₹ ${money(original.total)} → ₹ ${money(preview.quote.payable)}.`),
      settled ? el('p', { class: 'issue warn' }, `${settled} payment(s) are settled against this bill. Keep them attached (normal), or detach them so they become on-account?`) : null,
      el('div', { class: 'row', style: { justifyContent: 'flex-end' } },
        el('button', { class: 'btn', onclick: () => close(false) }, 'Cancel'),
        settled ? el('button', { class: 'btn', onclick: () => close('release') }, 'Detach payments & save') : null,
        el('button', { class: 'btn primary', onclick: () => close('preserve') }, 'Save changes')),
    ]);
  }

  async function afterSave(r) {
    const choice = await modal((close) => [
      el('h1', {}, `Saved ${r.reference}`),
      // The figure the operator says to the customer, big enough to read across the counter.
      el('div', { class: 'saved-amount' }, el('span', {}, doc.paymentMode === 1 ? 'Added to account' : KINDS[doc.kind].inward ? 'Bill total' : 'Collect'), el('strong', {}, '₹ ' + money(r.total))),
      el('p', { class: 'muted' }, 'Now in HQ exactly as if entered there.'),
      el('div', { class: 'row', style: { justifyContent: 'flex-end' } },
        el('button', { class: 'btn', 'data-key': 'o', onclick: () => close('open') }, 'Open bill (O)'),
        el('button', { class: 'btn', 'data-key': 'p', onclick: () => close('print') }, 'Print (P)'),
        el('button', { class: 'btn primary', onclick: () => close('new') }, 'Next bill (Enter)')),
    ]);
    window.removeEventListener('hashchange', onLeave);
    window.removeEventListener('beforeunload', onUnload);
    if (choice === 'print') { await printDocument(r.documentId); billView(root, { kind: doc.kind }); }
    else if (choice === 'open') location.hash = `#/doc/${r.documentId}`;
    else billView(root, { kind: doc.kind });
  }

  async function saveDraft(quiet = false) {
    const id = draftId ?? crypto.randomUUID();
    const title = `${KINDS[doc.kind].title} · ${doc.party.name || party(doc.accountId)?.name || 'no party'} · ${filled().length} ${filled().length === 1 ? 'line' : 'lines'}`;
    const saved = await put(`/${firmId()}/drafts/${id}`, { kind: doc.kind, title, payload: doc, version: draftVersion });
    draftId = saved.id;
    draftVersion = saved.version;
    kept = JSON.stringify(doc);
    toast(quiet ? `Unsaved ${KINDS[doc.kind].title.toLowerCase()} kept in Drafts` : 'Draft saved (not in HQ yet)');
  }

  // ---------------------------------------------------------------- layout
  const saveBtn = el('button', { class: 'btn primary', onclick: save }, original ? 'Save changes (F10)' : 'Save to HQ (F10)');
  root.replaceChildren(
    el('div', { class: 'page-head' },
      el('div', {}, el('h1', {}, original ? `Edit ${original.summary.reference}` : doc.sourceOrderId ? 'Sale from order' : KINDS[doc.kind].title),
        el('div', { class: 'muted' }, original ? `${original.summary.typeName} · ${original.party.name}` : 'Fill the party, then type item names below.')),
      el('div', { class: 'row' }, !original ? el('button', { class: 'btn', onclick: saveDraft }, 'Save draft') : null, saveBtn)),
    el('div', { class: 'panel bill-head-panel' }, header),
    doc.capture ? photoPanel() : null,
    el('div', { class: 'bill-layout' },
      el('div', { class: 'panel flush' }, el('table', { class: 'grid bill-grid' },
        el('thead', {}, el('tr', {}, el('th', {}, '#'), el('th', {}, 'Item'), el('th', { class: 'n' }, 'Qty'), el('th', { class: 'n' }, 'Rate'), el('th', { class: 'n' }, 'Disc %'), el('th', { class: 'n' }, 'Amount'), el('th', {}))),
        body)),
      el('div', { class: 'panel totals stack' }, el('div', { class: 'totals-title' }, 'Bill total'), totals, status, ledgerBox)),
    el('div', { class: 'keyhelp' },
      el('span', {}, el('kbd', {}, 'Enter'), ' next box'), el('span', {}, el('kbd', {}, '↑'), el('kbd', {}, '↓'), ' choose from list'),
      el('span', {}, el('kbd', {}, 'Tab'), ' pick'), el('span', {}, el('kbd', {}, 'F10'), ' save to HQ'), el('span', {}, el('kbd', {}, 'Esc'), ' close list')));

  function photoPanel() {
    const cap = doc.capture;
    const pages = Math.max(1, cap.pages ?? 1);
    let zoom = 1;
    const imgs = Array.from({ length: pages }, (_, n) => el('img', { src: pageUrl(cap.id, n + 1), alt: `Bill page ${n + 1}`, style: { width: '100%', display: 'block', background: '#fff', marginBottom: '6px' } }));
    const frame = el('div', { style: { maxHeight: '36vh', overflow: 'auto', borderRadius: '6px', border: '1px solid var(--line)' } }, imgs);
    const setZoom = (z) => { zoom = z; imgs.forEach((i) => (i.style.width = `${zoom * 100}%`)); };
    return el('details', { class: 'panel', open: true, style: { marginTop: '12px' } },
      el('summary', {}, `Bill photo · read by ${cap.model}`, cap.printedTotal != null ? ` · printed total ₹${money(cap.printedTotal)}` : ''),
      el('div', { class: 'row', style: { margin: '6px 0' } }, el('button', { class: 'btn small', onclick: () => setZoom(Math.max(0.5, zoom - 0.25)) }, '−'),
        el('button', { class: 'btn small', onclick: () => setZoom(Math.min(3, zoom + 0.25)) }, '+'),
        el('a', { class: 'btn small', href: pageUrl(cap.id, 1), target: '_blank' }, 'Open full size'),
        el('a', { class: 'btn small', href: '#/capture' }, 'All scans')),
      frame,
      (cap.warnings ?? []).length ? el('div', { style: { marginTop: '10px' } }, checklist(cap.warnings)) : null);
  }

  // This render's own marker: after "Next" re-renders into the same root, the old handler must retire, or F10 would
  // save the previous (already saved) bill again.
  const alive = el('span', { hidden: true });
  root.append(alive);
  const keys = (e) => {
    if (!alive.isConnected) return document.removeEventListener('keydown', keys);
    if (e.key === 'F10' || (e.ctrlKey && e.key.toLowerCase() === 's')) { e.preventDefault(); save(); }
  };
  document.addEventListener('keydown', keys);
  // Leaving a half-made bill (a stray F-key, a menu click) keeps it in Drafts instead of losing it.
  const unsaved = () => !saved && !original && filled().length > 0 && JSON.stringify(doc) !== kept;
  const onLeave = () => {
    window.removeEventListener('hashchange', onLeave);
    window.removeEventListener('beforeunload', onUnload);
    if (unsaved()) saveDraft(true).catch((e) => toast('Could not keep the unsaved bill: ' + e.message, true));
  };
  const onUnload = (e) => { if (alive.isConnected && unsaved()) { e.preventDefault(); e.returnValue = ''; } };
  window.addEventListener('hashchange', onLeave);
  window.addEventListener('beforeunload', onUnload);
  renderHeader();
  renderLines();
  refreshPreview();
  kept = draftId ? JSON.stringify(doc) : null; // a reopened draft left unchanged is not saved again
}
