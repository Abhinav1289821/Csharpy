// Guided item clean-up for the shop owner: one question at a time, answers saved as he goes, nothing in HQ until he
// presses "Write to HQ".
import { get, post } from './api.js';
import { el, money, dmy, toast, confirmBox, newest } from './util.js';
import { firmId, loadCatalogue, state } from './state.js';

const api = (p) => `/${firmId()}/cleanup${p}`;
const n = (v) => Number(v).toLocaleString('en-IN');
const STEPS = {
  automatic: { title: 'Spelling, capitals and spaces', what: (s) => `${n(s.total)} fixes. No questions: you said these need no approval.` },
  rules: { title: 'Formatting rules', what: (s) => s.open ? `${s.open} question${s.open === 1 ? '' : 's'}, one per rule` : 'All answered' },
  review: { title: 'Names to decide', what: (s) => s.open ? `${n(s.open)} of ${n(s.total)} items still to look at` : `All ${n(s.total)} decided` },
  retire: { title: 'Items not used for a year', what: (s) => s.open ? `${n(s.open)} items: no sale, no purchase, no stock` : `All ${n(s.total)} decided` },
  data: { title: 'GST and HSN to check', what: (s) => s.open ? `${n(s.open)} items still to look at` : `All ${n(s.total)} decided` },
  groups: { title: 'Items in the wrong group', what: (s) => s.open ? `${n(s.open)} items` : `All ${n(s.total)} decided` },
};

// A key handler that retires with its screen (the router replaces <main> on every page; we check our own marker too).
// It retires only once its marker has been on the page: a key pressed while the card is still being built (the
// look-alikes are fetched first) used to remove the new card's handler for good, leaving its one-key answers dead.
// Register it after the marker is placed; the `seen` check is the safety net.
export function onKeys(marker, handler, doc = document) {
  let seen = false;
  const fn = (e) => {
    if (marker.isConnected) seen = true;
    else { if (seen) doc.removeEventListener('keydown', fn); return; }
    if (e.ctrlKey || e.altKey || doc.querySelector?.('.modal-back')) return;
    if (e.target.matches?.('input, textarea, select') && e.key !== 'Enter' && e.key !== 'Escape') return;
    handler(e);
  };
  doc.addEventListener('keydown', fn);
  return fn;
}

/**
 * One answer per card or question: the first key or click sends it, and every further key/click on the same card is
 * ignored until it fails (then he may answer again). Without this a quick second Enter (or Enter then N) answered the
 * item still on screen a second time, while the next one was loading.
 */
export function oneAnswer(onError = (e) => toast(e.message, true)) {
  let busy = false;
  const run = async (fn) => {
    if (busy) return false;
    busy = true;
    try { await fn(); return true; } catch (e) { busy = false; onError(e); return false; }
  };
  run.busy = () => busy;
  return run;
}

const reassure = () => el('p', { class: 'muted' }, 'Your answers are saved as you go. Nothing changes in HQ until the last step, "Write to HQ".');
const back = () => el('a', { class: 'btn', href: '#/cleanup' }, '← All steps');
const facts = (p) => el('div', { class: 'cu-facts' },
  el('span', {}, `Group: ${p.group || '—'}`),
  el('span', {}, p.sold12 ? `Sold ${p.sold12}× this year` : 'Not sold this year'),
  el('span', {}, `Stock ${Number(p.stock).toLocaleString('en-IN')}`),
  p.lastSale ? el('span', {}, `Last sold ${dmy(p.lastSale)}`) : null,
  p.saleRate ? el('span', {}, `Rate ₹${money(p.saleRate)}`) : null);

// ------------------------------------------------------------------ home

