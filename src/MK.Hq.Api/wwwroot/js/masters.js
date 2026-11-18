// Item and account masters: created and changed exactly as HQ's own Items and Accounts screens do.
import { get, post, opId } from './api.js';
import { el, money, debounce, toast, rank, oneDecimal } from './util.js';
import { state, firmId, loadCatalogue, ITEM_FIELDS, PARTY_FIELDS } from './state.js';

const field = (label, input, hint) => el('label', { class: 'field' }, el('span', {}, label), input, hint ? el('small', { class: 'muted' }, hint) : null);

function bind(input, obj, key, onChange, transform = (x) => x) {
  input.value = obj[key] ?? '';
  input.addEventListener('input', () => {
    const v = transform(input.value);
    // Show what is kept: a rate typed "1..5" or "12a" is stored cleaned, so the box must not keep showing the raw text.
    if (typeof v === 'string' && v !== input.value) input.value = v;
    obj[key] = v;
    onChange();
  });
  return input;
}

// A live-previewed master form: every change is checked by the server against HQ before Save is offered.
function masterForm({ previewUrl, saveUrl, request, wrap, render, onSaved, title }) {
  let preview = null;
  let busy = false;
  let seq = 0; // only the newest preview may land: an older, slower answer must not overwrite it
  let pending = { key: '', id: '' };
  const status = el('div', { class: 'stack' });
  const refresh = async () => {
    const mine = ++seq;
    try {
      const p = await post(previewUrl, request());
      if (mine !== seq) return;
      preview = p;
      status.replaceChildren(...render(preview), ...preview.warnings.map((w) => el('div', { class: 'issue warn' }, w)));
    } catch (e) {
      if (mine !== seq) return;
      preview = null;
      status.replaceChildren(el('div', { class: 'issue err' }, e.message));
    }
  };
  const schedule = debounce(refresh, 350);
  const save = async () => {
    if (busy) return;
    busy = true;
    try {
      await refresh();
      // F10 on a form HQ would refuse (a blank name, a GSTIN that does not check out) says why, not nothing.
      if (!preview) { toast(status.querySelector('.issue.err')?.textContent || 'Not ready to save: check the form', true); return; }
      // Same id when the same reviewed form is sent again after a lost response: never two new items/accounts.
      const body = { expectedHash: preview.hash, ...wrap(request()) };
      const key = JSON.stringify(body);
      if (pending.key !== key) pending = { key, id: opId() };
      const r = await post(saveUrl, { operationId: pending.id, ...body });
      await loadCatalogue().catch(() => {});
      toast(`${title(r)} saved in HQ`);
      onSaved(r);
    } catch (e) { toast(e.message, true); } finally { busy = false; }
  };
  const keys = (e) => {
    if (!status.isConnected) return document.removeEventListener('keydown', keys);
    if (e.key === 'F10' || (e.ctrlKey && e.key.toLowerCase() === 's')) { e.preventDefault(); save(); }
  };
  document.addEventListener('keydown', keys);
  return { status, schedule, save, refresh };
}

// ------------------------------------------------------------------ items

export async function itemsView(root) {
  const items = [...new Map(state.catalogue.items.map((i) => [i.itemId, i])).values()];
  const out = el('div');
  const show = (text) => {
    const rows = text ? rank(items, text, ITEM_FIELDS, 200) : items.slice(0, 200);
    out.replaceChildren(el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
      el('thead', {}, el('tr', {}, ['Item', 'Code', 'Group', 'HSN', 'GST %', 'Purchase', 'Sale', 'Stock'].map((h, i) => el('th', { class: i >= 4 ? 'n' : '' }, h)))),
      el('tbody', {}, rows.map((i) => el('tr', { class: 'click', onclick: () => (location.hash = `#/items/${i.itemId}`) },
        el('td', {}, i.name, i.active ? null : el('small', { class: 'muted' }, ' · inactive')), el('td', {}, i.code), el('td', {}, i.group), el('td', {}, i.hsn),
        el('td', { class: 'n' }, Number(i.igst)), el('td', { class: 'n' }, money(i.purchaseRate)), el('td', { class: 'n' }, money(i.saleRate)), el('td', { class: 'n' }, Number(i.stock))))))));
  };
  const search = el('input', { placeholder: 'Search name, code, group or HSN  ·  Enter opens the top item', autofocus: true, 'data-rows': '' });
  search.addEventListener('input', debounce(() => show(search.value.trim()), 150));
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, 'Items'), el('a', { class: 'btn primary', href: '#/items/new' }, '+ New item')),
    el('div', { class: 'panel row' }, el('div', { class: 'grow' }, search), el('span', { class: 'muted' }, `${items.length} items in HQ`)), out);
  show('');
  search.focus();
}

