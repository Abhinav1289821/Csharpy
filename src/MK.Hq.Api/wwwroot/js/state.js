// Shared app state: selected firm, its HQ profile and the cached catalogue (items + accounts).
import { get } from './api.js';
import { rank } from './util.js';

export const state = { firms: [], firm: null, catalogue: { items: [], parties: [], stamp: null }, listeners: new Set() };

export const firmId = () => state.firm?.id;
export const onChange = (fn) => (state.listeners.add(fn), () => state.listeners.delete(fn));
export const notify = (what) => state.listeners.forEach((fn) => fn(what));

export async function loadCatalogue(force = false) {
  const headers = !force && state.catalogue.stamp ? { 'If-None-Match': state.catalogue.stamp } : {};
  const data = await get(`/${firmId()}/catalogue`, headers);
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

export function searchItems(text) {
  return rank(state.catalogue.items.filter((i) => i.active), text, (i) => [i.name, i.code, i.group, i.hsn]);
}

export function searchParties(text, filter = () => true) {
  return rank(state.catalogue.parties.filter((p) => p.active && filter(p)), text, (p) => [p.name, p.town, p.mobile, p.phone, p.gstin, p.code]);
}

export const paymentModes = () => (state.firm?.paymentModes ?? []).filter((m) => m.active);
export const ttype = (tt) => state.firm?.types?.find((t) => t.ttId === tt);
