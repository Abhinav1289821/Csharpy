// Assistant: a conversation with Google Antigravity (agy, over ACP) docked beside every screen. The agent reads HQ
// through MK's own tools, moves this window to the screen it is talking about, and prepares bills as drafts that open
// in the normal editor. Nothing it does is saved in HQ until the user presses Save.
import { get, post, del, tokenQuery, ApiError } from './api.js';
import { el, $, toast } from './util.js';
import { state } from './state.js';

const OPEN_KEY = 'mk-hq-assistant-open';
const CHAT_KEY = 'mk-hq-assistant-chat';

let hooks = { selectFirm: async () => {} };
let panel, log, input, sendBtn, modelPick, statusLine, toggleBtn;
let chatId = null;
let source = null;
let busy = false;
let current = null; // the assistant message being streamed: { node, text }
let unloading = false; // a closing page's event stream errors too; that must not end the conversation
window.addEventListener('pagehide', () => { unloading = true; });
const tools = new Map(); // toolCallId -> row
let activeQuestion = null;

const store = {
  get: (k) => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k, v) => { try { v === null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { /* private mode */ } },
};

const LABELS = {
  list_firms: 'Firms', find_parties: 'Looking up parties', find_items: 'Looking up items', party_ledger: 'Reading ledger',
  outstanding: 'Reading outstanding', list_bills: 'Listing bills', get_bill: 'Reading bill', day_summary: "Reading the day's figures",
  day_book: 'Reading day book', stock: 'Reading stock', gst_summary: 'Reading GST register', db_tables: 'Checking HQ tables',
  db_query: 'Querying HQ', current_screen: 'Checking your screen', open_screen: 'Opening screen', prepare_bill: 'Preparing draft',
  ask_user: 'Waiting for your answer', shop_workflow: 'Checking shop rules', scan_bill: 'Scanning a page', scan_status: 'Checking the scan',
};

const SUGGESTIONS = [
  "Today's sales and cash in hand",
  'Who owes us the most? Show the top 10',
  'Items that went into negative stock',
  'Make an estimate for ',
  'Scan a purchase bill',
];

export function initAssistant({ selectFirm }) {
  hooks.selectFirm = selectFirm;
  toggleBtn = el('button', { type: 'button', class: 'assistant-toggle', title: 'Assistant (Alt+A)', onclick: () => toggle() }, 'Assistant');
  $('.letterhead-right').prepend(toggleBtn);

  modelPick = el('select', { class: 'as-model', 'aria-label': 'Model', hidden: true, onchange: () => setOption('model', modelPick.value) });
  statusLine = el('div', { class: 'as-status muted' });
  log = el('div', { class: 'as-log', 'aria-live': 'polite' });
  input = el('textarea', { rows: 2, placeholder: 'Ask, or tell it what to do…  (Enter to send)', onkeydown: onKey });
  sendBtn = el('button', { type: 'submit', class: 'btn primary' }, 'Send');
  panel = el('aside', { class: 'assistant', 'aria-label': 'Assistant', hidden: true },
    el('div', { class: 'as-head' },
      el('div', {}, el('div', { class: 'as-title' }, 'Assistant'), el('div', { class: 'as-sub' }, 'Google Antigravity')),
      el('div', { class: 'row' }, modelPick,
        el('button', { type: 'button', class: 'btn small', title: 'Start a new conversation', onclick: newChat }, 'New'),
        el('button', { type: 'button', class: 'btn small', title: 'Close (Alt+A)', 'aria-label': 'Close', onclick: () => toggle(false) }, '✕'))),
    log, statusLine,
    el('form', { class: 'as-compose', onsubmit: (e) => { e.preventDefault(); busy && !input.value.trim() ? stop() : send(); } }, input, sendBtn));
  $('.shell').append(panel);

  document.addEventListener('keydown', (e) => {
    if (e.altKey && !e.ctrlKey && (e.key === 'a' || e.key === 'A')) { e.preventDefault(); toggle(); }
  });
  window.addEventListener('hashchange', () => { if (chatId) post(`/agent/chats/${chatId}/screen`, screen()).catch(() => {}); });

  chatId = store.get(CHAT_KEY);
  if (chatId) connect(); else empty();
  if (store.get(OPEN_KEY) === '1') toggle(true);
}

function toggle(open = panel.hidden) {
  panel.hidden = !open;
  document.body.classList.toggle('assistant-open', open);
  toggleBtn.setAttribute('aria-pressed', String(open));
  store.set(OPEN_KEY, open ? '1' : '0');
  if (open) { input.focus(); scroll(); }
}

const screen = () => ({
  firm: state.firm?.id ?? null,
  hash: location.hash || '#/',
  title: ($('#view h1')?.textContent ?? document.title).trim().slice(0, 80),
});

