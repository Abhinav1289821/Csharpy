// Small DOM and formatting helpers. No framework, no build step.

// Views pass optional parts as `cond ? node : null` and nested arrays; let replaceChildren skip/flatten them
// exactly like el() does, instead of printing "null".
const nativeReplace = Element.prototype.replaceChildren;
Element.prototype.replaceChildren = function (...kids) {
  return nativeReplace.apply(this, kids.flat(Infinity).filter((k) => k !== null && k !== undefined && k !== false));
};

export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') node.className = v;
    else if (k === 'style' && typeof v === 'object') Object.assign(node.style, v);
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'value') node.value = v;
    else if (v === true) node.setAttribute(k, '');
    else node.setAttribute(k, v);
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return node;
}

export const $ = (sel, root = document) => root.querySelector(sel);

const inr = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const inrQty = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 4 });
// A sum like 0.1 + 0.2 - 0.3 is -0.00000000000000005: it must read "0.00", not "-0.00" (a minus on a nil balance
// or a nil difference from the printed total makes the operator look twice).
const noNegativeZero = (s) => s.replace(/^-(?=[0.,]*$)/, '');
export const money = (v) => (v === null || v === undefined || v === '' ? '' : noNegativeZero(inr.format(Number(v))));
export const qty = (v) => (v === null || v === undefined || v === '' ? '' : noNegativeZero(inrQty.format(Number(v))));
export const today = () => {
  const d = new Date();
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
};
export const dmy = (iso) => (iso ? iso.slice(8, 10) + '-' + iso.slice(5, 7) + '-' + iso.slice(0, 4) : '');
export const addDays = (iso, n) => {
  const d = new Date(iso + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};

/** Number inputs: digits and the first decimal point only ("1..5" would otherwise read as not-a-number and be dropped). */
export const oneDecimal = (v) => {
  const s = String(v).replace(/[^0-9.]/g, '');
  const dot = s.indexOf('.');
  return dot < 0 ? s : s.slice(0, dot + 1) + s.slice(dot + 1).replace(/\./g, '');
};

/**
 * Only the newest of several overlapping requests may land on screen. `const ticket = newest()`; in each load:
 * `const current = ticket(); const r = await get(...); if (!current()) return;` — a slower answer to an older search
 * (or older dates) is then dropped instead of overwriting the newer one.
 */
export function newest() {
  let n = 0;
  return () => {
    const mine = ++n;
    return () => mine === n;
  };
}

export function debounce(fn, ms) {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}

export function toast(text, error = false) {
  const box = el('div', { class: 't' + (error ? ' err' : '') }, text);
  $('#toast').append(box);
  setTimeout(() => box.remove(), error ? 9000 : 4000);
}

/** Opens a modal; resolves with the value passed to close(). */
export function modal(build) {
  return new Promise((resolve) => {
    const root = $('#modal-root');
    const close = (value) => {
      back.remove();
      document.removeEventListener('keydown', onKey, true);
      resolve(value);
    };
    const onKey = (e) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        close(undefined);
        return;
      }
      // Buttons marked data-key="p" answer to that letter (when not typing in a box).
      if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement?.tagName ?? '')) {
        const b = back.querySelector(`button[data-key="${e.key.toLowerCase()}"]`);
        if (b) { e.preventDefault(); e.stopPropagation(); b.click(); }
      }
    };
    const back = el('div', { class: 'modal-back', onmousedown: (e) => e.target === back && close(undefined) }, el('div', { class: 'modal' }, build(close)));
    root.append(back);
    document.addEventListener('keydown', onKey, true);
    back.querySelector('button.primary, input, select')?.focus();
  });
}

export async function confirmBox(title, body, okText = 'Confirm', danger = false) {
  return modal((close) => [
    el('h1', {}, title),
    el('div', { class: 'stack' }, body),
    el('div', { class: 'row', style: { justifyContent: 'flex-end', marginTop: '14px' } },
      el('button', { class: 'btn', onclick: () => close(false) }, 'Cancel'),
      el('button', { class: 'btn ' + (danger ? 'danger' : 'primary'), onclick: () => close(true) }, okText)),
  ]);
}

/**
 * Keyboard lookup: type to filter, ↑/↓ to move, Enter/Tab to choose, Esc to close.
 * search(text) returns [{ label, hint, value }]. onPick(value) is called on choice.
 */