export async function cleanupView(root, step) {
  if (step === 'rules') return rulesStep(root);
  if (step === 'review') return reviewStep(root);
  if (step === 'retire') return retireStep(root);
  if (step === 'data') return dataStep(root);
  if (step === 'groups') return groupsStep(root);
  if (step === 'automatic') return automaticStep(root);
  if (step === 'apply') return applyStep(root);
  const s = await get(api(''));
  const loader = () => {
    const input = el('input', { type: 'file', accept: '.json,application/json', style: { display: 'none' } });
    input.addEventListener('change', async () => {
      const file = input.files?.[0];
      if (!file) return;
      try {
        await post(api('/import'), JSON.parse(await file.text()));
        toast('List loaded');
        await cleanupView(root);
      } catch (e) { toast(e.message, true); }
    });
    return [input, el('button', { class: s.items ? 'btn small' : 'btn primary', onclick: () => input.click() }, s.items ? 'Load a fresh list' : 'Load the list (proposals file)')];
  };
  if (!s.items) {
    root.replaceChildren(el('h1', {}, 'Tidy item names'),
      el('div', { class: 'panel stack', style: { maxWidth: '760px' } },
        el('p', {}, `Load the proposals file for ${state.firm.name} (proposals-${state.firm.id.toUpperCase()}.json, made by tools/item-cleanup/propose.py).`),
        el('div', {}, ...loader())));
    return;
  }
  const byKey = Object.fromEntries(s.stages.map((x) => [x.key, x]));
  const card = (key) => {
    const st = byKey[key];
    if (!st || !st.total) return null;
    const done = key === 'automatic' || !st.open;
    return el('a', { class: 'panel cu-step' + (done ? ' done' : ''), href: `#/cleanup/${key}` },
      el('div', { class: 'cu-tick' }, done ? '✓' : ''), el('div', { class: 'grow' }, el('div', { class: 'cu-step-title' }, STEPS[key].title), el('div', { class: 'muted' }, STEPS[key].what(st))),
      el('span', { class: 'btn small' + (done ? '' : ' primary') }, done ? 'Look' : 'Start'));
  };
  root.replaceChildren(
    el('div', { class: 'row spread' }, el('h1', {}, 'Tidy item names'), el('div', { class: 'row' }, ...loader())),
    el('p', { class: 'muted' }, `${state.firm.name} · list made ${s.generated ? dmy(s.generated.slice(0, 10)) : ''} · ${n(s.items)} items`),
    reassure(),
    el('div', { class: 'stack', style: { maxWidth: '760px' } },
      ...['automatic', 'rules', 'review', 'retire', 'data', 'groups'].map(card),
      el('a', { class: 'panel cu-step cu-apply', href: '#/cleanup/apply' }, el('div', { class: 'cu-tick' }, '→'),
        el('div', { class: 'grow' }, el('div', { class: 'cu-step-title' }, 'Write to HQ'),
          el('div', { class: 'muted' }, s.readyToApply ? `${n(s.readyToApply)} changes ready` : 'Nothing waiting'),
          s.applied ? el('div', { class: 'muted' }, `Already written: ${n(s.applied)}${s.skipped ? ` · left alone: ${n(s.skipped)}` : ''}${s.failed ? ` · could not be saved: ${n(s.failed)}` : ''}`) : null),
        el('span', { class: 'btn small primary' }, 'Open'))));
}

// ------------------------------------------------------------------ automatic

async function automaticStep(root) {
  const items = await get(api('/items?stage=automatic&take=60'));
  const s = await get(api(''));
  const total = s.stages.find((x) => x.key === 'automatic')?.total ?? 0;
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, STEPS.automatic.title), back()),
    el('p', {}, `${n(total)} names get only spelling, capitals or spaces fixed (and HEAVY for ISI). You said these need no approval, so there is nothing to answer. Here are the ones that sell most:`),
    reassure(),
    el('div', { class: 'panel' }, el('table', { class: 'grid cu-table' }, el('tbody', {}, items.map((i) => {
      const keep = el('button', { class: 'btn small' }, i.decision?.keepName ? 'Kept as is' : 'Leave as is');
      keep.addEventListener('click', async () => {
        keep.disabled = true;
        try { await post(api('/decide'), [{ itemId: i.proposal.id, decision: { keepName: true } }]); keep.textContent = 'Kept as is'; }
        catch (e) { keep.disabled = false; toast(e.message, true); }
      });
      const same = i.proposal.proposed === i.proposal.current;
      return el('tr', {}, el('td', { class: 'muted' }, i.proposal.current), el('td', {}, '→'),
        el('td', { class: 'cu-new' }, same ? el('span', { class: 'muted' }, 'extra space before or after the name removed') : i.proposal.proposed),
        el('td', { class: 'n muted' }, i.proposal.sold12 ? `${i.proposal.sold12}×` : ''), el('td', {}, keep));
    })))));
}