function onKey(e) {
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  if (e.altKey && ['a', 'i'].includes(e.key.toLowerCase())) return; // Alt+A still closes the panel
  e.stopPropagation(); // typing here must not trigger the screens' F-keys or list keys
}

// ------------------------------------------------------------------ conversation
async function ensureChat() {
  if (chatId) return chatId;
  statusLine.textContent = 'Starting Antigravity…';
  try {
    const chat = await post('/agent/chats', screen());
    chatId = chat.id;
    store.set(CHAT_KEY, chatId);
    log.replaceChildren();
    connect();
    return chatId;
  } catch (e) {
    statusLine.textContent = '';
    problem(e);
    return null;
  } finally {
    if (statusLine.textContent === 'Starting Antigravity…') statusLine.textContent = '';
  }
}

async function send() {
  const text = input.value.trim();
  if (!text) return;
  input.value = '';
  if (activeQuestion) {
    try { await answerQuestion(activeQuestion, text); }
    catch (e) { input.value = text; toast(e.message, true); }
    return;
  }
  if (!(await ensureChat())) { input.value = text; return; }
  try {
    await post(`/agent/chats/${chatId}/prompt`, { text, ...screen() });
  } catch (e) {
    if (e instanceof ApiError && e.code === 'AGENT_CHAT_GONE') { forget(); input.value = text; return send(); }
    input.value = text;
    toast(e.message, true);
  }
}

async function stop() {
  if (chatId) await post(`/agent/chats/${chatId}/cancel`).catch(() => {});
}

async function newChat() {
  const old = chatId;
  forget();
  empty();
  if (old) del(`/agent/chats/${old}`).catch(() => {});
  input.focus();
}

function forget() {
  source?.close();
  source = null;
  chatId = null;
  store.set(CHAT_KEY, null);
  setBusy(false);
  current = null;
  tools.clear();
  activeQuestion = null;
}

async function setOption(configId, value) {
  if (!chatId) return;
  try { await post(`/agent/chats/${chatId}/option`, { configId, value }); toast('Model changed'); } catch (e) { toast(e.message, true); }
}

function empty() {
  log.replaceChildren(el('div', { class: 'as-empty' },
    el('p', {}, 'Ask about bills, parties, stock or GST, or tell it what to do. It looks things up in HQ, opens the right screen, and prepares bills as drafts. You check them and press F10 to save.'),
    el('div', { class: 'as-chips' }, SUGGESTIONS.map((s) => el('button', { type: 'button', class: 'btn small', onclick: () => {
      input.value = s; input.focus();
      if (!s.endsWith(' ')) send();
    } }, s.trim())))));
}

function problem(e) {
  const box = el('div', { class: 'as-problem' });
  if (e.code === 'AGENT_SIGN_IN') {
    box.append(el('p', {}, el('strong', {}, 'Sign in to Antigravity. '), 'This PC has no Antigravity sign-in yet, or it has expired.'),
      el('div', { class: 'row' },
        el('button', { class: 'btn primary small', onclick: async () => {
          try { await post('/agent/login'); toast('A sign-in window opened on this PC: sign in with Google, then press Retry'); } catch (x) { toast(x.message, true); }
        } }, 'Sign in…'),
        el('button', { class: 'btn small', onclick: () => { box.remove(); ensureChat(); } }, 'Retry')));
  } else if (e.code === 'AGENT_NOT_INSTALLED') {
    box.append(el('p', {}, el('strong', {}, 'Antigravity CLI is not installed on this PC. '), e.message));
  } else {
    box.append(el('p', { class: 'issue err' }, e.message));
  }
  log.append(box);
  scroll();
}

// ------------------------------------------------------------------ live events
function connect() {
  source?.close();
  log.replaceChildren();
  tools.clear();
  activeQuestion = null;
  current = null;
  const id = chatId;
  source = new EventSource(`/api/agent/chats/${id}/events?${tokenQuery()}`);
  source.onmessage = (m) => { if (chatId === id) apply(JSON.parse(m.data)); };
  source.onerror = async () => {
    if (unloading || chatId !== id) return;
    // EventSource retries by itself; only give the conversation up when the service no longer has it
    // (it restarted: conversations live in memory) or it was closed in another window.
    let known = true;
    try { known = (await get('/agent')).chats.some((c) => c.id === id); } catch { return; /* service down: keep retrying */ }
    if (known && source?.readyState === EventSource.CLOSED) { connect(); return; }
    if (!known && chatId === id) { forget(); empty(); }
  };
}