export function lookup({ placeholder = '', value = '', search, onPick, onInput, autofocus = false, className = '', explicit = false }) {
  const input = el('input', { placeholder, value, autocomplete: 'off', spellcheck: 'false', class: className });
  const list = el('div', { class: 'lookup-list', hidden: true });
  const wrap = el('div', { class: 'lookup' }, input, list);
  let items = [];
  // explicit: nothing is highlighted until ↓ is pressed, so Enter/Tab keep the typed text instead of picking a match.
  const start = explicit ? -1 : 0;
  let sel = start;
  const render = () => {
    list.replaceChildren(...items.map((it, i) => el('div', { class: i === sel ? 'sel' : '', onmousedown: (e) => { e.preventDefault(); choose(i); } },
      el('span', {}, it.label), it.hint ? el('small', {}, it.hint) : null)));
    list.hidden = items.length === 0;
    list.children[sel]?.scrollIntoView({ block: 'nearest' });
  };
  const choose = (i) => {
    const it = items[i];
    if (!it) return;
    items = [];
    render();
    onPick(it.value, input);
  };
  input.addEventListener('input', () => {
    onInput?.(input.value);
    items = input.value.trim() ? search(input.value) : [];
    sel = start;
    render();
  });
  input.addEventListener('focus', () => input.select());
  input.addEventListener('blur', () => setTimeout(() => { items = []; render(); }, 120));
  input.addEventListener('keydown', (e) => {
    if (list.hidden) return;
    if (e.key === 'ArrowDown') { sel = Math.min(sel + 1, items.length - 1); render(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { sel = Math.max(sel - 1, start); render(); e.preventDefault(); }
    else if (e.key === 'Enter' || e.key === 'Tab') {
      if (items.length && sel >= 0) { e.preventDefault(); choose(sel); }
      else { items = []; render(); }
    }
    else if (e.key === 'Escape') { items = []; render(); e.stopPropagation(); }
  });
  if (autofocus) setTimeout(() => input.focus(), 0);
  return { wrap, input };
}

// The searchable text of an entry, cached per field list: the parties screen searches different fields from the bill's
// party lookup, so one shared cache on the entry would make whichever ran first decide what the other can find
// (a landline number not found in the bill after the parties screen was searched). Pass a module-level `fields`
// function to benefit from the cache; an inline arrow is simply recomputed.
const hays = new WeakMap();
const hayOf = (e, fields) => {
  let cache = hays.get(fields);
  if (!cache) hays.set(fields, (cache = new WeakMap()));
  let hay = cache.get(e);
  if (hay === undefined) cache.set(e, (hay = fields(e).join(' ').toLowerCase()));
  return hay;
};

/** Rank catalogue entries: every typed word must appear; names that start with the words come first. */
export function rank(entries, text, fields, limit = 30) {
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return [];
  const out = [];
  for (const e of entries) {
    const hay = hayOf(e, fields);
    let score = 0;
    let ok = true;
    for (const w of words) {
      const at = hay.indexOf(w);
      if (at < 0) { ok = false; break; }
      // Judge the best place the word appears, not the first: "tap" in "stoptap tap" starts a word.
      score += at === 0 ? 0 : hay[at - 1] === ' ' || hay.includes(' ' + w) ? 1 : 3;
    }
    if (ok) out.push([score + hay.length / 1000, e]);
  }
  return out.sort((a, b) => a[0] - b[0]).slice(0, limit).map((x) => x[1]);
}

/** The CSV text (Excel-ready: CRLF rows, quoted where needed). Separate from csv() so it can be checked without a browser. */
export function csvText(rows, columns) {
  const esc = (v) => {
    let s = v === null || v === undefined ? '' : String(v);
    // A party name or narration typed as "=..." / "+..." / "@..." would run as a formula when the accountant opens the
    // file in Excel: keep it text. Numbers (a negative balance) are left as numbers.
    if (typeof v === 'string' && (/^[=+@\t\r]/.test(s) || (s.startsWith('-') && !Number.isFinite(Number(s))))) s = "'" + s;
    return /[",\r\n]/.test(s) ? '"' + s.replaceAll('"', '""') + '"' : s;
  };
  return [columns.map((c) => esc(c.title)).join(','), ...rows.map((r) => columns.map((c) => esc(typeof c.get === 'function' ? c.get(r) : r[c.get])).join(','))].join('\r\n');
}

export function csv(rows, columns, filename) {
  const text = csvText(rows, columns);
  const a = el('a', { href: URL.createObjectURL(new Blob(['﻿' + text], { type: 'text/csv' })), download: filename });
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}