// ------------------------------------------------------------------ rules (one question per rule)

async function rulesStep(root) {
  const s = await get(api(''));
  if (!root.isConnected) return; // he left while it loaded: no keys for a question nobody sees
  const open = s.rules.filter((r) => !r.decision);
  const rule = open[0];
  if (!rule) {
    root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, STEPS.rules.title), back()), el('p', {}, 'All formatting rules are answered.'),
      el('div', { class: 'panel stack' }, s.rules.map((r) => el('div', { class: 'row spread' }, el('div', {}, el('strong', {}, r.title), el('span', { class: 'muted' }, ` · ${n(r.items)} items`)),
        el('div', { class: 'row' }, el('span', {}, r.decision === 'yes' ? '✓ Yes' : '✗ No'), el('button', { class: 'btn small', onclick: async () => {
          try { await post(api('/rules'), { rule: r.rule, decision: null }); await rulesStep(root); } catch (e) { toast(e.message, true); }
        } }, 'Change'))))));
    return;
  }
  // One answer per question: Enter then N (or Enter twice) while the next question loads must not answer this rule twice.
  const once = oneAnswer();
  const answer = (decision) => once(async () => {
    await post(api('/rules'), { rule: rule.rule, decision });
    // The answer is saved; a failure to load the next question is shown, but must not re-open this one.
    rulesStep(root).catch((e) => toast(e.message, true));
  });
  const marker = el('span');
  const all = el('div');
  const showAll = async () => {
    let items;
    try { items = await get(api(`/items?stage=rule:${encodeURIComponent(rule.rule)}&take=3000`)); } catch (e) { toast(e.message, true); return; }
    all.replaceChildren(el('div', { class: 'panel scroll', style: { maxHeight: '50vh' } }, el('table', { class: 'grid cu-table' }, el('tbody', {}, items.map((i) =>
      el('tr', {}, el('td', { class: 'muted' }, i.proposal.current), el('td', {}, '→'), el('td', { class: 'cu-new' }, i.proposal.proposed)))))));
  };
  const asked = s.rules.length - open.length + 1;
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, `Question ${asked} of ${s.rules.length}`), back()), reassure(), marker,
    el('div', { class: 'panel stack cu-card' },
      el('div', { class: 'cu-question' }, rule.title),
      el('div', { class: 'muted' }, `Changes ${n(rule.items)} names. Examples (your best sellers):`),
      el('table', { class: 'grid cu-table' }, el('tbody', {}, rule.examples.map((x) => el('tr', {}, el('td', { class: 'muted' }, x.before), el('td', {}, '→'), el('td', { class: 'cu-new' }, x.after))))),
      el('div', { class: 'row cu-buttons' },
        el('button', { class: 'btn primary big', onclick: () => answer('yes') }, `Yes, for all ${n(rule.items)}  (Enter)`),
        el('button', { class: 'btn big', onclick: () => answer('no') }, 'No, keep as written  (N)'),
        el('button', { class: 'btn', onclick: showAll }, 'Show all')),
      el('p', { class: 'muted' }, 'If you say no, these names still get their spelling, capitals and spaces fixed.')), all);
  onKeys(marker, (e) => {
    if (e.key === 'Enter') { e.preventDefault(); answer('yes'); }
    else if (e.key.toLowerCase() === 'n') { e.preventDefault(); answer('no'); }
  });
}

// ------------------------------------------------------------------ one card at a time

