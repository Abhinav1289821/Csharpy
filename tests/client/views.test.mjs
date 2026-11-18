import './setup.mjs';
import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { live, keepPlace } from '../../src/MK.Hq.Api/wwwroot/js/views.js';
import { state, notify } from '../../src/MK.Hq.Api/wwwroot/js/state.js';

afterEach(() => { state.listeners.clear(); globalThis.document.activeElement = null; });

// A list screen's <main>, reduced to what live()/keepPlace() look at.
const fakeRoot = (rows = [], scrolls = []) => ({
  isConnected: true,
  rows,
  scrolls,
  contains(n) { return this.rows.includes(n); },
  querySelectorAll(sel) { return sel === 'tr.click' ? this.rows : sel === '.scroll' ? this.scrolls : []; },
});
const fakeRow = () => {
  const r = { isConnected: true, tabIndex: 0, matches: (s) => s === 'tr.click', focus() { document.activeElement = r; r.focused = true; } };
  return r;
};
const tick = () => new Promise((r) => setTimeout(r, 0));

describe('live list refresh (HQ saved something)', () => {
  test('refreshes the screen in place instead of re-routing it', async () => {
    const root = fakeRoot();
    let refreshed = 0;
    live(root, async () => { refreshed++; });
    notify('changed');
    await tick();
    assert.equal(refreshed, 1);
    notify('catalogue'); // only HQ changes refresh the list
    await tick();
    assert.equal(refreshed, 1);
  });

  test('retires when the screen is gone', async () => {
    const root = fakeRoot();
    let refreshed = 0;
    live(root, async () => { refreshed++; });
    root.isConnected = false;
    notify('changed');
    await tick();
    assert.equal(refreshed, 0);
    assert.equal(state.listeners.size, 0);
  });

  test('a failed background refresh is not an unhandled rejection', async () => {
    const root = fakeRoot();
    live(root, async () => { throw new Error('offline'); });
    let unhandled = null;
    const onUnhandled = (e) => { unhandled = e; };
    process.on('unhandledRejection', onUnhandled);
    try {
      notify('changed');
      await tick();
      await tick();
    } finally { process.off('unhandledRejection', onUnhandled); }
    assert.equal(unhandled, null);
  });
});

describe('keepPlace', () => {
  test('puts focus back on the same row when the refresh rebuilt the table', async () => {
    const before = [fakeRow(), fakeRow(), fakeRow()];
    const root = fakeRoot(before, [{ scrollTop: 240 }]);
    document.activeElement = before[1];
    await keepPlace(root, async () => {
      root.rows = [fakeRow(), fakeRow(), fakeRow()]; // new rows, old ones gone
      root.scrolls = [{ scrollTop: 0 }];
      document.activeElement = document.body;
    });
    assert.equal(root.rows[1].focused, true);
    assert.equal(root.scrolls[0].scrollTop, 240);
  });

  test('a shorter list keeps focus on its last row', async () => {
    const before = [fakeRow(), fakeRow(), fakeRow()];
    const root = fakeRoot(before);
    document.activeElement = before[2];
    await keepPlace(root, async () => { root.rows = [fakeRow()]; document.activeElement = document.body; });
    assert.equal(root.rows[0].focused, true);
  });

  test('does not steal focus from a box he moved to meanwhile', async () => {
    const before = [fakeRow(), fakeRow()];
    const root = fakeRoot(before);
    document.activeElement = before[0];
    const searchBox = { isConnected: true };
    await keepPlace(root, async () => { root.rows = [fakeRow(), fakeRow()]; document.activeElement = searchBox; });
    assert.equal(root.rows[0].focused, undefined);
    assert.equal(document.activeElement, searchBox);
  });
});
