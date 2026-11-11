const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const from = html.indexOf("      case 'stream_interpretation':");
const to = html.indexOf("      case 'stream_phase':", from);
assert.ok(from > 0 && to > from);
// #201: the host says how it read the question; the page shows it once, above the answer, and updates it in place.
function node(cls) { return { cls, textContent: '', children: [] }; }
const assistant = { children: [], insertBefore(n, ref) { this.children.splice(this.children.indexOf(ref), 0, n); } };
const body = node('msg-body md');
assistant.children.push(body);
const state = { current: { assistant, body } };
const ctx = vm.createContext({ state, el: (tag, cls) => node(cls) });
vm.runInContext('function onHostMessage(data) { switch(data.type) {' + html.slice(from, to) + '}}', ctx);
ctx.onHostMessage({ type: 'stream_interpretation', text: 'Interpretata come ricerca nella posta (parole: «contratto»).' });
assert.equal(assistant.children.length, 2);
assert.equal(assistant.children[0].cls, 'msg-interpretation');
assert.equal(assistant.children[1], body);
assert.match(assistant.children[0].textContent, /ricerca nella posta/);
ctx.onHostMessage({ type: 'stream_interpretation', text: 'Interpretata come domanda generale: nessuna ricerca in posta, calendario o contatti.' });
assert.equal(assistant.children.length, 2);
assert.match(assistant.children[0].textContent, /domanda generale/);
// Nothing to show without a running turn or a text.
state.current = null;
ctx.onHostMessage({ type: 'stream_interpretation', text: 'x' });
state.current = { assistant: { children: [], insertBefore() { throw new Error('no node for an empty text'); } }, body };
ctx.onHostMessage({ type: 'stream_interpretation', text: '' });
assert.ok(html.includes('.msg-interpretation {'));
console.log('PASS interpretation line above the answer, updated in place');