export async function itemForm(root, id) {
  const options = await get(`/${firmId()}/masters/items/options`);
  const it = id ? await get(`/${firmId()}/masters/items/${id}`)
    : { name: '', code: '', groupId: 0, categoryId: 0, gstRate: 18, hsn: '', purchaseRate: '', saleRate: '', mrp: '', description: '', active: true, pack: options.pack };
  const request = () => ({ ...it, itemId: id ?? undefined, purchaseRate: String(it.purchaseRate || 0), saleRate: String(it.saleRate || 0), mrp: String(it.mrp || 0),
    groupId: Number(it.groupId), categoryId: Number(it.categoryId), gstRate: String(it.gstRate) });
  const f = masterForm({
    previewUrl: `/${firmId()}/masters/items/preview`, saveUrl: `/${firmId()}/masters/items`, request, wrap: (item) => ({ item }),
    title: (r) => `${r.name} (code ${r.code})`,
    onSaved: (r) => { location.hash = `#/items/${r.itemId}`; },
    render: (p) => [
      el('div', {}, el('strong', {}, 'Code '), p.code),
      el('details', { open: true }, el('summary', {}, 'HQ accounts for this GST rate'), el('table', { class: 'grid' }, el('tbody', {},
        p.accounts.map((a) => el('tr', {}, el('td', {}, a.use), el('td', {}, a.account))))))],
  });
  const s = f.schedule;
  const num = { inputmode: 'decimal', class: 'num' };
  const select = (key, list, blank) => {
    const x = el('select', {}, blank ? el('option', { value: 0 }, blank) : null, list.map((o) => el('option', { value: o.id, selected: Number(it[key]) === o.id }, o.name)));
    x.addEventListener('change', () => { it[key] = Number(x.value); s(); });
    return x;
  };
  const rates = el('select', {}, options.gstRates.map((r) => el('option', { value: r, selected: Number(it.gstRate) === Number(r) }, `${r}%`)));
  rates.addEventListener('change', () => { it.gstRate = rates.value; s(); });
  const active = el('input', { type: 'checkbox', checked: it.active });
  active.addEventListener('change', () => { it.active = active.checked; s(); });
  const decimal = (v) => oneDecimal(v);
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, id ? `Item: ${it.name}` : 'New item'),
      el('div', { class: 'row' }, el('a', { class: 'btn small', href: '#/items' }, 'All items'), el('button', { class: 'btn primary', onclick: f.save }, 'Save to HQ (F10)'))),
    el('div', { class: 'bill-layout', style: { marginTop: '12px' } },
      el('div', { class: 'panel stack' },
        field('Name', bind(el('input', { maxlength: 70, autofocus: true }), it, 'name', s), 'As it should print on bills (up to 70 characters)'),
        el('div', { class: 'row' },
          field('Group (company)', select('groupId', options.groups, 'Choose group…')),
          field('Category', select('categoryId', options.categories, '—'))),
        el('div', { class: 'row' },
          field('Code', bind(el('input', { maxlength: 30, placeholder: options.defaultCode }), it, 'code', s), id ? null : `Blank = HQ's next code ${options.defaultCode}`),
          field('Pack', id ? el('input', { value: it.pack ?? '', disabled: true }) : bind(el('input', { maxlength: 15 }), it, 'pack', s), id ? 'HQ keeps the pack once set' : null),
          field('HSN code', bind(el('input', { maxlength: 50 }), it, 'hsn', s)),
          field('GST', rates)),
        el('div', { class: 'row' },
          field('Purchase rate', bind(el('input', num), it, 'purchaseRate', s, decimal)),
          field('Sale rate', bind(el('input', num), it, 'saleRate', s, decimal)),
          field('MRP', bind(el('input', num), it, 'mrp', s, decimal))),
        field('Description', bind(el('input', { maxlength: 500 }), it, 'description', s)),
        el('label', { class: 'row' }, active, el('span', {}, 'Active (inactive items are hidden from bills in HQ)'))),
      el('div', { class: 'panel totals stack' }, f.status)));
  f.refresh();
}

