// Shared app state: selected firm, its HQ profile and the cached catalogue (items + accounts).
import { get } from './api.js';
import { rank } from './util.js';

export const state = { firms: [], firm: null, catalogue: { items: [], parties: [], stamp: null }, listeners: new Set() };

export const firmId = () => state.firm?.id;
export const onChange = (fn) => (state.listeners.add(fn), () => state.listeners.delete(fn));
// A snapshot, each listener on its own: a screen subscribing while we notify waits for the next event, and one
// failing listener does not stop the others from refreshing.
export const notify = (what) => [...state.listeners].forEach((fn) => { try { fn(what); } catch (e) { console.error(e); } });

export async function loadCatalogue(force = false) {
  const headers = !force && state.catalogue.stamp ? { 'If-None-Match': state.catalogue.stamp } : {};
  const fid = firmId();
  const data = await get(`/${fid}/catalogue`, headers);
  // After a quick firm switch the slower answer may be the other firm's: never install it over the selected firm's.
  if (firmId() !== fid) return state.catalogue;
  if (data) {
    state.catalogue = data;
    state.byItem = new Map(data.items.map((i) => [i.itemId + ':' + i.unitId, i]));
    state.byParty = new Map(data.parties.map((p) => [p.accountId, p]));
    notify('catalogue');
  }
  return state.catalogue;
}

export const item = (itemId, unitId) => state.byItem?.get(itemId + ':' + unitId);
export const party = (id) => state.byParty?.get(Number(id));

// What a typed search matches, shared by the bill lookups and the Items / Customers screens (one list each, so the
// same words find the same entries everywhere; module-level so rank() can cache the text per entry).
export const ITEM_FIELDS = (i) => [i.name, i.code, i.group, i.hsn];
export const PARTY_FIELDS = (p) => [p.name, p.town, p.mobile, p.phone, p.gstin, p.code];

export function searchItems(text) {
  return rank(state.catalogue.items.filter((i) => i.active), text, ITEM_FIELDS);
}

export function searchParties(text, filter = () => true) {
  return rank(state.catalogue.parties.filter((p) => p.active && filter(p)), text, PARTY_FIELDS);
}

export const paymentModes = () => (state.firm?.paymentModes ?? []).filter((m) => m.active);
export const ttype = (tt) => state.firm?.types?.find((t) => t.ttId === tt);
