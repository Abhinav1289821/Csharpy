import './setup.mjs';
import { test, describe, mock } from 'node:test';
import assert from 'node:assert/strict';
import { money, qty, dmy, addDays, today, oneDecimal, rank, debounce, newest, csvText } from '../../src/MK.Hq.Api/wwwroot/js/util.js';

describe('money', () => {
  test('Indian lakh/crore grouping, always two decimals', () => {
    assert.equal(money(1234567.891), '12,34,567.89');
    assert.equal(money(10000000), '1,00,00,000.00');
    assert.equal(money(999.5), '999.50');
    assert.equal(money('1500'), '1,500.00'); // the server sends decimals as JSON numbers or strings
    assert.equal(money(0), '0.00');
  });
  test('negatives keep their sign', () => {
    assert.equal(money(-1234.5), '-1,234.50');
    assert.equal(money(-0.01), '-0.01');
  });
  test('a nil amount never reads "-0.00" (floating sums, negative zero)', () => {
    assert.equal(money(-0), '0.00');
    assert.equal(money(-0.004), '0.00');
    assert.equal(money(0.1 + 0.2 - 0.3 - 1e-16), '0.00');
    assert.equal(money(100.1 - 100.1000000001), '0.00');
  });
  test('blank stays blank', () => {
    assert.equal(money(null), '');
    assert.equal(money(undefined), '');
    assert.equal(money(''), '');
  });
});

describe('qty', () => {
  test('up to four decimals, lakh grouping', () => {
    assert.equal(qty(1.23456), '1.2346');
    assert.equal(qty(100000), '1,00,000');
    assert.equal(qty(2.5), '2.5');
    assert.equal(qty(-3), '-3');
  });
  test('blank stays blank (an empty quantity used to print as 0)', () => {
    assert.equal(qty(''), '');
    assert.equal(qty(null), '');
    assert.equal(qty(undefined), '');
  });
  test('no "-0"', () => {
    assert.equal(qty(-0), '0');
    assert.equal(qty(-0.00001), '0');
  });
});

describe('dates', () => {
  test('dmy turns ISO into the shop\'s dd-mm-yyyy', () => {
    assert.equal(dmy('2026-04-01'), '01-04-2026');
    assert.equal(dmy('2026-03-31T18:30:00'), '31-03-2026');
    assert.equal(dmy(''), '');
    assert.equal(dmy(null), '');
  });

  test('addDays crosses the financial-year boundary and leap days', () => {
    assert.equal(addDays('2026-03-31', 1), '2026-04-01');
    assert.equal(addDays('2026-04-01', -1), '2026-03-31');
    assert.equal(addDays('2028-02-28', 1), '2028-02-29');
    assert.equal(addDays('2026-11-18', -30), '2026-10-19');
    assert.equal(addDays('2026-12-31', 1), '2027-01-01');
  });

  test('addDays does not depend on the PC\'s time zone', () => {
    const saved = process.env.TZ;
    try {
      for (const tz of ['Asia/Kolkata', 'America/New_York', 'Pacific/Kiritimati', 'UTC']) {
        process.env.TZ = tz;
        assert.equal(addDays('2026-03-31', 1), '2026-04-01', tz);
        assert.equal(addDays('2026-03-08', 1), '2026-03-09', tz); // a US DST change night
      }
    } finally { process.env.TZ = saved; }
  });

  describe('today() is the shop\'s local date, not UTC', () => {
    const at = (iso, fn) => {
      mock.timers.enable({ apis: ['Date'], now: Date.parse(iso) });
      try { return fn(); } finally { mock.timers.reset(); }
    };
    test('00:30 IST is already the next day (UTC still says yesterday)', () => {
      assert.equal(at('2026-11-18T19:00:00Z', today), '2026-11-19');
    });
    test('23:59:59 IST is still the same day', () => {
      assert.equal(at('2026-11-19T18:29:59Z', today), '2026-11-19');
    });
    test('the financial year turns at local midnight on 1 April', () => {
      assert.equal(at('2026-03-31T18:29:59Z', today), '2026-03-31');
      assert.equal(at('2026-03-31T18:30:00Z', today), '2026-04-01');
    });
  });
});

describe('oneDecimal (number boxes)', () => {
  test('digits and the first decimal point only', () => {
    assert.equal(oneDecimal('1..5'), '1.5');
    assert.equal(oneDecimal('12a.3.4'), '12.34');
    assert.equal(oneDecimal('.5'), '.5');
    assert.equal(oneDecimal(''), '');
    assert.equal(oneDecimal('1,50,000'), '150000');
  });
  test('no minus: amounts and quantities are typed positive', () => {
    assert.equal(oneDecimal('-5'), '5');
  });
  test('numbers pass through', () => {
    assert.equal(oneDecimal(2.5), '2.5');
  });
});

