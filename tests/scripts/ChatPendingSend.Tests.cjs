const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

// #240: Invia shows the prompt and «In attesa…» at once; without stream_start the text goes back to the field.
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const start = html.indexOf('  // #240: Invia shows the prompt');
const end = html.indexOf("  prompt.addEventListener('input'", start);
assert.ok(start > 0 && end > start, 'pending-send block not found before the prompt listeners');

function element(tag, cls, text) {
  return { tag, className: cls || '', textContent: text || '', innerHTML: '', children: [], parentNode: null,
    appendChild(c) { c.parentNode = this; this.children.push(c); },
    removeChild(c) { this.children.splice(this.children.indexOf(c), 1); c.parentNode = null; } };
}
const timers = new Map();
let sequence = 0;
const toasts = [];
const conv = element('div', 'conv');
const prompt = { value: '' };
const context = vm.createContext({
  conv, prompt,
  el: element,
  hideEmptyState() {}, scrollToBottom() {}, announce() {}, autoresize() {}, updateSendButton() {},
  showToast: (message, kind) => toasts.push({ message, kind }),
  setTimeout: (fn, ms) => { assert.equal(ms, 15000); timers.set(++sequence, fn); return sequence; },
  clearTimeout: id => timers.delete(id),
});
new vm.Script(html.slice(start, end)).runInContext(context);

context.beginPendingSend('Quando scade il contratto?');
assert.equal(conv.children.length, 1, 'the prompt is shown before the host answers');
const turn = conv.children[0];
assert.equal(turn.children[0].textContent, 'Quando scade il contratto?');
assert.match(turn.children[1].children[0].innerHTML, /In attesa…/);
assert.equal(timers.size, 1);

// stream_start (and stream_error, stream_cancelled) replace the provisional turn.
context.clearPendingSend();
assert.equal(conv.children.length, 0);
assert.equal(timers.size, 0, 'the host answer stops the timeout');

// No answer: the provisional turn goes, the text returns to an empty field, with a message.
context.beginPendingSend('Riassumi la discussione');
[...timers.values()][0]();
assert.equal(conv.children.length, 0);
assert.equal(prompt.value, 'Riassumi la discussione');
assert.equal(toasts.length, 1);
assert.equal(toasts[0].kind, 'error');
assert.match(toasts[0].message, /testo è tornato nel campo/);

// Text typed meanwhile is not overwritten.
prompt.value = 'nuova domanda';
context.beginPendingSend('vecchia');
[...timers.values()][0]();
assert.equal(prompt.value, 'nuova domanda');

// Wiring: submitPrompt starts the pending send; every stream outcome clears it.
const submit = html.slice(html.indexOf('  function submitPrompt() {'), start);
assert.match(submit, /beginPendingSend\(text\);\s*\}\s*$/, 'submitPrompt must show the pending prompt');
for (const kind of ['stream_start', 'stream_error', 'stream_cancelled']) {
  assert.match(html, new RegExp(`case '${kind}':\\s*clearPendingSend\\(\\);`), `${kind} must clear the pending prompt`);
}
console.log('PASS #240 chat: immediate prompt and «In attesa…», text restored without stream_start');