// ------------------------------------------------------------------ accounts

export async function accountsView(root, filter = 'all') {
  const parties = state.catalogue.parties.filter((p) => ['Customer', 'Supplier', 'Customer-Supplier'].includes(p.type) && (filter === 'all' || p.type === filter));
  const out = el('div');
  const show = (text) => {
    const rows = text ? rank(parties, text, PARTY_FIELDS, 200) : parties.slice(0, 200);
    out.replaceChildren(el('div', { class: 'panel scroll' }, el('table', { class: 'grid' },
      el('thead', {}, el('tr', {}, ['Name', 'Type', 'Town', 'Mobile', 'GSTIN', 'Balance'].map((h, i) => el('th', { class: i === 5 ? 'n' : '' }, h)))),
      el('tbody', {}, rows.map((p) => el('tr', { class: 'click', onclick: () => (location.hash = `#/accounts/${p.accountId}`) },
        el('td', {}, p.name, p.active ? null : el('small', { class: 'muted' }, ' · inactive')), el('td', {}, p.type), el('td', {}, p.town), el('td', {}, p.mobile || p.phone),
        el('td', {}, p.gstin), el('td', { class: 'n' }, money(Math.abs(p.balance)) + (p.balance >= 0 ? ' Dr' : ' Cr'))))))));
  };
  const search = el('input', { placeholder: 'Search name, town, mobile or GSTIN  ·  Enter opens the top one', 'data-rows': '' });
  search.addEventListener('input', debounce(() => show(search.value.trim()), 150));
  const pick = el('select', { onchange: (e) => (location.hash = `#/accounts?${e.target.value}`) },
    [['all', 'All parties'], ['Customer', 'Customers'], ['Supplier', 'Suppliers']].map(([k, t]) => el('option', { value: k, selected: k === filter }, t)));
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, 'Customers & suppliers'),
      el('div', { class: 'row' }, el('a', { class: 'btn primary', href: '#/accounts/new/Customer' }, '+ New customer'), el('a', { class: 'btn', href: '#/accounts/new/Supplier' }, '+ New supplier'))),
    el('div', { class: 'panel row' }, pick, el('div', { class: 'grow' }, search), el('span', { class: 'muted' }, `${parties.length} accounts`)), out);
  show('');
  search.focus();
}

const GST_TYPES = [[2, 'Unregistered'], [0, 'Registered (regular)'], [1, 'Composition'], [3, 'Consumer']];

