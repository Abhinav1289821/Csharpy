// Minimal browser stand-ins so the client's ES modules (plain browser code, no build step) import under Node.
// Only what the modules touch at import time or in the pure helpers under test; anything else stays undefined so a
// test that wanders into real DOM work fails loudly instead of passing against a fake.
process.env.TZ = 'Asia/Kolkata'; // the shop's clock; tests that need another zone set and restore it themselves

class FakeElement {
  replaceChildren() {}
}
globalThis.Element ??= FakeElement;
globalThis.document ??= { activeElement: null, body: { tagName: 'BODY' }, querySelector: () => null, addEventListener() {}, removeEventListener() {} };
globalThis.location ??= { hash: '#/', pathname: '/' };
globalThis.history ??= { replaceState() {} };
globalThis.localStorage ??= { getItem: () => null, setItem() {}, removeItem() {} };

export const JS = new URL('../../src/MK.Hq.Api/wwwroot/js/', import.meta.url);