async function cardFlow(root, { stage, heading, answered, render }) {
  const items = await get(api(`/items?stage=${stage}&take=3000`));
  let skipped = new Set();
  const total = items.length;
  const next = () => items.find((i) => !answered(i) && !skipped.has(i.proposal.id));
  const ticket = newest(); // only the newest card may land (a slow look-alike fetch must not replace a later card)
  const show = async () => {
    const current = ticket();
    const item = next();
    const done = items.filter(answered).length;
    if (!item) {
      root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, heading), back()),
        el('div', { class: 'panel stack cu-card' }, el('div', { class: 'cu-question' }, skipped.size ? `Done for now. ${skipped.size} skipped for later.` : 'All done here. Thank you.'),
          el('div', { class: 'row cu-buttons' }, skipped.size ? el('button', { class: 'btn', onclick: () => { skipped = new Set(); show(); } }, 'Look at the skipped ones') : null,
            el('a', { class: 'btn primary big', href: '#/cleanup' }, 'Back to all steps'))));
      return;
    }
    const marker = el('span');
    // This card answers once: until the next card is on screen, further keys and clicks on it do nothing.
    const once = oneAnswer();
    // `others`: decisions on other items sent in the same call (the look-alikes retired with "keep this one").
    const save = (decision, others = []) => once(async () => {
      await post(api('/decide'), [...others, { itemId: item.proposal.id, decision }]);
      item.decision = { ...(item.decision ?? {}), ...decision };
      show();
    });
    const skip = () => once(async () => { skipped.add(item.proposal.id); show(); });
    let built;
    try {
      built = await render(item, save, skip);
    } catch (e) {
      if (!current() || !root.isConnected) return;
      root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, heading), back()),
        el('div', { class: 'issue err' }, e.message), el('div', { class: 'row cu-buttons' }, el('button', { class: 'btn primary', onclick: () => show() }, 'Try again')));
      return;
    }
    if (!current() || !root.isConnected) return;
    root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, heading), back()),
      el('div', { class: 'cu-progress' }, el('div', { style: { width: `${Math.round(100 * done / Math.max(1, total))}%` } })),
      el('p', { class: 'muted' }, `${done} of ${total} done · Enter = yes · S = skip for now`), marker, built.body);
    // Keys only once this card is on the page (the previous card's handler retires as its marker leaves).
    onKeys(marker, built.keys);
    // No automatic focus in the text box: the one-key answers (K, R, S, D…) must work straight away; he clicks into the box
    // only to type his own wording, and Enter then saves it.
    document.activeElement?.blur?.();
  };
  show();
}

const PLAIN = {
  'another item in this group would get the same name': 'Another item in the same group would get this same name. HQ cannot have both: keep one, retire the other.',
  'looks like a duplicate of another item in this group': 'Looks like the same item is entered twice in this group.',
  'size in inches only': 'The name has only the inch size.',
  'ends with a bare number': 'The name ends with a number without a unit. Is it the inch size?',
  "MM size added from the shop's own items of this size": 'The MM size was added, taken from your other items of the same size.',
};
const plain = (note) => Object.entries(PLAIN).find(([k]) => note.startsWith(k))?.[1] ?? note;

async function reviewStep(root) {
  await cardFlow(root, {
    stage: 'review', heading: STEPS.review.title,
    answered: (i) => !!(i.decision && (i.decision.name || i.decision.keepName)),
    render: async (item, save, skip) => {
      const p = item.proposal;
      const name = el('input', { class: 'cu-name cu-focus', value: item.decision?.name ?? p.proposed, maxlength: 70 });
      const twins = p.duplicateOf?.length ? await get(api(`/items?stage=review&ids=${p.duplicateOf.join(',')}`)) : [];
      const use = () => save({ name: name.value.trim() || p.proposed });
      // The usual answer for a duplicate: keep this one, retire the look-alikes that hold no stock. One call, so the
      // look-alikes are never retired while this item's own answer fails to save.
      const spare = twins.filter((t) => Number(t.proposal.stock) <= 0 && t.proposal.active && t.status !== 'applied');
      const keepThis = () => {
        if (!spare.length) return;
        save({ name: name.value.trim() || p.proposed }, spare.map((t) => ({ itemId: t.proposal.id, decision: { keepName: true, retire: true } })));
      };
      const keys = (e) => {
        const k = e.key.toLowerCase();
        if (e.key === 'Enter') { e.preventDefault(); use(); }
        else if (k === 'd' && spare.length) { e.preventDefault(); keepThis(); }
        else if (k === 'k') { e.preventDefault(); save({ keepName: true }); }
        else if (k === 'r') { e.preventDefault(); save({ keepName: true, retire: true }); }
        else if (k === 's') { e.preventDefault(); skip(); }
      };
      return { keys, body: el('div', { class: 'panel stack cu-card' },
        el('div', { class: 'muted' }, 'Now called'), el('div', { class: 'cu-old' }, p.current),
        el('div', { class: 'muted' }, 'New name (you can change it)'), name,
        ...p.notes.map((x) => el('div', { class: 'issue warn' }, plain(x))),
        facts(p),
        twins.length ? el('div', { class: 'stack' }, el('strong', {}, twins.length > 1 ? 'The look-alike items:' : 'The look-alike item:'), twins.map((t) => el('div', { class: 'cu-twin' },
          el('div', { class: 'cu-new' }, t.proposal.proposed ?? t.proposal.current), facts(t.proposal),
          Number(t.proposal.stock) > 0 ? el('div', { class: 'muted' }, 'Has stock: move its stock to the item you keep (in HQ) before retiring it.') : null))) : null,
        el('div', { class: 'row cu-buttons' },
          el('button', { class: 'btn primary big', onclick: use }, 'Use this name  (Enter)'),
          spare.length ? el('button', { class: 'btn big', onclick: keepThis }, `Keep this, retire ${spare.length === twins.length ? 'the look-alike' + (spare.length > 1 ? 's' : '') : spare.length + ' without stock'}  (D)`) : null,
          el('button', { class: 'btn big', onclick: () => save({ keepName: true }) }, 'Keep the old name  (K)'),
          el('button', { class: 'btn', onclick: () => save({ keepName: true, retire: true }) }, 'Retire this item  (R)'),
          el('button', { class: 'btn', onclick: skip }, 'Skip for now  (S)'))) };
    },
  });
}