function apply(e) {
  const fresh = Date.now() - Date.parse(e.at) < 20000; // replayed history must not move the screen again
  switch (e.type) {
    case 'session': return options(e.options, e.tier);
    case 'options': return options(e.options);
    case 'user': endMessage(); return add(el('div', { class: 'as-msg user' }, el('div', { class: 'as-who' }, 'You'), el('div', { class: 'as-text' }, e.text)));
    case 'busy': return setBusy(e.busy);
    case 'done': endMessage(); if (e.stopReason === 'cancelled') add(el('div', { class: 'as-note muted' }, 'Stopped.')); return;
    case 'error': endMessage(); return add(el('div', { class: 'issue err' }, e.message));
    case 'notice': return add(el('div', { class: 'issue warn' }, e.text));
    case 'question': return questionCard(e, fresh);
    case 'question-closed': {
      const card = log.querySelector(`[data-question="${e.id}"]`);
      const hadFocus = card?.contains(document.activeElement);
      card?.querySelectorAll('button, input').forEach((control) => { control.disabled = true; });
      card?.querySelector('.as-answer').replaceChildren(e.answer ? `You answered: ${e.answer}` : 'Question closed');
      if (activeQuestion?.id === e.id) activeQuestion = null;
      if (hadFocus && !panel.hidden) input.focus();
      return;
    }
    case 'permission': return permission(e);
    case 'permission-closed': { const b = log.querySelector(`[data-permission="${e.id}"]`); b?.querySelectorAll('button').forEach((x) => (x.disabled = true)); return; }
    case 'ui': if (fresh && e.action === 'navigate') navigate(e.firm, e.hash); return;
    case 'update': return update(e.update);
    default: return;
  }
}

function update(u) {
  switch (u.sessionUpdate) {
    case 'agent_message_chunk':
      if (u.content?.type === 'text') stream(u.content.text);
      return;
    case 'agent_thought_chunk':
      statusLine.textContent = 'Thinking…';
      return;
    case 'tool_call':
    case 'tool_call_update':
      return toolRow(u);
    case 'plan':
      endMessage();
      return add(el('ol', { class: 'as-plan' }, (u.entries ?? []).map((p) => el('li', { class: 'st-' + p.status }, p.content))));
    case 'config_option_update':
      return options(u.configOptions);
    default:
      return;
  }
}

function stream(text) {
  if (!current) {
    const body = el('div', { class: 'as-text md' });
    current = { body, text: '' };
    add(el('div', { class: 'as-msg agent' }, el('div', { class: 'as-who' }, 'Assistant'), body));
  }
  current.text += text;
  statusLine.textContent = '';
  if (!current.queued) {
    current.queued = true;
    const c = current;
    // A timer, not requestAnimationFrame: frames stop while the window is in the background, and the shop often
    // switches to HQ while the assistant is still writing.
    setTimeout(() => { c.queued = false; c.body.replaceChildren(markdown(c.text)); scroll(); }, 40);
  }
}

function endMessage() { current = null; }

function toolRow(u) {
  const name = String(u.name ?? u.title ?? '').replace(/^Run\s+/, '').replace(/^mcp_mk_/, '');
  const args = u.rawInput ?? u.args ?? {};
  let row = tools.get(u.toolCallId);
  if (!row) {
    endMessage();
    const hint = args.query ?? args.search ?? args.screen ?? args.kind ?? '';
    row = el('div', { class: 'as-tool' }, el('span', { class: 'as-mark' }, '›'), el('span', {}, LABELS[name] ?? name), hint ? el('span', { class: 'muted' }, ' · ' + hint) : null);
    tools.set(u.toolCallId, row);
    add(row);
  }
  if (u.status) row.dataset.status = u.status;
  if (u.status === 'failed') {
    const msg = (u.content ?? []).map((c) => c?.content?.text).filter(Boolean).join(' ');
    if (msg && !row.querySelector('.as-fail')) row.append(el('div', { class: 'as-fail' }, msg));
  }
  statusLine.textContent = u.status === 'in_progress' || u.status === 'pending' ? (LABELS[name] ?? 'Working') + '…' : '';
}

async function answerQuestion(question, answer) {
  const value = answer?.trim();
  const match = /^([1-6])$/.exec(value ?? '');
  const choice = match ? question.choices?.[Number(match[1]) - 1] : null;
  await post(`/agent/chats/${chatId}/answer`, { id: question.id, answer: choice ?? value ?? null });
}

function questionCard(e, focus) {
  endMessage();
  activeQuestion = e;
  const field = el('input', { type: 'text', maxlength: 1000, autocomplete: 'off',
    'aria-label': 'Answer', placeholder: e.choices?.length ? 'Type an answer or choice number' : 'Type your answer' });
  const submit = async (answer) => {
    try { await answerQuestion(e, answer); }
    catch (error) { toast(error.message, true); }
  };
  const card = el('form', { class: 'as-permission as-question', 'data-question': e.id,
    onsubmit: (event) => { event.preventDefault(); if (field.value.trim()) submit(field.value); },
    onkeydown: (event) => { if (event.key === 'Escape') { event.stopPropagation(); submit(null); } } },
    el('p', {}, el('strong', {}, e.question)),
    el('div', { class: 'row' }, (e.choices ?? []).map((choice, i) =>
      el('button', { type: 'button', class: 'btn small', onclick: () => submit(choice) }, `${i + 1}. ${choice}`))),
    el('div', { class: 'row' }, field, el('button', { type: 'submit', class: 'btn small primary' }, 'Reply')),
    el('p', { class: 'as-answer muted', 'aria-live': 'polite' }));
  add(card);
  if (focus && !panel.hidden) field.focus();
}

