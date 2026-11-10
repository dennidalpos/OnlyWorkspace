const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const from = html.indexOf("      case 'job_restore':");
const to = html.indexOf("      case 'stream_context':", from);
assert.ok(from > 0 && to > from);
const turns = [];
const state = { current: null, job: {} };
const ctx = vm.createContext({
  state, hideEmailContext() {}, closeDrawers() {}, updateJobBar() {}, setBusy() {}, announce() {},
  createTurn(kind, action) { const turn = { kind, action, text: '', cancelled: false }; turns.push(turn); return turn; },
  finishTurn() { state.current.cancelled = true; state.current = null; }
});
vm.runInContext('function onHostMessage(data) { switch(data.type) {' + html.slice(from, to) +
  "case 'stream_chunk': if(state.current) state.current.text += data.chunk; break; }}", ctx);
const start = { type: 'stream_start', turnId: '1:7', action: 'Sintesi' };
const restore = { type: 'job_restore', turnId: '1:7', messages: [start, { type: 'stream_chunk', chunk: 'Originale intero' }] };
// Pending queue followed by ControlReady: no second bubble, no cancelled first turn, no doubled text.
ctx.onHostMessage(start);
ctx.onHostMessage({ type: 'stream_chunk', chunk: 'Originale intero' });
ctx.onHostMessage(restore);
ctx.onHostMessage(restore);
assert.equal(turns.length, 1);
assert.equal(state.current.text, 'Originale intero');
assert.equal(turns[0].cancelled, false);
// A fresh page reconstructs the complete text, once, and continues streaming.
state.current = null;
ctx.onHostMessage(restore);
ctx.onHostMessage({ type: 'stream_chunk', chunk: ' e seguito' });
assert.equal(state.current.text, 'Originale intero e seguito');
assert.equal(turns.length, 2);
// An intentional new action still supersedes its predecessor.
ctx.onHostMessage({ type: 'stream_start', turnId: '1:8', action: 'Estrai dati' });
assert.equal(turns[1].cancelled, true);
assert.equal(turns.length, 3);

function node(tag, cls, text) { return { tag, text, children: [], appendChild(n) { this.children.push(n); }, addEventListener() {} }; }
ctx.el = node;
ctx.post = () => {};
const sourcesFrom = html.indexOf('  function addSources(');
const sourcesTo = html.indexOf('  // A question from the host', sourcesFrom);
vm.runInContext(html.slice(sourcesFrom, sourcesTo), ctx);
const container = node('div');
ctx.addSources(container, [
  { Title: 'Non citata', Url: 'https://example.test/1' },
  { Title: 'Citata', Url: 'https://example.test/2' }
], [], 'Riscontro [W2]');
assert.equal(container.children[0].children.length, 2);
assert.equal(container.children[0].children[1].text, '[W2] Citata');
const saved = node('div');
ctx.addSources(saved, [{ CitationNumber: 2, Title: 'Citata', Url: 'https://example.test/2' }], [], 'Riscontro [W2]');
assert.equal(saved.children[0].children[1].text, '[W2] Citata');
ctx.conv = node('div');
ctx.clearConversation = () => { ctx.conv.children = []; };
ctx.hideEmptyState = ctx.scrollToBottom = ctx.focusComposer = ctx.updateActions = () => {};
const historyFrom = html.indexOf('  function renderLoadedConversation(data)');
const historyTo = html.indexOf("  $('btnCheckHealth')", historyFrom);
vm.runInContext(html.slice(historyFrom, historyTo), ctx);
ctx.renderLoadedConversation({ policyChanged: true, messages: [] });
assert.match(ctx.conv.children.at(-1).text, /nuova conversazione.*storico precedente resta consultabile/i);
ctx.renderLoadedConversation({ policyChanged: false, messages: [] });
assert.equal(ctx.conv.children.at(-1).text, 'Scrivi qui sotto per continuare questa conversazione.');
console.log('PASS queue/replay, page reload, stable sources and explicit history policy warning');