async function dataStep(root) {
  const rates = (await get(`/${firmId()}/masters/items/options`)).gstRates;
  await cardFlow(root, {
    stage: 'data', heading: STEPS.data.title,
    answered: (i) => !!(i.decision && (i.decision.hsn || i.decision.gst != null || i.decision.dataLeave)),
    render: async (item, save, skip) => {
      const p = item.proposal;
      const hsn = el('input', { class: 'cu-name cu-focus', value: item.decision?.hsn ?? p.hsnSuggest ?? p.hsn ?? '', inputmode: 'numeric', maxlength: 8, style: { maxWidth: '220px' } });
      const gst = el('select', { class: 'cu-select' }, rates.map((r) => el('option', { value: r, selected: Number(r) === Number(item.decision?.gst ?? p.gst) }, `${r}%`)));
      const fix = () => {
        const d = {};
        if (hsn.value.trim() && hsn.value.trim() !== p.hsn) d.hsn = hsn.value.trim();
        if (Number(gst.value) !== Number(p.gst)) d.gst = Number(gst.value);
        save(Object.keys(d).length ? d : { dataLeave: true });
      };
      const keys = (e) => {
        const k = e.key.toLowerCase();
        if (e.key === 'Enter') { e.preventDefault(); fix(); }
        else if (k === 'l') { e.preventDefault(); save({ dataLeave: true }); }
        else if (k === 's') { e.preventDefault(); skip(); }
      };
      return { keys, body: el('div', { class: 'panel stack cu-card' },
        el('div', { class: 'cu-new' }, p.proposed ?? p.current),
        el('div', { class: 'issue warn' }, p.dataCheck),
        facts(p),
        el('div', { class: 'row' }, el('label', { class: 'field' }, el('span', {}, p.hsnSuggest ? `HSN (suggested ${p.hsnSuggest}, from similar items)` : 'HSN'), hsn),
          el('label', { class: 'field' }, el('span', {}, 'GST'), gst)),
        el('div', { class: 'row cu-buttons' },
          el('button', { class: 'btn primary big', onclick: fix }, 'Save this  (Enter)'),
          el('button', { class: 'btn big', onclick: () => save({ dataLeave: true }) }, 'Leave it as it is  (L)'),
          el('button', { class: 'btn', onclick: skip }, 'Skip for now  (S)'))) };
    },
  });
}

