// Photo capture: scanner files, webcam, phone (QR). The AI (Antigravity, or Gemma 4) reads each bill in the background; the result opens as a
// draft in the normal bill editor, beside the photo. Nothing reaches HQ until the user saves the bill.
import { get, post, del, api, tokenQuery } from './api.js';
import { el, toast, modal, confirmBox } from './util.js';
import { firmId } from './state.js';

const KINDS = [['purchase', 'Purchase bill'], ['salesOrder', 'Estimate / list'], ['sale', 'Sale']];
const STATUS = { received: ['Waiting', 'warn'], extracting: ['Reading…', 'warn'], ready: ['Ready to review', 'ok'], failed: ['Could not read', 'err'], saved: ['Saved in HQ', 'ok'] };

export const pageUrl = (id, page, v = '') => `/api/${firmId()}/captures/${id}/pages/${page}?${tokenQuery()}&v=${v}`;

async function upload(kind, file, captureId = null) {
  const path = captureId ? `/${firmId()}/captures/${captureId}/pages` : `/${firmId()}/captures?kind=${kind}`;
  const res = await fetch('/api' + path, { method: 'POST', body: file, headers: { Authorization: 'Bearer ' + tokenQuery().slice(6), 'Content-Type': file.type || 'application/octet-stream' } });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message ?? 'Upload failed');
  return data;
}

