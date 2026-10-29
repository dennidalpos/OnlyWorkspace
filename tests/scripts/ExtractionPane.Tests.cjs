const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
class Node {
  constructor(tag, cls, text) { this.tag = tag; this.className = cls; this.textContent = text || ''; this.children = []; this.listeners = {}; }
  appendChild(n) { this.children.push(n); return n; }
  addEventListener(event, fn) { this.listeners[event] = fn; }
  insertBefore(n) { this.children.unshift(n); return n; }
  setAttribute() { }
  get firstChild() { return this.children[0]; }
  get lastChild() { return this.children[this.children.length - 1] || this; }
}
const copied = [], sent = [];
const context = vm.createContext({
  el: (tag, cls, text) => new Node(tag, cls, text), icon: () => '', state: { busy: false },
  document: { createElement: tag => new Node(tag), createTextNode: text => new Node('#text', null, text) },
  copyText: text => copied.push(text), post: message => sent.push(message),
  actionButton: (icon, label, click) => { const n = new Node('button', null, label); n.listeners.click = click; return n; },
  mailChip: ref => { const n = new Node('button', 'mail-chip', ref.subject + ' · ' + ref.meta); n.listeners.click = () => sent.push({ type: 'archive_open_email', entryId: ref.entryId, storeId: ref.storeId }); return n; }
});
const start = html.indexOf('  function addActionItems('), end = html.indexOf('  // Streaming text is not live', start);
assert.ok(start > 0 && end > start);
vm.runInContext(html.slice(start, end), context);
const nodes = root => [root, ...root.children.flatMap(nodes)];
const table = { documentType: 'ordine', fields: Array.from({ length: 41 }, (_, i) => [`field${i + 1}`, `value${i + 1}`]),
  lineHeaders: ['Descrizione'], lines: Array.from({ length: 201 }, (_, i) => [`row${i + 1}`]),
  tsv: 'Risultato parziale\nrow201', csv: 'Risultato parziale;row201' };
const box = new Node('div');
context.addDataTable(box, table);
assert.equal(nodes(box).filter(n => n.tag === 'tr' && n.children.some(c => c.tag === 'td')).length, 242);
assert.ok(nodes(box).some(n => n.textContent === 'field41'));
assert.ok(nodes(box).some(n => n.textContent === 'row201'));
nodes(box).find(n => n.textContent === 'Copia tabella').listeners.click();
nodes(box).find(n => n.textContent === 'Salva CSV in Download').listeners.click();
assert.equal(copied.pop(), table.tsv);
assert.equal(sent.pop().csv, table.csv);
const actions = new Node('div');
context.addActionItems(actions, Array.from({ length: 11 }, (_, i) => ({ kind: 'task', title: `task${i + 1}` })), null);
assert.equal(nodes(actions).filter(n => n.className === 'action-item').length, 11);
assert.ok(nodes(actions).some(n => n.textContent === 'task11'));
// Mailbox scan item (A121): the email's own words for the date and the source email, openable.
const scan = new Node('div');
context.addActionItems(scan, [{ kind: 'appointment', title: 'Riunione', when: 'ven 30 ott 2026, 10:00', dateText: 'venerdì alle 10',
  source: { label: '[M2] Riunione di avanzamento', meta: '26/10/2026', entryId: 'E2', storeId: 'S1' }, selectable: true, verified: true }], null);
assert.ok(nodes(scan).some(n => n.textContent === 'Nell\'email: «venerdì alle 10»'));
nodes(scan).find(n => n.className === 'mail-chip').listeners.click();
assert.deepEqual(sent.pop(), { type: 'archive_open_email', entryId: 'E2', storeId: 'S1' });
console.log('PASS extraction pane: 41 fields, 201 rows, 11 actions, full copy/CSV payload, scan item source');
