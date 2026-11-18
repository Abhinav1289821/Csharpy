import './setup.mjs';
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { onKeys, oneAnswer } from '../../src/MK.Hq.Api/wwwroot/js/cleanup.js';

const fakeDoc = () => {
  const listeners = new Set();
  return {
    listeners,
    modal: false,
    addEventListener: (t, fn) => listeners.add(fn),
    removeEventListener: (t, fn) => listeners.delete(fn),
    querySelector(sel) { return sel === '.modal-back' && this.modal ? {} : null; },
    press(key, extra = {}) {
      const e = { key, target: { matches: () => !!extra.inBox }, ctrlKey: !!extra.ctrl, altKey: !!extra.alt, preventDefault() {} };
      for (const fn of [...listeners]) fn(e);
    },
  };
};

describe('onKeys (one-key answers on a clean-up card)', () => {
  test('a key pressed while the card is still being built does not kill its handler', () => {
    const doc = fakeDoc();
    const marker = { isConnected: false };
    const keys = [];
    onKeys(marker, (e) => keys.push(e.key), doc);
    doc.press('k'); // the look-alikes are still loading: the card is not on the page yet
    assert.equal(doc.listeners.size, 1, 'handler must survive a key pressed before its card is shown');
    assert.deepEqual(keys, []);
    marker.isConnected = true; // the card lands
    doc.press('k');
    assert.deepEqual(keys, ['k']);
  });

  test('retires for good once its card has left the page', () => {
    const doc = fakeDoc();
    const marker = { isConnected: true };
    const keys = [];
    onKeys(marker, (e) => keys.push(e.key), doc);
    doc.press('r');
    marker.isConnected = false; // next card / another screen
    doc.press('r');
    assert.deepEqual(keys, ['r']);
    assert.equal(doc.listeners.size, 0);
  });

  test('typing in the name box only lets Enter and Escape through', () => {
    const doc = fakeDoc();
    const keys = [];
    onKeys({ isConnected: true }, (e) => keys.push(e.key), doc);
    doc.press('r', { inBox: true });
    doc.press('Enter', { inBox: true });
    assert.deepEqual(keys, ['Enter']);
  });

  test('Ctrl+R (reload) and Ctrl+S are not R = retire / S = skip; nothing under an open dialog', () => {
    const doc = fakeDoc();
    const keys = [];
    onKeys({ isConnected: true }, (e) => keys.push(e.key), doc);
    doc.press('r', { ctrl: true });
    doc.press('s', { ctrl: true });
    doc.press('a', { alt: true });
    doc.modal = true;
    doc.press('Enter');
    assert.deepEqual(keys, []);
  });
});

describe('oneAnswer (one answer per card)', () => {
  test('a second key while the first answer is being saved is ignored', async () => {
    const once = oneAnswer(() => {});
    const sent = [];
    let finish;
    const first = once(() => new Promise((r) => { sent.push('yes'); finish = r; }));
    const second = once(async () => { sent.push('no'); });
    assert.equal(await second, false);
    finish();
    assert.equal(await first, true);
    assert.deepEqual(sent, ['yes']);
  });

  test('after a saved answer the card stays answered (the next card has its own)', async () => {
    const once = oneAnswer(() => {});
    await once(async () => {});
    assert.equal(once.busy(), true);
    assert.equal(await once(async () => assert.fail('must not run')), false);
  });

  test('a failed save lets him answer again, and reports the error', async () => {
    const errors = [];
    const once = oneAnswer((e) => errors.push(e.message));
    assert.equal(await once(async () => { throw new Error('HQ offline'); }), false);
    assert.deepEqual(errors, ['HQ offline']);
    assert.equal(once.busy(), false);
    assert.equal(await once(async () => {}), true);
  });
});