function permission(e) {
  endMessage();
  add(el('div', { class: 'as-permission', 'data-permission': e.id },
    el('p', {}, el('strong', {}, 'Allow? '), e.title),
    el('div', { class: 'row' }, (e.options ?? []).map((o) => el('button', {
      class: 'btn small' + (o.kind?.startsWith('allow') ? ' primary' : ''),
      onclick: () => post(`/agent/chats/${chatId}/permission`, { id: e.id, optionId: o.optionId }).catch((x) => toast(x.message, true)),
    }, o.name)))));
}

function options(list, tier) {
  const model = (list ?? []).find((o) => o.id === 'model');
  if (!model) return;
  modelPick.replaceChildren(...model.options.map((o) => el('option', { value: o.value }, o.name)));
  modelPick.value = model.currentValue;
  modelPick.hidden = false;
  if (tier) panel.querySelector('.as-sub').textContent = 'Google Antigravity · ' + tier;
}

async function navigate(firm, hash) {
  if (firm && state.firm?.id !== firm) await hooks.selectFirm(firm);
  if (location.hash !== hash) location.hash = hash;
}

function setBusy(on) {
  busy = on;
  sendBtn.textContent = on ? 'Stop' : 'Send';
  sendBtn.classList.toggle('danger', on);
  sendBtn.classList.toggle('primary', !on);
  toggleBtn?.classList.toggle('working', on);
  if (!on) statusLine.textContent = '';
  else if (!statusLine.textContent) statusLine.textContent = 'Working…';
}

function add(node) {
  log.querySelector('.as-empty')?.remove();
  log.append(node);
  scroll();
}

function scroll() { log.scrollTop = log.scrollHeight; }

// ------------------------------------------------------------------ a small, safe markdown subset
function inline(text) {
  const out = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`)/g;
  let at = 0;
  for (const m of text.matchAll(re)) {
    if (m.index > at) out.push(text.slice(at, m.index));
    out.push(m[0].startsWith('**') ? el('strong', {}, m[0].slice(2, -2)) : el('code', {}, m[0].slice(1, -1)));
    at = m.index + m[0].length;
  }
  if (at < text.length) out.push(text.slice(at));
  return out;
}

function markdown(src) {
  const frag = document.createDocumentFragment();
  const lines = src.replace(/\r/g, '').split('\n');
  for (let i = 0; i < lines.length;) {
    const line = lines[i];
    if (/^\s*\|/.test(line)) {
      const rows = [];
      while (i < lines.length && /^\s*\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = (r) => r.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
      const body = rows.filter((r) => !/^\s*\|?\s*:?-{2,}/.test(r));
      const [head, ...rest] = body.map(cells);
      const numeric = (c) => /^[-₹\s\d,.%()]+$/.test(c) && /\d/.test(c);
      frag.append(el('div', { class: 'as-table' }, el('table', { class: 'grid' },
        head ? el('thead', {}, el('tr', {}, head.map((c) => el('th', {}, inline(c))))) : null,
        el('tbody', {}, rest.map((r) => el('tr', {}, r.map((c) => el('td', { class: numeric(c) ? 'num' : null }, inline(c)))))))));
      continue;
    }
    if (/^\s*([-*•]|\d+[.)])\s+/.test(line)) {
      const ordered = /^\s*\d/.test(line);
      const items = [];
      while (i < lines.length && /^\s*([-*•]|\d+[.)])\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*([-*•]|\d+[.)])\s+/, ''));
      frag.append(el(ordered ? 'ol' : 'ul', {}, items.map((t) => el('li', {}, inline(t)))));
      continue;
    }
    if (/^#{1,4}\s/.test(line)) { frag.append(el('p', { class: 'as-h' }, inline(line.replace(/^#+\s*/, '')))); i++; continue; }
    if (!line.trim()) { i++; continue; }
    const para = [];
    while (i < lines.length && lines[i].trim() && !/^\s*(\||[-*•]\s|\d+[.)]\s|#{1,4}\s)/.test(lines[i])) para.push(lines[i++]);
    if (!para.length) { frag.append(el('p', {}, inline(lines[i++]))); continue; }
    frag.append(el('p', {}, para.flatMap((p, k) => (k ? [el('br'), ...inline(p)] : inline(p)))));
  }
  return frag;
}
