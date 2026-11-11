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
// Every panel opens at its top, and focusing its first field or title does not scroll it.
{
  const start = html.indexOf('  function focusDrawer(id) {');
  const end = html.indexOf('  function openDrawer(id) {', start);
  assert.ok(start > 0 && end > start, 'focusDrawer and drawerToTop are next to openDrawer');
  const focusOptions = [];
  const body = { scrollTop: 480 };
  const title = { focus: o => focusOptions.push(o) };
  const drawer = { querySelector: s => s === '.drawer-body' ? body : s === '.drawer-header h2' ? title : null };
  const pane = vm.createContext({ $: () => drawer });
  new vm.Script(html.slice(start, end)).runInContext(pane);
  pane.drawerToTop('infoDrawer');
  assert.equal(body.scrollTop, 0);
  pane.focusDrawer('infoDrawer');
  assert.equal(JSON.stringify(focusOptions), JSON.stringify([{ preventScroll: true }]));
  const open = html.slice(html.indexOf('  function openDrawer(id) {'), html.indexOf('  // ---------- Structured cards ----------'));
  assert.ok(open.indexOf('drawerToTop(id);') > 0 && open.indexOf('drawerToTop(id);') < open.indexOf('focusDrawer(id);'));
  assert.doesNotMatch(html, /\$\('infoTitle'\)\.scrollIntoView/);
  assert.match(html, /close\.focus\(\{ preventScroll: true \}\);\s*modal\.scrollTop = 0;/);
}
console.log('PASS drawers and help open at their top');
