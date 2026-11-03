const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

// Writing actions, the email under each prompt and Riprendi in the history (Danny, 20/10/2026).
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function section(start, end) {
  const from = html.indexOf(start);
  const to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from, start);
  return html.slice(from, to);
}

class Node {
  constructor(tag, cls, text) { this.tagName = tag.toUpperCase(); this.className = cls || ''; this.textContent = text || ''; this.children = []; this.attrs = {}; this.listeners = {}; this.innerHTML = ''; this.dataset = {}; }
  appendChild(n) { this.children.push(n); return n; }
  setAttribute(k, v) { this.attrs[k] = v; }
  addEventListener(type, fn) { this.listeners[type] = fn; }
  click() { this.listeners.click(); }
  get lastChild() { return this.children[this.children.length - 1]; }
}
function all(node) { return [node].concat(...node.children.map(all)); }
function label(button) { return all(button).filter(n => n.tagName === '#TEXT').map(n => n.textContent).join(''); }

const sent = [];
const ctx = vm.createContext({
  // Copied out of the sandbox: objects from another realm fail a strict deep comparison.
  post: m => sent.push(JSON.parse(JSON.stringify(m))),
  el: (tag, cls, text) => new Node(tag, cls, text),
  plural: (n, one, many) => n + ' ' + (n === 1 ? one : many),
  icon: name => '<svg ' + name + '>',
  copyText: () => {},
  isFollowUp: () => false,
  focusComposer: () => {},
  state: { busy: false },
  cloudIcon: '',
  armConfirm: () => {},
  busyBlocked: () => false,
  finishDrawerLoad: () => {},
  historyMutation: null,
  historyControl: (node, id, action) => { node.dataset.historyId = id; node.dataset.historyAction = action; return node; },
  $: id => ctx.nodes[id] || (ctx.nodes[id] = new Node('div')),
  nodes: {},
  document: { body: { dataset: { page: 'chat' } }, activeElement: {}, createTextNode: t => Object.assign(new Node('#text'), { textContent: t }) },
});
new vm.Script(section('  function actionButton(iconName, label, onClick, tip) {', '  function addSources(')).runInContext(ctx);

// A text written on the selection: Sostituisci (with the selection it came from) and Inserisci; no reply buttons.
const box = new Node('div');
ctx.addResponseActions(box, 'Testo nuovo', { writing: { insert: true, replace: true, newMail: false, subject: false }, source: { type: 'compose_improve', selection: 'testo vecchio' } });
const buttons = all(box).filter(n => n.tagName === 'BUTTON');
assert.deepEqual(buttons.map(label), ['Copia', 'Sostituisci selezione', 'Inserisci']);
buttons[1].click();
assert.deepEqual(sent.pop(), { type: 'compose_apply', mode: 'replace', text: 'Testo nuovo', selection: 'testo vecchio' });

// Written from scratch in the main window: Nuova email opens it in Outlook.
const fresh = new Node('div');
ctx.addResponseActions(fresh, 'Oggetto: Consegna\nGentile Rossi…', { writing: { insert: false, replace: false, newMail: true, subject: false } });
const newMail = all(fresh).filter(n => n.tagName === 'BUTTON').find(b => label(b) === 'Nuova email');
newMail.click();
assert.equal(sent.pop().mode, 'new_mail');

// The email a prompt belongs to: a button that opens it; a draft is only shown.
const chip = ctx.mailChip({ entryId: 'E1', storeId: 'S1', subject: 'Offerta', meta: 'Rossi · 20/10/2026 09:00' });
assert.equal(chip.tagName, 'BUTTON');
chip.click();
assert.deepEqual(sent.pop(), { type: 'archive_open_email', entryId: 'E1', storeId: 'S1' });
assert.equal(ctx.mailChip({ itemType: 'draft', subject: 'nuova email' }).tagName, 'SPAN');

// History rows show the email and a Riprendi icon button (named for screen readers) that loads the conversation.
new vm.Script(section('  function renderHistoryList(data) {', '  function renderLoadedConversation(')).runInContext(ctx);
ctx.historyReadRequest = 'writing-read';
ctx.renderHistoryList({ requestId: 'writing-read', items: [{ id: 'c1', title: 'Sintesi: Offerta', updatedAt: '20/10/2026 10:00', messageCount: 2, email: { subject: 'Offerta', meta: 'Rossi', itemType: 'email' } }] });
const list = ctx.nodes.historyList;
assert.ok(all(list).some(n => n.className === 'history-mail'));
const resume = all(list).filter(n => n.tagName === 'BUTTON').find(b => b.attrs['aria-label'] === 'Riprendi Sintesi: Offerta');
resume.click();
assert.deepEqual(sent.pop(), { type: 'load_history', id: 'c1' });
// The tooltip carries the full title and email subject, which the row truncates with an ellipsis.
const open = all(list).find(n => n.className === 'history-open');
assert.equal(open.title, 'Riprendi: Sintesi: Offerta · Offerta');

assert.match(html, /writing: data\.writing/);
assert.match(html, /id="composeGroup"/);
console.log('PASS writing buttons, email of each prompt and Riprendi in the history');
