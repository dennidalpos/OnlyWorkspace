const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const sent = [], spoken = [], nodes = new Map(), deferred = [];
let focusListener;
let historyReads = 0;
const document = { body: { dataset: { page: 'historyDrawer' } }, activeElement: null,
  addEventListener: (event, fn) => { assert.equal(event, 'focusin'); focusListener = fn; } };
class Element {
  constructor(tag, cls, text) { this.tagName = tag; this.className = cls || ''; this.textContent = text || ''; this.children = []; this.dataset = {}; this.listeners = {}; }
  appendChild(node) { this.children.push(node); node.parent = this; return node; }
  set innerHTML(value) { this.children = []; this.html = value; }
  setAttribute(key, value) { this[key] = value; }
  addEventListener(event, fn) { this.listeners[event] = fn; }
  contains(node) { return this === node || this.children.some(child => child.contains(node)); }
  closest() { return this.className === 'history-item' ? this : this.parent ? this.parent.closest() : null; }
  querySelectorAll() { return this.children.flatMap(child => [...(child.dataset.historyAction ? [child] : []), ...child.querySelectorAll()]); }
  focus() { document.activeElement = this; if (focusListener) focusListener({ target: this }); }
}
const ctx = vm.createContext({ document, historyPageVersion: 0,
  $: id => { if (!nodes.has(id)) nodes.set(id, new Element('div')); return nodes.get(id); },
  el: (tag, cls, text) => new Element(tag, cls, text), icon: () => '',
  plural: n => String(n), finishDrawerLoad() {}, busyBlocked: () => false,
  beginDrawerLoad() {}, setTimeout(fn) { deferred.push(fn); },
  post: data => { sent.push(data); if (data.type === 'list_history') historyReads++; }, announce: text => spoken.push(text),
  armConfirm: (button, label, action) => action()
});
vm.runInContext(html.slice(html.indexOf('  var historyMutation = null,'), html.indexOf('  function renderLoadedConversation(')), ctx);
vm.runInContext(html.slice(html.indexOf('  function requestHistory('), html.indexOf('  // ---------- Drawers ----------')), ctx);
const item = id => ({ id, title: id, messageCount: 1 });
const list = ctx.$('historyList');
function render(ids, extra) {
  if (!extra || !extra.requestId) { ctx.requestHistory(true); extra = Object.assign({ requestId: ctx.historyReadRequest }, extra); }
  ctx.renderHistoryList(Object.assign({ items: ids.map(item) }, extra));
  while (deferred.length) deferred.shift()();
}
function button(id, action) { return list.querySelectorAll().find(b => b.dataset.historyId === id && b.dataset.historyAction === action); }
function remove(id) { const trigger = id == null ? ctx.$('btnDeleteAll') : button(id, 'delete'); trigger.focus(); ctx.deleteHistory(trigger, id); return sent.at(-1).requestId; }
function assertFocus(id, action = 'open') { assert.equal(document.activeElement, button(id, action)); }

render(['a', 'b', 'c']);
button('b', 'resume').focus();
render(['c', 'b', 'a']);
assertFocus('b', 'resume');
let requestId = remove('b');
assert.equal(spoken.length, 0, 'No success before persistence');
render(['a'], { requestId: 'obsolete', deleted: true });
assert.equal(list.children.length, 3, 'Stale completion ignored');
render(['a']);
assert.equal(list.children.length, 3, 'Unrelated read cannot overwrite a pending mutation');
render(['c', 'a'], { requestId, deleted: true });
assertFocus('a');
assert.equal(spoken.at(-1), 'Conversazione eliminata.');
ctx.$('btnDeleteAll').focus();
render(['c', 'a']);
assert.equal(document.activeElement, ctx.$('btnDeleteAll'), 'Ordinary refresh retains toolbar focus');
requestId = remove('a');
render(['c'], { requestId, deleted: true });
assertFocus('c', 'open');
requestId = remove('c');
render([], { requestId, deleted: true });
assert.equal(document.activeElement, ctx.$('historyTitle'));

render(['a', 'b']);
requestId = remove(null);
render([], { requestId, deleted: true });
assert.equal(spoken.at(-1), 'Tutte le conversazioni eliminate.');
assert.equal(document.activeElement, ctx.$('historyTitle'));