export async function accountForm(root, id, type = 'Customer') {
  const states = await get(`/${firmId()}/masters/accounts/states`);
  const a = id ? await get(`/${firmId()}/masters/accounts/${id}`)
    : { name: '', type, address1: '', address2: '', town: '', district: '', pin: '', phone: '', mobile: '', email: '', person: '', gstType: 2, gstin: '', pan: '',
        state: '37-Andhra Pradesh', openingBalance: '', openingSide: type === 'Customer' ? 'D' : 'C', billWise: false, active: true };
  const request = () => ({ ...a, accountId: id ?? undefined, openingBalance: String(a.openingBalance || 0), gstType: Number(a.gstType) });
  const f = masterForm({
    previewUrl: `/${firmId()}/masters/accounts/preview`, saveUrl: `/${firmId()}/masters/accounts`, request, wrap: (account) => ({ account }),
    title: (r) => `${r.name.trim()} (${r.code})`,
    onSaved: (r) => { location.hash = `#/accounts/${r.accountId}`; },
    render: (p) => [el('div', {}, el('strong', {}, 'Code '), p.code), el('div', {}, el('strong', {}, 'State '), p.state)],
  });
  const s = f.schedule;
  const text = (key, max, attrs = {}) => bind(el('input', { maxlength: max, ...attrs }), a, key, s);
  const typePick = el('select', {}, ['Customer', 'Supplier'].map((t) => el('option', { value: t, selected: a.type === t }, t)));
  typePick.addEventListener('change', () => { a.type = typePick.value; s(); });
  const gstType = el('select', {}, GST_TYPES.map(([k, t]) => el('option', { value: k, selected: Number(a.gstType) === k }, t)));
  const gstin = text('gstin', 15, { style: { textTransform: 'uppercase' } });
  const statePick = el('select', {}, states.map((x) => el('option', { value: x, selected: a.state === x }, x)));
  const syncGst = () => {
    const registered = Number(a.gstType) <= 1;
    gstin.disabled = !registered;
    statePick.disabled = registered;
    if (!registered) { a.gstin = ''; gstin.value = ''; }
  };
  gstType.addEventListener('change', () => { a.gstType = Number(gstType.value); syncGst(); s(); });
  statePick.addEventListener('change', () => { a.state = statePick.value; s(); });
  const side = el('select', {}, [['D', 'Dr (they owe us)'], ['C', 'Cr (we owe them)']].map(([k, t]) => el('option', { value: k, selected: a.openingSide === k }, t)));
  side.addEventListener('change', () => { a.openingSide = side.value; s(); });
  const check = (key, label) => { const x = el('input', { type: 'checkbox', checked: a[key] }); x.addEventListener('change', () => { a[key] = x.checked; s(); }); return el('label', { class: 'row' }, x, el('span', {}, label)); };
  syncGst();
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, id ? `${a.type}: ${a.name.trim()}` : `New ${type.toLowerCase()}`),
      el('div', { class: 'row' }, el('a', { class: 'btn small', href: '#/accounts' }, 'All parties'), id ? el('a', { class: 'btn small', href: `#/ledger/${id}` }, 'Ledger') : null,
        el('button', { class: 'btn primary', onclick: f.save }, 'Save to HQ (F10)'))),
    el('div', { class: 'bill-layout', style: { marginTop: '12px' } },
      el('div', { class: 'panel stack' },
        el('div', { class: 'row' }, el('div', { class: 'grow' }, field('Name', text('name', 100, { autofocus: true }))), field('Type', typePick)),
        el('div', { class: 'row' }, el('div', { class: 'grow' }, field('Address line 1', text('address1', 60))), el('div', { class: 'grow' }, field('Address line 2', text('address2', 60)))),
        el('div', { class: 'row' }, field('Town', text('town', 25)), field('District', text('district', 25)), field('PIN', text('pin', 10, { inputmode: 'numeric' }))),
        el('div', { class: 'row' }, field('Mobile', text('mobile', 12, { inputmode: 'tel' })), field('Phone', text('phone', 30)), field('Email', text('email', 60)), field('Contact person', text('person', 50))),
        el('div', { class: 'row' }, field('GST registration', gstType), field('GSTIN', gstin), field('State', statePick, 'From the GSTIN when registered'), field('PAN', text('pan', 10))),
        el('div', { class: 'row' }, field('Opening balance (1-Apr)', bind(el('input', { class: 'num', inputmode: 'decimal' }), a, 'openingBalance', s, oneDecimal)), field('Side', side)),
        check('billWise', 'Bill-wise: settle receipts and payments against individual bills'),
        check('active', 'Active')),
      el('div', { class: 'panel totals stack' }, f.status)));
  f.refresh();
}
