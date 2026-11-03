const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

// A199: two-step confirmation, Copia on the toast, choice rows and the error toast reason.
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function section(start, end) {
  const first = html.indexOf(start);
  const last = html.indexOf(end, first);
  assert.ok(first >= 0 && last > first, `Pane section missing: ${start}`);
  return html.slice(first, last);
}
function element(tag, className, text) {
  const classes = new Set((className || '').split(' ').filter(Boolean));
  const node = {
    tag, attrs: {}, children: [], listeners: {}, isConnected: true, innerHTML: text || '', textContent: text || '',
    classList: { add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c) },
    appendChild(child) { this.children.push(child); return child; },
    setAttribute(k, v) { this.attrs[k] = v; }, getAttribute(k) { return this.attrs[k] ?? null; },
    addEventListener(type, fn) { this.listeners[type] = fn; },
    removeEventListener(type, fn) { if (this.listeners[type] === fn) delete this.listeners[type]; }
  };
  return node;
}

const announced = [], timers = [], toasts = [], sent = [];
const context = vm.createContext({
  announce: m => announced.push(m), el: element, post: m => sent.push(m),
  state: { busy: false }, showToast: (m, k) => toasts.push([m, k]),
  setTimeout: (fn, ms) => { timers.push([fn, ms]); return timers.length; }, clearTimeout: () => {},
  document: { createElement: element, body: element('body') }, navigator: {}
});

// Confirmation: stays armed while focused, blur and Esc disarm it, the second click confirms.
new vm.Script(section('  function armConfirm(', "  $('btnDeleteAll')")).runInContext(context);
let confirmed = 0;
const button = element('button', 'btn', 'Elimina');
button.innerHTML = 'Elimina';
context.armConfirm(button, 'Conferma', () => confirmed++);
assert.ok(button.classList.contains('confirming'));
assert.equal(timers.length, 1);
assert.equal(timers[0][1], 15000, 'no short fixed timeout');
button.listeners.blur();
assert.ok(!button.classList.contains('confirming'));
assert.equal(button.innerHTML, 'Elimina');
assert.equal(announced.pop(), 'Conferma annullata.');
assert.equal(button.listeners.blur, undefined, 'listeners removed on disarm');

context.armConfirm(button, 'Conferma', () => confirmed++);
let stopped = false;
button.listeners.keydown({ key: 'Enter', stopPropagation() { stopped = true; } });
assert.ok(button.classList.contains('confirming'), 'other keys keep it armed');
button.listeners.keydown({ key: 'Escape', stopPropagation() { stopped = true; } });
assert.ok(stopped && !button.classList.contains('confirming'));

context.armConfirm(button, 'Conferma', () => confirmed++);
context.armConfirm(button, 'Conferma', () => confirmed++);
assert.equal(confirmed, 1);

// Choices belong to their turn: removed with the stale Riprova buttons.
new vm.Script(section('  function addChoices(', '  function isFollowUp(')).runInContext(context);
const host = element('div');
context.addChoices(host, [{ label: 'Prima', message: { type: 'x' } }]);
assert.equal(host.children[0].getAttribute('data-latest-only'), '');

// Copia on the toast reports on the button; the message being copied stays on screen.
new vm.Script(section('  function copyText(', '  // ---------- Composer ----------')).runInContext(context);
const copyButton = element('button', 'link-btn', 'Copia');
context.navigator.clipboard = { writeText: () => Promise.resolve() };
context.copyText('errore completo', copyButton);
setImmediate(() => {
  assert.equal(copyButton.textContent, 'Copiato');
  assert.equal(toasts.length, 0, 'no toast replaces the error');
  timers.pop()[0]();
  assert.equal(copyButton.textContent, 'Copia');
  // Elsewhere a copy still confirms with a toast.
  context.copyText('altro');
  setImmediate(() => {
    assert.deepEqual(toasts.pop(), ['Copiato negli appunti.', 'success']);
    // The toast after an operation finished behind a drawer names the reason.
    assert.match(html, /'Operazione non riuscita' \+ \(result\.message \? ': ' \+ result\.message : '\.'\)/);
    console.log('PASS pane feedback: confirmation, choices, Copia and error reason');
  });
});
