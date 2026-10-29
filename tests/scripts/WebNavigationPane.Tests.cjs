const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
// Four labelled sections; Persons owns Counterparties and Address book.
for (const [id, title] of [['btnChatPage', 'Chat'], ['btnSearchPage', 'Posta'], ['btnPartiesPage', 'Persone'], ['btnHistory', 'Storico']]) {
  assert.match(html, new RegExp('id="' + id + '"[^>]*aria-label="' + title + '"[^>]*title="[^"]+"'));
}
assert.match(html, /<header class="topbar">[\s\S]*<nav class="primary-nav"[\s\S]*<\/nav>[\s\S]*<\/header>/);
// The conversation scroller paints its own background, so a theme switch with the pane open repaints it.
assert.match(html, /\.conversation \{[^}]*background: var\(--bg\);/);
// Contrast themes drop the current section's tint: a Highlight frame keeps it visible.
assert.match(html, /@media \(forced-colors: active\) \{[\s\S]*?\.primary-nav button\[aria-current='page'\] \{ border: 2px solid Highlight;/);
// Narrow pane or zoom 125%: the four pages go two by two and the drawers start under the taller bar.
assert.match(html, /@media \(max-width: 339px\) \{\s*:root \{ --topbar-h: 108px; \}\s*\.primary-nav \{ display: grid; grid-template-columns: 1fr 1fr;/);
// Brand as a label beside its name, before the page icons; the profile is chosen only in the ConfigTool (Danny, 26/10/2026).
assert.match(html, /<header class="topbar">\s*<div id="brand" class="brand"[^>]*><svg class="brand-logo"[^>]*aria-hidden="true">[\s\S]*?<\/svg><span>OnlyWorkspace<\/span><\/div>\s*<nav class="primary-nav"/);
assert.doesNotMatch(html, /profileSelect|set_profile|profile_changed|userCanSwitchProfile/);
// Riprova marks the operation as a retry: the host may reuse the emails and web results already read (A150).
assert.match(html, /post\(Object\.assign\(\{\}, op, \{ retry: true \}\)\);/);
assert.match(html, /if \(result\.retryable && result\.op\) addRetry\(c, result\.op\);/);
// An empty answer offers the same Riprova instead of a dead end.
assert.match(html, /Il modello non ha restituito testo\.[^\n]*\n\s*if \(result\.op\) addRetry\(c, result\.op\);/);
assert.doesNotMatch(html, /id="modelBadge"/);
assert.match(html, /id="btnFollowUp"/);
assert.match(html, /body\[data-page='analysisDrawer'\]/);
assert.match(html, /role="region" aria-labelledby="analysisTitle"/);
assert.doesNotMatch(html, /aria-modal="true" aria-labelledby="analysisTitle"/);
assert.match(html, /webSources: data\.webSources, webNotice: data\.webNotice, mailSources: data\.mailSources/);
// A web search that sent only the end of a longer request is declared under the answer (A44).
assert.match(html, /if \(result\.webNotice\) c\.assistant\.appendChild\(el\('div', 'msg-note', result\.webNotice\)\);/);
assert.match(html, /body:not\(\[data-page='chat'\]\) \.conversation/);

function section(start, end) {
  const from = html.indexOf(start);
  const to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from);
  return html.slice(from, to);
}

const sent = [];
const ctx = vm.createContext({
  window: { chrome: { webview: { postMessage: msg => sent.push(msg) } } },
  document: { getElementById: () => null },
});
new vm.Script(section('  var webview =', '  function el(tag, cls, text)')).runInContext(ctx);
// Web Search is decided per action in the ConfigTool: the pane sends no per-action choice.
ctx.post({ type: 'chat', prompt: 'ciao' });
assert.equal(sent[0].useWebSearch, undefined);
assert.doesNotMatch(html, /useWebSearch|confirm_external/);

const nodes = new Map();
for (const id of ['btnChatPage', 'btnSearchPage', 'btnPartiesPage', 'btnContactsPage', 'btnHistory', 'btnInfo']) nodes.set(id, { setAttribute(k, v) { this[k] = v; }, scrollIntoView() {} });
ctx.$ = id => nodes.get(id);
ctx.document.body = { dataset: {} };
new vm.Script(section('  var drawerToggles =', '  function openDrawerId()')).runInContext(ctx);
ctx.showPage('partyDrawer');
assert.equal(ctx.document.body.dataset.page, 'partyDrawer');
assert.equal(nodes.get('btnPartiesPage')['aria-current'], 'page');
assert.equal(nodes.get('btnHistory')['aria-current'], 'false');
ctx.showPage('contactsDrawer');
assert.equal(nodes.get('btnPartiesPage')['aria-current'], 'page');
assert.equal(nodes.get('btnHistory')['aria-current'], 'false');
ctx.showPage('analysisDrawer');
assert.equal(nodes.get('btnSearchPage')['aria-current'], 'page');
assert.equal(ctx.lastPeoplePage, 'contactsDrawer');
ctx.showPage('chat');
assert.equal(ctx.lastPeoplePage, 'contactsDrawer', 'Local reconstruction belongs to Cerca e analizza, independently of Persons.');

// The brand's tooltip names the model and where the data goes; «web» shows when some actions search the web.
const badgeNodes = { brand: { title: '' }, webFlag: { hidden: true, title: '', setAttribute(k, v) { this[k] = v; } } };
const badgeCtx = vm.createContext({ state: {}, $: id => badgeNodes[id] });
new vm.Script(section('  // Which model receives the data', '  // A cloud profile not covered')).runInContext(badgeCtx);
badgeCtx.updateModelBadge({ model: 'qwen3.5:9b', host: '127.0.0.1', webHosts: '', webActions: [] }, false, 'Ollama locale');
assert.equal(badgeNodes.brand.title, 'Ollama locale · qwen3.5:9b\nModello locale: i dati delle azioni IA restano su 127.0.0.1.');
assert.equal(badgeNodes.webFlag.hidden, true);
badgeCtx.updateModelBadge({ model: 'gpt-x', host: 'api.openai.com', webHosts: 'api.openai.com', webActions: ['Chat', 'Follow-up'] }, true, 'OpenAI');
assert.match(badgeNodes.brand.title, /^OpenAI · gpt-x\nModello cloud: i dati delle azioni IA vanno a api\.openai\.com\./);
assert.match(badgeNodes.brand.title, /Ricerca web \(api\.openai\.com\) per: Chat, Follow-up/);
assert.equal(badgeNodes.webFlag.hidden, false);
assert.doesNotMatch(badgeNodes.brand.title, /conferma/);
badgeCtx.updateModelBadge(null, false, '');
assert.equal(badgeNodes.brand.title, 'OnlyWorkspace');

console.log('PASS no per-action web choice, fixed page navigation, brand and model label');

// Clear is a fresh conversation request, never a history deletion, and is refused while either job works.
const clearNodes = new Map(); let hasContent = false;
const clearContext = vm.createContext({
  state: { busy: false }, contacts: { busy: false }, hasTurns: () => hasContent,
  post: m => sent.push(m), runTool: () => {},
  $: id => { if (!clearNodes.has(id)) clearNodes.set(id, { addEventListener: (_, cb) => clearNodes.get(id).click = cb }); return clearNodes.get(id); }
});
new vm.Script(section("  $('btnClearChat').addEventListener", "  conv.addEventListener('click'")).runInContext(clearContext);
const sendCount = sent.length;
clearNodes.get('btnClearChat').click(); assert.equal(sent.length, sendCount);
hasContent = true; clearContext.state.busy = true;
clearNodes.get('btnClearChat').click(); assert.equal(sent.length, sendCount);
clearContext.state.busy = false; clearContext.contacts.busy = true;
clearNodes.get('btnClearChat').click(); assert.equal(sent.length, sendCount);
clearContext.contacts.busy = false; clearNodes.get('btnClearChat').click();
assert.equal(sent.at(-1).type, 'new_chat'); assert.equal(sent.length, sendCount + 1);
console.log('PASS clear chat: no empty reset, no reset during work, fresh conversation preserves separate history deletion');
