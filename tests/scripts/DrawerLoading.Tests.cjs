const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const timers = new Map(), nodes = new Map(), sent = [];
let sequence = 0;
function element() {
  return { textContent: '', innerHTML: '', children: [], listeners: {},
    appendChild(c) { this.children.push(c); }, addEventListener(k, f) { this.listeners[k] = f; } };
}
const context = vm.createContext({
  historyMutation: null, historyReadRequest: null, historyRefreshPending: false,
  historyRequestPage: 'drawer-test', historyRequestSequence: 0,
  $: id => { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); },
  el: () => element(), post: m => sent.push(m),
  setTimeout: (fn, ms) => { assert.equal(ms, 15000); timers.set(++sequence, fn); return sequence; },
  clearTimeout: id => timers.delete(id)
});
const first = html.indexOf('  // ---------- Drawer loading ----------');
const last = html.indexOf('  // ---------- Drawers ----------', first);
new vm.Script(html.slice(first, last)).runInContext(context);
context.requestHistory();
assert.equal(timers.size, 1);
context.requestHistory();
assert.equal(timers.size, 1, 'retry replaces the previous timeout');
context.finishDrawerLoad('history');
assert.equal(timers.size, 0, 'a response stops the timeout');
context.requestHistory();
const timeout = [...timers.values()][0];
timeout();
assert.match(nodes.get('historyList').textContent, /Puoi riprovare/);
assert.equal(sent.filter(m => m.type === 'list_history').length, 3);
nodes.get('historyList').children[0].listeners.click();
assert.equal(sent.filter(m => m.type === 'list_history').length, 4);
assert.ok(sent.every(m => m.type === 'list_history'), 'UI timeout must not cancel backend work');
context.beginDrawerLoad('flow', element(), () => {});
context.finishDrawerLoad('flow');
assert.equal(context.drawerLoads.flow, undefined);
assert.match(html, /id="jobProgress"[^>]*role="progressbar"[^>]*aria-label=/);
console.log('PASS drawer loading: response, timeout, retry and no backend cancellation');