export async function captureView(root) {
  let kind = localStorage.getItem('mk-capture-kind') || 'purchase';
  let reader = 'antigravity';
  try { reader = (await get('/settings/ai')).reader ?? reader; } catch { /* keep the default wording */ }
  const readerName = reader === 'gemma' ? 'Gemma 4' : 'Antigravity';
  let timer = null;
  const list = el('div', { class: 'stack' });

  async function refresh() {
    if (!list.isConnected) { clearTimeout(timer); return; }
    let items;
    try { items = await get(`/${firmId()}/captures`); } catch (e) { list.replaceChildren(el('p', { class: 'issue err' }, e.message)); return; }
    list.replaceChildren(items.length ? items.map(card) : el('p', { class: 'muted' }, 'No photos yet. Add a bill photo above.'));
    clearTimeout(timer);
    timer = setTimeout(refresh, items.some((c) => c.status === 'received' || c.status === 'extracting') ? 2500 : 10000);
  }

  function card(c) {
    const [label, tone] = STATUS[c.status] ?? [c.status, ''];
    const act = (text, fn, cls = '') => el('button', { class: 'btn small ' + cls, onclick: async () => { try { await fn(); refresh(); } catch (e) { toast(e.message, true); } } }, text);
    const addPage = el('input', { type: 'file', accept: 'image/*', hidden: true, onchange: async (e) => {
      for (const f of e.target.files) await upload(kind, f, c.id);
      toast('Page added; reading the whole bill again');
      refresh();
    } });
    const busy = c.status === 'received' || c.status === 'extracting';
    return el('div', { class: 'panel row', style: { alignItems: 'flex-start' } },
      el('div', { class: 'row', style: { gap: '6px' } }, Array.from({ length: c.pages }, (_, i) =>
        el('a', { href: pageUrl(c.id, i + 1, c.updatedAt), target: '_blank', title: `Page ${i + 1}` },
          el('img', { src: pageUrl(c.id, i + 1, c.updatedAt), alt: `Page ${i + 1}`, style: { width: '92px', height: '120px', objectFit: 'cover', border: '1px solid var(--line)', background: '#fff' } })))),
      el('div', { class: 'grow stack', style: { gap: '6px' } },
        el('div', { class: 'row' }, el('strong', {}, KINDS.find((k) => k[0] === c.kind)?.[1] ?? c.kind), el('span', { class: 'pill ' + tone }, label),
          el('span', { class: 'muted' }, `${new Date(c.createdAt).toLocaleString('en-IN')} · ${c.source}${c.pages > 1 ? ' · ' + c.pages + ' pages' : ''}${c.elapsedMs ? ' · read in ' + Math.round(c.elapsedMs / 1000) + ' s' : ''}`)),
        c.summary ? el('div', {}, c.summary) : null,
        c.errorMessage ? el('div', { class: 'issue err' }, c.errorMessage) : null,
        c.reference ? el('div', {}, 'Saved as ', el('a', { href: `#/doc/${c.documentId}` }, c.reference)) : null,
        el('div', { class: 'row' },
          c.status === 'ready' && c.draftId ? el('a', { class: 'btn small primary', href: `#/draft/${c.draftId}` }, 'Review & save') : null,
          !busy && c.status !== 'saved' ? [
            act('↻ Turn right', () => post(`/${firmId()}/captures/${c.id}/pages/1/rotate?degrees=90`)),
            act('Add page', () => addPage.click()),
            act('Read again', () => post(`/${firmId()}/captures/${c.id}/retry`)),
            reader === 'gemma' ? act('Read with larger model', () => post(`/${firmId()}/captures/${c.id}/retry?model=gemma-4-31b-it`)) : null,
            act('Discard', async () => { if (await confirmBox('Discard this photo?', [el('p', {}, 'The photo and its draft are removed. Nothing in HQ changes.')], 'Discard', true)) await del(`/${firmId()}/captures/${c.id}`); }, 'danger'),
          ] : null, addPage)));
  }

  async function addFiles(files) {
    let n = 0;
    for (const f of files) {
      try { await upload(kind, f); n++; } catch (e) { toast(`${f.name}: ${e.message}`, true); }
    }
    if (n) toast(`${n} photo${n > 1 ? 's' : ''} sent to ${readerName}; each opens as a draft when read`);
    refresh();
  }

  const picker = el('input', { type: 'file', accept: 'image/*', multiple: true, hidden: true, onchange: (e) => { addFiles([...e.target.files]); e.target.value = ''; } });
  const drop = el('div', { class: 'panel', style: { border: '1.5px dashed var(--line)', textAlign: 'center', padding: '24px' },
    ondragover: (e) => { e.preventDefault(); drop.style.borderColor = 'var(--accent)'; },
    ondragleave: () => (drop.style.borderColor = 'var(--line)'),
    ondrop: (e) => { e.preventDefault(); drop.style.borderColor = 'var(--line)'; addFiles([...e.dataTransfer.files].filter((f) => f.type.startsWith('image/') || /\.(bmp|png|jpe?g|webp|tiff?)$/i.test(f.name))); } },
    el('div', { style: { fontSize: '16px' } }, 'Drop bill photos or scanner files here'),
    el('div', { class: 'muted' }, 'One bill per photo. JPEG, PNG, BMP or TIFF; sideways pages are turned upright automatically.'),
    el('div', { class: 'row', style: { justifyContent: 'center', marginTop: '10px' } },
      el('button', { class: 'btn primary', onclick: () => picker.click() }, 'Choose photos'),
      el('button', { class: 'btn', onclick: webcam }, 'Use camera'),
      el('button', { class: 'btn', onclick: phone }, 'Send from phone (QR)')));

  async function phone() {
    try {
      const p = await post(`/${firmId()}/captures/pair?kind=${kind}`);
      const holder = el('div', { style: { width: '260px', margin: '0 auto', background: '#fff', padding: '8px', borderRadius: '8px' } });
      holder.innerHTML = p.svg;
      await modal((close) => [el('h1', {}, 'Scan with the phone camera'), holder,
        el('p', { class: 'muted', style: { textAlign: 'center' } }, `Phone and PC must be on the same Wi-Fi. Link works for 20 minutes.`, el('br'), el('small', {}, p.url)),
        el('div', { class: 'row', style: { justifyContent: 'flex-end' } }, el('button', { class: 'btn primary', onclick: () => close() }, 'Done'))]);
      refresh();
    } catch (e) { toast(e.message, true); }
  }

  async function webcam() {
    let stream;
    try { stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment', width: { ideal: 2560 } } }); }
    catch { toast('No camera available on this PC', true); return; }
    const video = el('video', { autoplay: true, playsinline: true, style: { width: '100%', borderRadius: '8px', background: '#000' } });
    video.srcObject = stream;
    const shot = await modal((close) => [el('h1', {}, 'Hold the bill flat and steady'), video,
      el('div', { class: 'row', style: { justifyContent: 'flex-end', marginTop: '10px' } }, el('button', { class: 'btn', onclick: () => close() }, 'Cancel'),
        el('button', { class: 'btn primary', onclick: () => {
          const canvas = el('canvas', { width: video.videoWidth, height: video.videoHeight });
          canvas.getContext('2d').drawImage(video, 0, 0);
          canvas.toBlob((b) => close(b), 'image/jpeg', 0.92);
        } }, 'Take photo'))]);
    stream.getTracks().forEach((t) => t.stop());
    if (shot) addFiles([new File([shot], 'camera.jpg', { type: 'image/jpeg' })]);
  }

  root.replaceChildren(
    el('div', { class: 'row spread' }, el('div', {}, el('h1', {}, 'Scan bills'), el('div', { class: 'muted' }, `${readerName} reads the photo; you check it beside the picture and save. Nothing is saved to HQ automatically.`)),
      el('label', { class: 'field' }, el('span', {}, 'These photos are'), el('select', { onchange: (e) => { kind = e.target.value; localStorage.setItem('mk-capture-kind', kind); } },
        KINDS.map(([k, t]) => el('option', { value: k, selected: k === kind }, t))))),
    el('div', { style: { margin: '12px 0' } }, drop, picker), list);
  document.addEventListener('paste', function onPaste(e) {
    if (!list.isConnected) return document.removeEventListener('paste', onPaste);
    const files = [...(e.clipboardData?.files ?? [])].filter((f) => f.type.startsWith('image/'));
    if (files.length) addFiles(files);
  });
  refresh();
}

// ------------------------------------------------------------------ AI settings
export async function settingsView(root) {
  const s = await get('/settings/ai');
  const key = el('input', { type: 'password', placeholder: s.ai.keyConfigured ? `Saved key ${s.ai.keyHint} (leave blank to keep)` : 'Paste the Google AI Studio API key', style: { minWidth: '360px' } });
  const model = el('select', {}, s.ai.models.map((m) => el('option', { value: m, selected: m === s.ai.model }, m === 'gemma-4-26b-a4b-it' ? `${m} (fast, default)` : `${m} (larger, for hard handwriting)`)));
  const photos = s.reader === 'antigravity'
    ? [el('h2', {}, 'AI for bill photos (Google Antigravity)'),
      el('div', { class: 'panel' }, 'Bill photos are read by Google Antigravity with the same Google sign-in as the Assistant, so no API key is needed. ' +
        'If a photo says “not signed in”, open the Assistant (Alt+A), press Sign in, then press Read again on the photo.')]
    : [el('h2', {}, 'AI for bill photos (Google Gemma 4)'), el('div', { class: 'panel stack' },
      el('label', { class: 'field' }, el('span', {}, 'Gemini API key'), key),
      el('label', { class: 'field' }, el('span', {}, 'Model'), model),
      el('div', { class: 'muted' }, `Key ${s.ai.keyConfigured ? 'saved (' + s.ai.source + '), encrypted for this Windows user' : 'not set'}. It never leaves this PC except to Google.`),
      el('div', {}, el('button', { class: 'btn primary', onclick: async () => {
        try { await api('PUT', '/settings/ai', { apiKey: key.value || null, model: model.value }); toast('Saved'); settingsView(root); } catch (e) { toast(e.message, true); }
      } }, 'Save')))];
  root.replaceChildren(el('h1', {}, 'Settings'), ...photos,
    el('h2', {}, 'Phone uploads'),
    el('div', { class: 'panel' }, s.phone.enabled ? `Phones on the shop Wi-Fi reach this PC at ${s.phone.address}:${s.phone.port} (QR codes only; the phone page can upload photos and nothing else). If Windows asks, allow MK Billing on private networks.`
      : 'This PC has no LAN address, so phone upload is off. Connect it to the shop Wi-Fi/LAN and restart.'));
}
