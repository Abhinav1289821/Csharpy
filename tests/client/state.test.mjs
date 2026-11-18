import './setup.mjs';
import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { state, searchItems, searchParties, onChange, notify, loadCatalogue, item, party, firmId } from '../../src/MK.Hq.Api/wwwroot/js/state.js';

const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; state.listeners.clear(); });

const catalogue = (items, parties) => ({ items, parties, stamp: 'x' });

describe('catalogue search', () => {
  test('items: inactive ones are never offered on a bill', () => {
    state.catalogue = catalogue([
      { itemId: 1, unitId: 1, name: 'GI PIPE 1 INCH', code: '10', group: 'TATA', hsn: '7306', active: true },
      { itemId: 2, unitId: 1, name: 'GI PIPE 2 INCH', code: '11', group: 'TATA', hsn: '7306', active: false },
    ], []);
    assert.deepEqual(searchItems('pipe').map((i) => i.itemId), [1]);
    assert.deepEqual(searchItems('tata 7306').map((i) => i.itemId), [1]); // group and HSN are searched too
  });

  test('parties: found by name, town, mobile, landline, GSTIN or code; filter applies', () => {
    state.catalogue = catalogue([], [
      { accountId: 7, name: 'RAMU TRADERS', town: 'ONGOLE', mobile: '9848012345', phone: '08592-232323', gstin: '37ABCDE1234F1Z5', code: 'R1', active: true, isCustomer: true },
      { accountId: 8, name: 'RAMU STEELS', town: 'GUNTUR', mobile: '', phone: '', gstin: '', code: 'R2', active: true, isCustomer: false },
      { accountId: 9, name: 'RAMU OLD', town: '', mobile: '', phone: '', gstin: '', code: 'R3', active: false, isCustomer: true },
    ]);
    assert.deepEqual(searchParties('ramu').map((p) => p.accountId).sort(), [7, 8]);
    assert.deepEqual(searchParties('232323').map((p) => p.accountId), [7]);
    assert.deepEqual(searchParties('37abcde').map((p) => p.accountId), [7]);
    assert.deepEqual(searchParties('ramu', (p) => p.isCustomer).map((p) => p.accountId), [7]);
  });
});

describe('onChange / notify', () => {
  test('listeners hear events and can unsubscribe themselves while being notified', () => {
    const heard = [];
    const off = onChange((what) => { heard.push(what); off(); });
    const other = [];
    onChange((what) => other.push(what));
    notify('changed');
    notify('changed');
    assert.deepEqual(heard, ['changed']);
    assert.deepEqual(other, ['changed', 'changed']);
  });

  test('one failing listener does not stop the others', () => {
    const heard = [];
    const err = console.error;
    console.error = () => {};
    try {
      onChange(() => { throw new Error('boom'); });
      onChange((w) => heard.push(w));
      notify('changed');
    } finally { console.error = err; }
    assert.deepEqual(heard, ['changed']);
  });

  test('a listener subscribed during a notification waits for the next one', () => {
    const late = [];
    let subscribed = false;
    onChange(() => { if (!subscribed) { subscribed = true; onChange((w) => late.push(w)); } });
    notify('a');
    assert.deepEqual(late, []);
    notify('b');
    assert.deepEqual(late, ['b']);
  });
});

describe('loadCatalogue', () => {
  test('installs the catalogue and its lookups, and announces it', async () => {
    state.firm = { id: 'mbm' };
    state.catalogue = { items: [], parties: [], stamp: null };
    const data = catalogue([{ itemId: 5, unitId: 2, name: 'X', active: true }], [{ accountId: 6, name: 'CASH', active: true }]);
    globalThis.fetch = async () => ({ status: 200, ok: true, text: async () => JSON.stringify(data) });
    const heard = [];
    onChange((w) => heard.push(w));
    await loadCatalogue(true);
    assert.equal(item(5, 2).name, 'X');
    assert.equal(party('6').name, 'CASH'); // ids from <select> values arrive as strings
    assert.deepEqual(heard, ['catalogue']);
  });

  test('a slow answer for the previous firm is not installed over the newly selected firm', async () => {
    state.firm = { id: 'mbm' };
    state.catalogue = { items: [], parties: [], stamp: null };
    let release;
    globalThis.fetch = () => new Promise((r) => { release = () => r({ status: 200, ok: true, text: async () => JSON.stringify(catalogue([{ itemId: 1, unitId: 1, name: 'MBM ITEM', active: true }], [])) }); });
    const pending = loadCatalogue(true);
    state.firm = { id: 'mp' }; // operator switched firm meanwhile
    release();
    await pending;
    assert.equal(firmId(), 'mp');
    assert.deepEqual(state.catalogue.items, []);
  });

  test('304 (unchanged) keeps the catalogue', async () => {
    state.firm = { id: 'mbm' };
    state.catalogue = catalogue([{ itemId: 1, unitId: 1, name: 'KEEP', active: true }], []);
    globalThis.fetch = async (url, init) => {
      assert.equal(init.headers['If-None-Match'], 'x');
      return { status: 304, ok: false, text: async () => '' };
    };
    await loadCatalogue();
    assert.equal(state.catalogue.items[0].name, 'KEEP');
  });
});
