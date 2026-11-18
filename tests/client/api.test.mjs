import './setup.mjs';
import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, api, get } from '../../src/MK.Hq.Api/wwwroot/js/api.js';

const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; });

const reply = (status, text) => async () => ({ status, ok: status >= 200 && status < 300, text: async () => text });

describe('ApiError', () => {
  test('carries the server\'s {code, message, kind, detail}', () => {
    const e = new ApiError(409, { code: 'REVIEW_CHANGED', message: 'The bill changed', kind: 'Conflict', detail: { a: 1 } });
    assert.ok(e instanceof Error);
    assert.equal(e.status, 409);
    assert.equal(e.code, 'REVIEW_CHANGED');
    assert.equal(e.message, 'The bill changed');
    assert.equal(e.kind, 'Conflict');
    assert.deepEqual(e.detail, { a: 1 });
  });
  test('no body: a message with the status, code HTTP_<status>', () => {
    const e = new ApiError(502, null);
    assert.equal(e.message, 'Request failed (502)');
    assert.equal(e.code, 'HTTP_502');
  });
});

describe('api()', () => {
  test('returns the parsed JSON', async () => {
    globalThis.fetch = reply(200, '{"x":1}');
    assert.deepEqual(await get('/firms'), { x: 1 });
  });

  test('an empty body is null; 304 (catalogue unchanged) is null', async () => {
    globalThis.fetch = reply(200, '');
    assert.equal(await get('/x'), null);
    globalThis.fetch = reply(304, '');
    assert.equal(await get('/x'), null);
  });

  test('a business refusal becomes ApiError with the server\'s words', async () => {
    globalThis.fetch = reply(400, '{"code":"BAD_REQUEST","message":"Enter the customer name","kind":"Invalid"}');
    await assert.rejects(get('/x'), (e) => e instanceof ApiError && e.status === 400 && e.message === 'Enter the customer name');
  });

  test('a non-JSON error page keeps its HTTP status (it used to throw "Unexpected token <")', async () => {
    globalThis.fetch = reply(502, '<html><body>Bad Gateway</body></html>');
    await assert.rejects(get('/x'), (e) => e instanceof ApiError && e.status === 502 && e.message === 'Request failed (502)');
  });

  test('an unreadable 200 is not taken as success', async () => {
    globalThis.fetch = reply(200, '{"cut off');
    await assert.rejects(get('/x'), (e) => e instanceof ApiError && e.code === 'BAD_REPLY');
  });

  test('service not running: OFFLINE with the operator\'s instruction', async () => {
    globalThis.fetch = async () => { throw new TypeError('Failed to fetch'); };
    await assert.rejects(get('/x'), (e) => e instanceof ApiError && e.code === 'OFFLINE' && /desktop icon/.test(e.message));
  });

  test('writes send JSON with a content type; reads send none', async () => {
    const seen = [];
    globalThis.fetch = async (url, init) => { seen.push({ url, init }); return { status: 200, ok: true, text: async () => '{}' }; };
    await api('POST', '/mbm/vouchers', { a: 1 });
    await get('/mbm/catalogue', { 'If-None-Match': 'abc' });
    assert.equal(seen[0].url, '/api/mbm/vouchers');
    assert.equal(seen[0].init.headers['Content-Type'], 'application/json');
    assert.equal(seen[0].init.body, '{"a":1}');
    assert.equal(seen[1].init.headers['Content-Type'], undefined);
    assert.equal(seen[1].init.headers['If-None-Match'], 'abc');
    assert.equal(seen[1].init.body, undefined);
  });
});
