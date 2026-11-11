// Shell: firm selection, hash router, function-key navigation and the live link to HQ.
import { initToken, get, eventsUrl } from './api.js';
import { el, $, toast, dmy } from './util.js';
import { state, loadCatalogue, notify } from './state.js';
import { billView } from './bill.js';
import { voucherView, voucherKindOfTt } from './voucher.js';
import { itemsView, itemForm, accountsView, accountForm } from './masters.js';
import { cleanupView } from './cleanup.js';
import { captureView, settingsView } from './capture.js';
import { dashboardView, documentsView, documentView, ledgerView, outstandingView, reportsView, stockView, draftsView } from './views.js';
import { healthView } from './health.js';
import { initAssistant } from './assistant.js';

// Each screen renders into its own <main>. Replacing it on every navigation disconnects the previous screen, so its
// key/paste handlers and timers (which check root.isConnected) retire instead of acting on a screen no longer shown.
let view = $('#view');
let events = null;

const routes = [
  [/^#?\/?$/, () => dashboardView(view)],
  [/^#\/new\/(sale|salesOrder|purchase|purchaseOrder)$/, (m) => billView(view, { kind: m[1] })],
  [/^#\/edit\/(sale|salesOrder|purchase|purchaseOrder)\/(\d+)$/, (m) => billView(view, { kind: m[1], amendId: Number(m[2]) })],
  [/^#\/from-order\/(\d+)$/, (m) => billView(view, { kind: 'sale', fromOrder: Number(m[1]) })],
  [/^#\/draft\/([\w-]+)$/, async (m) => { const d = await get(`/${state.firm.id}/drafts/${m[1]}`); return billView(view, { kind: d.kind, draft: d }); }],
  [/^#\/voucher\/(cashReceipt|bankReceipt|cashPayment|bankPayment|creditNote|debitNote)$/, (m) => voucherView(view, m[1])],
  [/^#\/edit-voucher\/(\d+)\/(\d+)$/, (m) => voucherView(view, voucherKindOfTt(Number(m[1])), { editId: Number(m[2]) })],
  [/^#\/cleanup(?:\/(\w+))?$/, (m) => cleanupView(view, m[1])],
  [/^#\/items$/, () => itemsView(view)],
  [/^#\/items\/new$/, () => itemForm(view, null)],
  [/^#\/items\/(\d+)$/, (m) => itemForm(view, Number(m[1]))],
  [/^#\/accounts(?:\?(all|Customer|Supplier))?$/, (m) => accountsView(view, m[1] ?? 'all')],
  [/^#\/accounts\/new\/(Customer|Supplier)$/, (m) => accountForm(view, null, m[1])],
  [/^#\/accounts\/(\d+)$/, (m) => accountForm(view, Number(m[1]))],
  [/^#\/documents$/, () => documentsView(view)],
  [/^#\/doc\/(\d+)$/, (m) => documentView(view, Number(m[1]))],
  [/^#\/ledger(?:\/(\d+))?$/, (m) => ledgerView(view, m[1] ? Number(m[1]) : null)],
  [/^#\/outstanding(?:\/(receivable|payable))?$/, (m) => outstandingView(view, m[1] ?? 'receivable')],
  [/^#\/reports$/, () => reportsView(view)],
  [/^#\/stock$/, () => stockView(view)],
  [/^#\/drafts$/, () => draftsView(view)],
  [/^#\/health$/, () => healthView(view)],
  [/^#\/capture$/, () => captureView(view)],
  [/^#\/settings$/, () => settingsView(view)],
];

async function route() {
  const hash = location.hash || '#/';
  for (const a of document.querySelectorAll('#nav a')) a.classList.toggle('active', hash === a.getAttribute('href') || (a.getAttribute('href') !== '#/' && hash.startsWith(a.getAttribute('href'))));
  const match = routes.map(([re, fn]) => [hash.match(re), fn]).find(([m]) => m);
  const fresh = el('main', { id: 'view', tabindex: '-1' }, el('p', { class: 'muted' }, 'Loading…'));
  view.replaceWith(fresh);
  view = fresh;
  try {
    if (!match) { location.hash = '#/'; return; }
    await match[1](match[0]);
  } catch (e) {
    view.replaceChildren(el('h1', {}, 'Something went wrong'), el('p', { class: 'issue err' }, e.message), el('p', {}, el('a', { href: '#/' }, 'Back to dashboard')));
  }
}

function connectLive() {
  events?.close();
  const live = $('#live');
  const text = $('#live-text');
  events = new EventSource(eventsUrl(state.firm.id));
  events.onopen = () => { live.className = 'live on'; text.textContent = 'Live with HQ'; };
  events.addEventListener('changed', async () => {
    await loadCatalogue().catch(() => {});
    notify('changed');
    // Lists refresh themselves when HQ (or another MK window) saves; editors keep what you typed.
    if (/^#\/?$|^#\/(documents|outstanding|stock)/.test(location.hash || '#/')) route();
  });
  events.addEventListener('offline', () => { live.className = 'live off'; text.textContent = 'HQ database offline'; });
  events.onerror = () => { live.className = 'live off'; text.textContent = 'Reconnecting…'; };
}

async function selectFirm(id) {
  state.firm = state.firms.find((f) => f.id === id) ?? state.firms[0];
  try { localStorage.setItem('mk-hq-firm', state.firm.id); } catch { /* ignore */ }
  $('#firm-period').textContent = `${dmy(state.firm.periodStart)} – ${dmy(state.firm.periodEnd)}`;
  // Each firm has its own colour across the whole screen, so a bill is never keyed into the wrong firm.
  document.body.dataset.firm = state.firm.id;
  $('#firm').value = state.firm.id;
  for (const b of document.querySelectorAll('#firm-tabs button')) b.setAttribute('aria-selected', String(b.dataset.id === state.firm.id));
  state.catalogue = { items: [], parties: [], stamp: null };
  await loadCatalogue(true);
  connectLive();
  if (state.firm.contract && !state.firm.contract.writesAllowed) toast(state.firm.contract.summary, true);
  route();
}

async function start() {
  if (!initToken()) {
    view.replaceChildren(el('h1', {}, 'MK Billing'), el('p', {}, 'Open MK Billing from its desktop launcher; it brings the access key.'));
    return;
  }
  try {
    state.firms = (await get('/firms')).filter((f) => f.online);
  } catch (e) {
    view.replaceChildren(el('h1', {}, 'Cannot reach MK Billing'), el('p', { class: 'issue err' }, e.message));
    return;
  }
  if (!state.firms.length) { view.replaceChildren(el('h1', {}, 'No HQ firm database is reachable'), el('p', {}, 'Check that SQL Server is running.')); return; }
  const pick = $('#firm');
  pick.replaceChildren(...state.firms.map((f) => el('option', { value: f.id }, f.name)));
  let saved = null;
  try { saved = localStorage.getItem('mk-hq-firm'); } catch { /* ignore */ }
  pick.value = state.firms.some((f) => f.id === saved) ? saved : state.firms[0].id;
  pick.addEventListener('change', () => selectFirm(pick.value));
  $('#firm-tabs').replaceChildren(...state.firms.map((f) => el('button', { type: 'button', role: 'tab', 'data-id': f.id, class: 'firm-tab',
    onclick: () => { if (state.firm?.id !== f.id) selectFirm(f.id); } }, f.name)));
  $('#today').textContent = new Date().toLocaleDateString('en-IN', { weekday: 'short', day: '2-digit', month: 'short', year: 'numeric' });
  window.addEventListener('hashchange', route);
  document.addEventListener('keydown', (e) => {
    const link = document.querySelector(`#nav a[data-key="${e.key}"]`);
    if (link && !e.ctrlKey && !e.altKey) { e.preventDefault(); location.hash = link.getAttribute('href'); }
  });
  document.addEventListener('keydown', rowKeys);
  initAssistant({ selectFirm });
  await selectFirm(pick.value);
}

// Lists of bills, accounts and items are worked from the keyboard: ↓ from a search box (or from nowhere) enters the
// list, ↑/↓ move, Enter opens. A search box marked data-rows also opens the top row on Enter.
function rowKeys(e) {
  if (e.ctrlKey || e.altKey || document.querySelector('.modal-back')) return;
  const rows = () => [...document.querySelectorAll('#view tr.click')];
  const go = (tr) => { if (!tr) return; tr.tabIndex = -1; tr.focus(); tr.scrollIntoView({ block: 'nearest' }); };
  const t = e.target;
  const row = t.closest?.('tr.click');
  if (row) {
    const list = rows();
    const i = list.indexOf(row);
    if (e.key === 'Enter') { e.preventDefault(); row.click(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); go(list[i + 1]); }
    else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (i > 0) go(list[i - 1]); else document.querySelector('#view [data-rows]')?.focus();
    }
    return;
  }
  const searchBox = t.matches?.('[data-rows]');
  const idle = t === document.body || t.id === 'view';
  if ((searchBox || idle) && e.key === 'ArrowDown') { e.preventDefault(); go(rows()[0]); }
  else if (searchBox && e.key === 'Enter') { const first = rows()[0]; if (first) { e.preventDefault(); first.click(); } }
}

start();
