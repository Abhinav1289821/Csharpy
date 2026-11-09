// HTTP client for the MK HQ engine. Every write carries a client-generated operation id, so a retried click
// can never save twice: the server returns the stored receipt instead.

const KEY = 'mk-hq-token';
let token = '';

export function initToken() {
  const m = location.hash.match(/token=([0-9a-f]{32,})/i);
  if (m) {
    token = m[1];
    try { localStorage.setItem(KEY, token); } catch { /* private mode */ }
    history.replaceState(null, '', location.pathname + '#/');
  } else {
    try { token = localStorage.getItem(KEY) ?? ''; } catch { token = ''; }
  }
  return token;
}

export class ApiError extends Error {
  constructor(status, body) {
    super(body?.message ?? `Request failed (${status})`);
    this.status = status;
    this.code = body?.code ?? 'HTTP_' + status;
    this.kind = body?.kind;
    this.detail = body?.detail;
  }
}

export async function api(method, path, body, extraHeaders = {}) {
  let res;
  try {
    res = await fetch('/api' + path, {
      method,
      headers: { Authorization: 'Bearer ' + token, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, { code: 'OFFLINE', message: 'MK Billing service is not running. Start it from the desktop icon.' });
  }
  if (res.status === 304) return null;
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}

export const get = (p, headers) => api('GET', p, undefined, headers);
export const post = (p, b) => api('POST', p, b ?? {});
export const put = (p, b) => api('PUT', p, b);
export const del = (p) => api('DELETE', p);
export const opId = () => crypto.randomUUID();
export const eventsUrl = (firm) => `/api/${firm}/events?token=${token}`;
/** For <img src> and uploads, which cannot always send the Authorization header. */
export const tokenQuery = () => `token=${token}`;