async function groupsStep(root) {
  const options = await get(`/${firmId()}/masters/items/options`);
  const byName = Object.fromEntries(options.groups.map((g) => [g.name.toUpperCase(), g.id]));
  await cardFlow(root, {
    stage: 'groups', heading: STEPS.groups.title,
    answered: (i) => !!(i.decision && (i.decision.groupId || i.decision.groupLeave)),
    render: async (item, save, skip) => {
      const p = item.proposal;
      const target = byName[(p.suggestGroup ?? '').toUpperCase()];
      const move = () => target ? save({ groupId: target }) : toast('That group is not in HQ', true);
      const keys = (e) => {
        const k = e.key.toLowerCase();
        if (e.key === 'Enter') { e.preventDefault(); move(); }
        else if (k === 'n') { e.preventDefault(); save({ groupLeave: true }); }
        else if (k === 's') { e.preventDefault(); skip(); }
      };
      return { keys, body: el('div', { class: 'panel stack cu-card' },
        el('div', { class: 'cu-new' }, p.proposed ?? p.current),
        el('div', { class: 'cu-question' }, `It is in "${p.group}". Items like it are in "${p.suggestGroup}". Move it?`),
        facts(p),
        el('div', { class: 'row cu-buttons' },
          el('button', { class: 'btn primary big', onclick: move }, `Move to ${p.suggestGroup}  (Enter)`),
          el('button', { class: 'btn big', onclick: () => save({ groupLeave: true }) }, 'Leave it  (N)'),
          el('button', { class: 'btn', onclick: skip }, 'Skip for now  (S)'))) };
    },
  });
}

// ------------------------------------------------------------------ retire (one list, everything ticked)

async function retireStep(root) {
  const items = await get(api('/items?stage=retire&take=3000'));
  const ticked = new Map(items.map((i) => [i.proposal.id, i.decision?.retire ?? true]));
  const count = el('strong');
  const update = () => { count.textContent = n([...ticked.values()].filter(Boolean).length); };
  const groups = [...new Set(items.map((i) => i.proposal.group))];
  const table = el('div', { class: 'panel scroll', style: { maxHeight: '58vh' } }, el('table', { class: 'grid cu-table' }, el('tbody', {}, groups.flatMap((g) => [
    el('tr', {}, el('th', { colspan: 4 }, g)),
    ...items.filter((i) => i.proposal.group === g).map((i) => {
      const box = el('input', { type: 'checkbox', checked: ticked.get(i.proposal.id) });
      box.addEventListener('change', () => { ticked.set(i.proposal.id, box.checked); update(); });
      return el('tr', {}, el('td', {}, box), el('td', {}, i.proposal.proposed ?? i.proposal.current),
        el('td', { class: 'muted' }, i.proposal.lastSale ? `last sold ${dmy(i.proposal.lastSale)}` : 'never sold'),
        el('td', { class: 'muted' }, i.proposal.lastPurchase ? `last bought ${dmy(i.proposal.lastPurchase)}` : ''));
    })]))));
  const all = (v) => { for (const k of ticked.keys()) ticked.set(k, v); table.querySelectorAll('input[type=checkbox]').forEach((b) => (b.checked = v)); update(); };
  const saveAll = async () => {
    try {
      await post(api('/decide'), [...ticked].map(([itemId, retire]) => ({ itemId, decision: { retire } })));
      toast('Saved');
      location.hash = '#/cleanup';
    } catch (e) { toast(e.message, true); }
  };
  update();
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, STEPS.retire.title), back()),
    el('p', {}, 'These were not sold or bought for a year and have no stock. Retiring hides them from bills in HQ; their history stays and you can switch them back on any time. Untick anything you still want to sell.'),
    reassure(),
    el('div', { class: 'row', style: { margin: '8px 0' } }, el('button', { class: 'btn primary big', onclick: saveAll }, 'Retire the ticked ', count),
      el('button', { class: 'btn', onclick: () => all(true) }, 'Tick all'), el('button', { class: 'btn', onclick: () => all(false) }, 'Untick all')),
    table);
}

// ------------------------------------------------------------------ write to HQ