describe('rank', () => {
  const items = [
    { name: 'STOPTAP TAP 15MM', code: 'A1' },
    { name: 'TAP 15MM', code: 'A2' },
    { name: 'BALL VALVE 15MM', code: 'A3' },
  ];
  const fields = (i) => [i.name, i.code];

  test('every word must appear; empty text finds nothing', () => {
    assert.deepEqual(rank(items, 'valve 15', fields).map((i) => i.code), ['A3']);
    assert.deepEqual(rank(items, '   ', fields), []);
    assert.deepEqual(rank(items, 'pipe', fields), []);
  });

  test('names starting with the word first, then a word that starts anywhere, then mid-word', () => {
    assert.deepEqual(rank(items, 'tap', fields).map((i) => i.code), ['A2', 'A1']);
  });

  test('a word that appears mid-word first but also starts a later word ranks as a word start', () => {
    const list = [{ name: 'XSTOPTAP TAP', code: 'B1' }, { name: 'XYZ STOPTAP', code: 'B2' }];
    // B1 has "tap" as a word of its own; B2 only inside STOPTAP. Old code scored both as mid-word.
    assert.deepEqual(rank(list, 'tap', (i) => [i.name]).map((i) => i.code), ['B1', 'B2']);
  });

  test('limit', () => {
    assert.equal(rank(items, '15mm', fields, 2).length, 2);
  });

  test('two screens searching different fields of the same entries do not share one cache', () => {
    const parties = [{ name: 'RAMU TRADERS', phone: '08672-252525', mobile: '' }];
    const screenFields = (p) => [p.name, p.mobile]; // a screen that does not search the landline
    const billFields = (p) => [p.name, p.mobile, p.phone]; // the bill's party lookup does
    assert.deepEqual(rank(parties, '252525', screenFields), []);
    // The old cache stored the first screen's text on the entry, so the bill could no longer find him by phone.
    assert.equal(rank(parties, '252525', billFields).length, 1);
  });

  test('null fields are fine', () => {
    assert.equal(rank([{ name: 'A', code: null }], 'a', fields).length, 1);
  });
});

describe('debounce', () => {
  test('only the last call within the wait runs, with its arguments', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
      const calls = [];
      const f = debounce((x) => calls.push(x), 250);
      f('r'); f('ra'); f('ram');
      mock.timers.tick(249);
      assert.deepEqual(calls, []);
      mock.timers.tick(1);
      assert.deepEqual(calls, ['ram']);
      f('ramu');
      mock.timers.tick(250);
      assert.deepEqual(calls, ['ram', 'ramu']);
    } finally { mock.timers.reset(); }
  });
});

describe('csvText (Export CSV)', () => {
  const cols = [{ title: 'Party', get: 'name' }, { title: 'Balance', get: 'balance' }, { title: 'Note', get: (r) => r.note }];
  test('quotes commas, quotes and line breaks (CR alone too); CRLF rows', () => {
    const t = csvText([{ name: 'RAMU, ONGOLE', balance: 10, note: 'said "ok"\rlater' }], cols);
    assert.equal(t, 'Party,Balance,Note\r\n"RAMU, ONGOLE",10,"said ""ok""\rlater"');
  });
  test('text that Excel would run as a formula stays text; negative numbers stay numbers', () => {
    const t = csvText([
      { name: '=HYPERLINK("x")', balance: -1500.5, note: '+91 98480' },
      { name: '@SUM(A1)', balance: '-20.00', note: '-SPECIAL RATE' },
    ], cols);
    const [, a, b] = t.split('\r\n');
    assert.equal(a, '"\'=HYPERLINK(""x"")",-1500.5,\'+91 98480');
    assert.equal(b, "'@SUM(A1),-20.00,'-SPECIAL RATE");
  });
  test('blank cells', () => {
    assert.equal(csvText([{ name: null, balance: undefined }], cols), 'Party,Balance,Note\r\n,,');
  });
});

describe('newest', () => {
  test('only the most recent ticket is current', () => {
    const ticket = newest();
    const a = ticket();
    assert.equal(a(), true);
    const b = ticket();
    assert.equal(a(), false);
    assert.equal(b(), true);
  });

  test('a slow older search cannot overwrite a newer one', async () => {
    const ticket = newest();
    let shown = null;
    const load = async (q, ms) => {
      const current = ticket();
      await new Promise((r) => setTimeout(r, ms));
      if (current()) shown = q;
    };
    await Promise.all([load('ra', 30), load('ramu', 5)]);
    assert.equal(shown, 'ramu');
  });

  test('separate screens have separate counters', () => {
    const one = newest();
    const two = newest();
    const a = one();
    two();
    assert.equal(a(), true);
  });
});
