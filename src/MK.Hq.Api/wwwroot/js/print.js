// GST tax invoice print (A4). Built from the saved HQ document, so the paper always matches HQ.
import { get } from './api.js';
import { el, money, qty, dmy, $ } from './util.js';
import { state, firmId } from './state.js';

export async function printDocument(documentId) {
  const d = await get(`/${firmId()}/documents/${documentId}`);
  const root = $('#print-root');
  root.replaceChildren(invoice(d));
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  window.print();
}

export function invoice(d) {
  const s = d.summary;
  const title = s.kind === 'Sales Order' ? 'ESTIMATE' : s.kind === 'Purchase' ? 'PURCHASE VOUCHER' : s.kind === 'Purchase Order' ? 'PURCHASE ORDER' : 'TAX INVOICE';
  const head = (d.letterhead?.length ? d.letterhead : [d.firmName]);
  const local = !d.interstate;
  return el('div', { class: 'invoice' },
    el('h2', {}, head[0]),
    head.slice(1).map((l) => el('div', { class: 'center' }, l)),
    d.firmGstin ? el('div', { class: 'center' }, 'GSTIN: ' + d.firmGstin) : null,
    el('div', { class: 'center', style: { fontWeight: '700', margin: '6px 0', letterSpacing: '.08em' } }, title),
    el('table', { class: 'noborder' }, el('tbody', {}, el('tr', {},
      el('td', { style: { width: '60%' } },
        el('div', {}, el('b', {}, s.kind === 'Purchase' ? 'Supplier: ' : 'Bill to: '), d.party.name),
        d.party.address ? el('div', {}, d.party.address) : null,
        d.party.town ? el('div', {}, d.party.town) : null,
        d.party.phone ? el('div', {}, 'Ph: ' + d.party.phone) : null,
        d.party.gstin ? el('div', {}, 'GSTIN: ' + d.party.gstin) : null,
        d.party.state ? el('div', {}, 'State: ' + d.party.state) : null),
      el('td', {},
        el('div', {}, el('b', {}, 'No: '), s.reference),
        el('div', {}, el('b', {}, 'Date: '), dmy(s.date)),
        d.supplierBillNumber ? el('div', {}, el('b', {}, 'Supplier bill: '), d.supplierBillNumber, ' dt ', dmy(d.supplierBillDate)) : null,
        el('div', {}, el('b', {}, 'Payment: '), d.paymentCaption),
        d.salesperson ? el('div', {}, el('b', {}, 'Salesman: '), d.salesperson) : null)))),
    el('table', { style: { marginTop: '6px' } },
      el('thead', {}, el('tr', {}, ['#', 'Item', 'HSN', 'Qty', 'Rate', 'Disc %', 'Taxable', 'GST %', 'Amount'].map((h, i) => el('th', { class: i >= 3 ? 'n' : '' }, h)))),
      el('tbody', {}, d.lines.map((l) => el('tr', {},
        el('td', {}, l.number), el('td', {}, l.name, l.description && l.description.trim() && l.description.trim() !== l.name ? el('div', { style: { fontSize: '11px' } }, l.description) : null),
        el('td', {}, l.hsn), el('td', { class: 'n' }, qty(l.quantity) + ' ' + (l.unit || '')), el('td', { class: 'n' }, money(l.rate)),
        el('td', { class: 'n' }, l.discountPercent ? qty(l.discountPercent) : ''), el('td', { class: 'n' }, money(l.taxable)),
        el('td', { class: 'n' }, qty(l.taxRate)), el('td', { class: 'n' }, money(l.net))))),
      el('tfoot', {}, el('tr', {}, el('td', { colspan: 3 }, 'Total'), el('td', { class: 'n' }, qty(d.quantity)), el('td', { colspan: 2 }),
        el('td', { class: 'n' }, money(d.taxable)), el('td'), el('td', { class: 'n' }, money(d.beforeRounding))))),
    el('table', { style: { marginTop: '6px', width: '60%' } },
      el('thead', {}, el('tr', {}, el('th', {}, 'GST %'), el('th', { class: 'n' }, 'Taxable'), ...(local ? [el('th', { class: 'n' }, 'CGST'), el('th', { class: 'n' }, 'SGST')] : [el('th', { class: 'n' }, 'IGST')]))),
      el('tbody', {}, d.slabs.map((x) => el('tr', {}, el('td', {}, qty(x.rate) + '%'), el('td', { class: 'n' }, money(x.taxable)),
        ...(local ? [el('td', { class: 'n' }, money(x.cgst)), el('td', { class: 'n' }, money(x.sgst))] : [el('td', { class: 'n' }, money(x.igst))]))))),
    el('table', { class: 'noborder', style: { marginTop: '6px' } }, el('tbody', {},
      el('tr', {}, el('td', {}, el('b', {}, d.totalInWords)), el('td', { class: 'n' }, 'Round off ', money(d.roundOff))),
      el('tr', {}, el('td'), el('td', { class: 'n', style: { fontSize: '16px' } }, el('b', {}, 'Total ₹ ' + money(d.total)))))),
    d.message ? el('div', { style: { marginTop: '10px' } }, d.message) : null,
    el('div', { style: { marginTop: '40px', textAlign: 'right' } }, 'For ' + head[0]));
}
