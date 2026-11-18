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
  [/^#?\/?$/, (m, v) => dashboardView(v)],
  [/^#\/new\/(sale|salesOrder|purchase|purchaseOrder)$/, (m, v) => billView(v, { kind: m[1] })],
  [/^#\/edit\/(sale|salesOrder|purchase|purchaseOrder)\/(\d+)$/, (m, v) => billView(v, { kind: m[1], amendId: Number(m[2]) })],
  [/^#\/from-order\/(\d+)$/, (m, v) => billView(v, { kind: 'sale', fromOrder: Number(m[1]) })],
  [/^#\/draft\/([\w-]+)$/, async (m, v) => { const d = await get(`/${state.firm.id}/drafts/${m[1]}`); if (v.isConnected) return billView(v, { kind: d.kind, draft: d }); }],
  [/^#\/voucher\/(cashReceipt|bankReceipt|cashPayment|bankPayment|creditNote|debitNote)$/, (m, v) => voucherView(v, m[1])],
  [/^#\/edit-voucher\/(\d+)\/(\d+)$/, (m, v) => voucherView(v, voucherKindOfTt(Number(m[1])), { editId: Number(m[2]) })],
  [/^#\/cleanup(?:\/(\w+))?$/, (m, v) => cleanupView(v, m[1])],
  [/^#\/items$/, (m, v) => itemsView(v)],
  [/^#\/items\/new$/, (m, v) => itemForm(v, null)],
  [/^#\/items\/(\d+)$/, (m, v) => itemForm(v, Number(m[1]))],
  [/^#\/accounts(?:\?(all|Customer|Supplier))?$/, (m, v) => accountsView(v, m[1] ?? 'all')],
  [/^#\/accounts\/new\/(Customer|Supplier)$/, (m, v) => accountForm(v, null, m[1])],
  [/^#\/accounts\/(\d+)$/, (m, v) => accountForm(v, Number(m[1]))],
  [/^#\/documents$/, (m, v) => documentsView(v)],
  [/^#\/doc\/(\d+)$/, (m, v) => documentView(v, Number(m[1]))],
  [/^#\/ledger(?:\/(\d+))?$/, (m, v) => ledgerView(v, m[1] ? Number(m[1]) : null)],
  [/^#\/outstanding(?:\/(receivable|payable))?$/, (m, v) => outstandingView(v, m[1] ?? 'receivable')],
  [/^#\/reports$/, (m, v) => reportsView(v)],
  [/^#\/stock$/, (m, v) => stockView(v)],
  [/^#\/drafts$/, (m, v) => draftsView(v)],
  [/^#\/health$/, (m, v) => healthView(v)],
  [/^#\/capture$/, (m, v) => captureView(v)],
  [/^#\/settings$/, (m, v) => settingsView(v)],
];

async function route() {
  const hash = location.hash || '#/';
  for (const a of document.querySelectorAll('#nav a')) a.classList.toggle('active', hash === a.getAttribute('href') || (a.getAttribute('href') !== '#/' && hash.startsWith(a.getAttribute('href'))));
  const match = routes.map(([re, fn]) => [hash.match(re), fn]).find(([m]) => m);
  const fresh = el('main', { id: 'view', tabindex: '-1' }, el('p', { class: 'muted' }, 'Loading…'));
  view.replaceWith(fresh);
  view = fresh;
  // Each handler gets its own <main>: after an await, the module-level `view` may already be a newer screen.
  const mine = fresh;
  try {
    if (!match) { location.hash = '#/'; return; }
    await match[1](match[0], mine);
  } catch (e) {
    // A slow screen that fails after the operator moved on must not overwrite the screen now shown.
    if (mine.isConnected) mine.replaceChildren(el('h1', {}, 'Something went wrong'), el('p', { class: 'issue err' }, e.message), el('p', {}, el('a', { href: '#/' }, 'Back to dashboard')));
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
    // Lists (dashboard, bills, outstanding, stock, ledger) subscribe to this and refresh their figures in place, keeping
    // the search text, dates and row the operator is on; editors keep what he typed. Re-routing here used to rebuild
    // the whole screen mid-keystroke whenever HQ saved anything.
    notify('changed');
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
  // The screen is about to be replaced without a hashchange: let a half-made bill keep itself in Drafts (under its own firm).
  window.dispatchEvent(new Event('mk:leave'));
  state.catalogue = { items: [], parties: [], stamp: null };
  const chosen = state.firm.id;
  // A failed catalogue load still opens the firm (lists and reports work; the live link retries it on the next change),
  // and says so, instead of leaving the previous firm's screen up under the new firm's colours.
  try { await loadCatalogue(true); } catch (e) { if (state.firm.id === chosen) toast(`Items and parties could not be loaded: ${e.message}`, true); }
  if (state.firm.id !== chosen) return; // the operator picked another firm meanwhile; that selection finishes the job
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
    if (!link || e.ctrlKey || e.altKey) return;
    e.preventDefault();
    // Not under an open dialog (e.g. "Saved" or "Save changes?"): the dialog would then act on a screen no longer shown.
    if (document.querySelector('.modal-back')) return;
    location.hash = link.getAttribute('href');
  });
  document.addEventListener('keydown', rowKeys);
  initAssistant({ selectFirm });
  await selectFirm(pick.value);
}

// Lists of bills, accounts and items are worked from the keyboard: ↓ from a search box (or from nowhere) enters the
// list, ↑/↓ move, Enter opens. A search box marked data-rows also opens the top row on Enter.
function rowKeys(e) {
  if (e.ctrlKey || e.altKey || document.querySelector('.modal-back') || document.querySelector('#view[data-own-keys]')) return;
  const rows = () => [...document.querySelectorAll('#view tr.click')];
  const go = (tr) => { if (!tr) return; tr.tabIndex = -1; tr.focus(); tr.scrollIntoView({ block: 'nearest' }); };
  const t = e.target;
  // A button or box inside a row (Drafts' Discard) keeps its own Enter: it must not open the row instead.
  const row = t.matches?.('button, a, input, select, textarea') ? null : t.closest?.('tr.click');
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