async function applyStep(root) {
  const s = await get(api(''));
  const pending = await get(api('/items?stage=pending&take=3000'));
  const kinds = { name: 0, retire: 0, group: 0, hsn: 0, gst: 0 };
  for (const i of pending) {
    const c = i.pending;
    if (c.name) kinds.name++;
    if (c.active === false) kinds.retire++;
    if (c.groupId) kinds.group++;
    if (c.hsn) kinds.hsn++;
    if (c.gst != null) kinds.gst++;
  }
  const out = el('div', { class: 'stack' });
  const run = async (path, body, label) => {
    // While writing, nothing else on this page can be pressed (a second run would race the first).
    root.querySelectorAll('button').forEach((b) => { b.disabled = true; });
    let batchId = body.batchId ?? null;
    const bar = el('div', { style: { width: '0%' } });
    const line = el('div', { class: 'muted' }, 'Starting…');
    const notes = el('div', { class: 'stack' });
    out.replaceChildren(el('div', { class: 'cu-progress big' }, bar), line, notes);
    let first = null;
    // The whole run belongs to the firm it started in: a firm switch midway must not send the next batch to the other
    // firm's HQ (unconfirmed), and leaving the page stops it.
    const url = api(path);
    const startedIn = firmId();
    for (;;) {
      if (firmId() !== startedIn || !out.isConnected) { toast('Writing stopped: you left the clean-up page. Open it again to continue.', true); return; }
      let r;
      try { r = await post(url, { ...body, batchId, take: 20 }); }
      catch (e) { line.textContent = e.message; toast(e.message, true); return; }
      batchId = r.batchId;
      first ??= r.remaining + r.applied + r.skipped + r.failed;
      bar.style.width = `${Math.round(100 * (first - r.remaining) / Math.max(1, first))}%`;
      line.textContent = `${label}: ${n(r.applied)} done${r.skipped ? ` · ${n(r.skipped)} left alone (changed in HQ meanwhile)` : ''}${r.failed ? ` · ${n(r.failed)} could not be saved` : ''} · ${n(r.remaining)} to go`;
      for (const m of r.messages) notes.append(el('div', { class: 'issue warn' }, m));
      if (!r.remaining || (!r.applied && !r.messages.length)) break;
    }
    await loadCatalogue().catch(() => {});
    out.append(el('div', { class: 'row cu-buttons' }, el('a', { class: 'btn primary big', href: '#/cleanup' }, 'Back to all steps')));
  };
  const write = async () => {
    const ok = await confirmBox(`Write ${n(pending.length)} changes to HQ?`,
      [el('p', {}, 'Each item is saved exactly as HQ\'s own Items screen saves it. Items someone changed in HQ meanwhile are left alone.')], 'Write to HQ');
    if (ok) run('/apply', {}, 'Written');
  };
  root.replaceChildren(el('div', { class: 'row spread' }, el('h1', {}, 'Write to HQ'), back()),
    el('div', { class: 'panel stack cu-card' },
      pending.length ? el('div', { class: 'cu-question' }, `${n(pending.length)} items are ready`) : el('div', { class: 'cu-question' }, 'Nothing is waiting to be written.'),
      pending.length ? el('ul', {}, kinds.name ? el('li', {}, `${n(kinds.name)} new names`) : null, kinds.retire ? el('li', {}, `${n(kinds.retire)} retired (hidden from bills)`) : null,
        kinds.group ? el('li', {}, `${n(kinds.group)} moved to another group`) : null, kinds.hsn ? el('li', {}, `${n(kinds.hsn)} HSN codes`) : null, kinds.gst ? el('li', {}, `${n(kinds.gst)} GST rates`) : null) : null,
      pending.length ? el('p', { class: 'muted' }, 'Questions you have not answered yet are simply left for later; only answered ones are written.') : null,
      pending.length ? el('div', { class: 'row cu-buttons' }, el('button', { class: 'btn primary big', onclick: write }, `Write ${n(pending.length)} changes to HQ`)) : null),
    out,
    s.batches.filter((b) => b.kind === 'apply' && b.applied).length ? el('div', { class: 'stack' }, el('h2', {}, 'Earlier batches'),
      s.batches.filter((b) => b.kind === 'apply' && b.applied).map((b) => el('div', { class: 'panel' },
        `${dmy(b.createdAt.slice(0, 10))} ${b.createdAt.slice(11, 16)} · ${n(b.applied)} written${b.skipped ? ` · ${n(b.skipped)} left alone` : ''}`))) : null);
}