render(['a', 'b']);
requestId = remove('a');
const failedTrigger = document.activeElement, announcements = spoken.length;
ctx.failHistoryDelete({ requestId: 'obsolete' });
assert.ok(ctx.historyMutation);
ctx.failHistoryDelete({ requestId });
assert.equal(ctx.historyMutation, null);
assert.equal(list.children.length, 2);
assert.equal(document.activeElement, failedTrigger);
assert.equal(spoken.length, announcements);
requestId = remove('a');
render([], { requestId, deleted: true, error: 'Lettura non riuscita' });
assert.equal(document.activeElement, ctx.$('historyTitle'), 'Committed deletion remains successful if the subsequent read fails');
assert.match(spoken.at(-1), /Conversazione eliminata.*Lettura non riuscita/);
const readsBeforeRetry = historyReads;
list.children.find(child => child.textContent === 'Riprova').listeners.click();
assert.equal(historyReads, readsBeforeRetry + 1, 'Retry reads the history without repeating the committed deletion');
assert.equal(document.activeElement, ctx.$('historyTitle'));

render(['a', 'b']);
requestId = remove('a');
ctx.$('btnHistory').focus();
render(['b'], { requestId, deleted: true });
assert.equal(document.activeElement, ctx.$('btnHistory'), 'Do not steal focus after another control is selected');
render(['a', 'b', 'c']);
requestId = remove('a');
button('c', 'resume').focus();
render(['b', 'c'], { requestId, deleted: true });
assertFocus('c', 'resume');
render(['a', 'b']);
requestId = remove('a');
ctx.historyPageVersion += 2; // Left and reopened the page while awaiting persistence.
render(['b'], { requestId, deleted: true });
assert.notEqual(document.activeElement, button('b', 'open'), 'A reopened page does not reuse old focus intent');
const beforeBackground = document.activeElement;
document.body.dataset.page = 'chat';
render(['a', 'b']);
assert.equal(document.activeElement, beforeBackground, 'Background refresh does not move focus');
render(['b'], { requestId, deleted: true });
assert.equal(list.children.length, 2, 'Duplicate completion ignored');
console.log('PASS history: stable identity, next/previous/empty focus, persisted outcomes, failures, stale replies and navigation');

// The real read protocol rejects responses from before a mutation and from older reads.
document.body.dataset.page = 'historyDrawer';
render(['a', 'b']);
ctx.requestHistory(true);
const obsoleteRead = ctx.historyReadRequest;
requestId = remove('b');
render(['a'], { requestId, deleted: true });
render(['a', 'b'], { requestId: obsoleteRead });
assert.equal(list.children.length, 1, 'Pre-deletion read cannot restore a deleted row');
ctx.requestHistory(true);
const olderRead = ctx.historyReadRequest;
ctx.requestHistory(true);
const newestRead = ctx.historyReadRequest;
render(['c'], { requestId: newestRead });
render(['a'], { requestId: olderRead, error: 'Late error' });
render(['a'], { requestId: newestRead });
assert.ok(button('c', 'open'), 'Older errors and duplicate completions are ignored');
ctx.requestHistory(true);
const previousPageRead = ctx.historyReadRequest;
ctx.historyPageVersion++;
ctx.requestHistory(true);
render(['a'], { requestId: previousPageRead });
assert.ok(button('c', 'open'), 'Reopened page keeps the new read pending');
render(['c', 'b'], { requestId: ctx.historyReadRequest });
requestId = remove('b');
const beforeInvalidation = historyReads;
ctx.historyChanged();
assert.equal(historyReads, beforeInvalidation, 'Invalidation is deferred during mutation');
render(['c'], { requestId, deleted: true });
assert.equal(historyReads, beforeInvalidation + 1);
const refresh = ctx.historyReadRequest;
assertFocus('c');
render(['c'], { requestId: refresh });
assertFocus('c');
requestId = remove('c');
ctx.historyChanged();
ctx.failHistoryDelete({ requestId });
assert.ok(ctx.historyReadRequest, 'Failed deletion also releases a deferred refresh');
console.log('PASS history read correlation: mutation barrier, reverse order, duplicate/error, reopened page, deferred invalidation');
