const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function section(start, end) {
  const first = html.indexOf(start);
  const last = html.indexOf(end, first);
  assert.ok(first >= 0 && last > first, `Pane section missing: ${start}`);
  return html.slice(first, last);
}

let activeElement = null;
function element(tag, className, text) {
  let ownText = text || '';
  const node = {
    tag, tagName: tag.toUpperCase(), className: className || '', dataset: {}, attributes: {}, children: [], listeners: {}, hidden: false, disabled: false, style: {}, scrollTop: 0,
    get textContent() { return ownText; },
    set textContent(value) { ownText = value; this.children = []; },
    get innerHTML() { return ownText; },
    set innerHTML(value) { ownText = value; this.children = []; },
    appendChild(child) { this.children.push(child); return child; },
    setAttribute(key, value) { this.attributes[key] = value; },
    getAttribute(key) { return this.attributes[key] ?? null; },
    addEventListener(type, callback) { this.listeners[type] = callback; },
    click() { if (!this.disabled) this.listeners.click?.(); },
    focus() { activeElement = this; },
    querySelectorAll(selector) { return descendants(this).slice(1).filter(n => selector === '[data-ui-key]' ? n.dataset.uiKey : selector.startsWith('.') ? n.className.split(' ').includes(selector.slice(1)) : n.tag === selector); },
    querySelector(selector) { return this.querySelectorAll(selector)[0] || (selector === '.drawer-body' ? this : null); },
    scrollIntoView() {}, contains(child) { return descendants(this).includes(child); }
  };
  node.classList = {
    contains: cls => node.className.split(' ').includes(cls),
    toggle(cls, on) { node.className = node.className.split(' ').filter(c => c && c !== cls).concat(on ? [cls] : []).join(' '); }
  };
  return node;
}
function descendants(root) {
  return [root, ...root.children.flatMap(descendants)];
}

const nodes = new Map();
const node = id => {
  if (!nodes.has(id)) nodes.set(id, element('div'));
  return nodes.get(id);
};
const body = element('div', 'drawer-body');
node('analysisDrawer').appendChild(body);
for (const id of ['flowList', 'flowPossible', 'flowCoverage']) body.appendChild(node(id));
const messages = [];
const state = { busy: false, configOk: true, analysis: { preset: 'reconstruct' } };
const context = vm.createContext({
  document: { get activeElement() { return activeElement; } },
  finishDrawerLoad: () => {}, beginDrawerLoad: () => {}, announce: () => {},
  $: node, state, el: element, post: message => messages.push(message),
  formatCount: n => n.toLocaleString('it-IT'),
  openDrawer: () => { node('analysisDrawer').hidden = false; },
  closeDrawers: () => { node('analysisDrawer').hidden = true; },
  jobShown: null, otherJobs: []
});
new vm.Script(section('  // ---------- Structured cards ----------', '  // ---------- Correspondence flow')).runInContext(context);
new vm.Script(section('  // ---------- Correspondence flow ----------', '  // ---------- Mail archive')).runInContext(context);

const deliver = data => context.renderFlow({ requestId: context.flow.requestId, ...data });
const root = { id: 1, entryId: 'root', storeId: 'pst', subject: 'Offerta', date: '01/10/2026', sender: 'Alice', bodyState: 'indexed' };
const missing = { id: 2, entryId: 'missing', storeId: 'pst', subject: 'Risposta', date: '02/10/2026', sender: 'Bob',
  bodyState: 'pending', parentEntryId: 'root', parentStoreId: 'pst', attachments: ['contratto.pdf'] };
const visible = { id: 0, entryId: 'live', storeId: 'pst', subject: 'Copia', date: '03/10/2026',
  visibleOnly: true, bodyState: 'pending' };
node('analysisDrawer').hidden = false;
deliver({ seed: { id: 1, entryId: 'root', storeId: 'pst' }, items: [root, missing, visible],
  possible: [{ ...root, id: 4, entryId: 'possible' }], coverage: [], limitReached: false });
assert.match(node('flowStatus').textContent, /3 email nella pratica/);
const rows = node('flowList').children;
assert.equal(rows.length, 3);
assert.match(descendants(rows[1]).map(e => e.textContent).join(' '), /corpo non indicizzato/);
assert.match(descendants(rows[1]).map(e => e.textContent).join(' '), /Allegati: contratto.pdf/);
assert.match(descendants(rows[1]).map(e => e.textContent).join(' '), /risposta a Offerta/);
assert.match(descendants(rows[2]).map(e => e.textContent).join(' '), /visibile in Outlook, non ancora indicizzata/);
assert.equal(descendants(rows[2]).find(e => e.textContent === 'Apri originale').disabled, false);
assert.equal(node('flowPossible').children.length, 3);

node('flowAttachments').checked = true;
node('flowAnalyze').click();
const request = messages.pop();
assert.equal(request.type, 'mail_analysis');
assert.equal(request.scope.kind, 'flow');
assert.equal(request.scope.attachments, true);
assert.equal(request.scope.entryId, 'root');
assert.equal('max' in request || 'confirmed' in request, false); // A124: no field the host never reads

node('analysisDrawer').hidden = false;
deliver({ seed: { id: 1, entryId: 'root', storeId: 'pst' }, items: Array(20000).fill(root),
  coverage: [], limitReached: true });
assert.match(node('flowStatus').textContent, /cronologia mostra le prime 20.000 email/);
assert.equal(node('flowAnalyze').disabled, false);
const before = messages.length;
node('flowAnalyze').click();
assert.equal(messages.length, before + 1);
assert.equal(messages.pop().scope.kind, 'flow');

new vm.Script(section('  function addSources(container, webSources, mailSources, text) {', '  // A question from the host')).runInContext(context);
const sources = element('div');
context.addSources(sources, [], [
  { Subject: 'Offerta', EntryId: 'root', StoreId: 'pst' },
  { Subject: 'Copia', EntryId: 'copy', StoreId: 'gmail' },
  { Subject: 'Originale mancante' }
]);
const buttons = descendants(sources).filter(e => e.tag === 'button');
assert.equal(buttons.length, 2);
assert.match(buttons[0].textContent, /^\[M1\] Offerta/);
assert.equal(buttons[1].textContent, '[M2] Copia');
buttons[0].click();
assert.equal(JSON.stringify(messages.pop()), JSON.stringify({ type: 'archive_open_email', entryId: 'root', storeId: 'pst' }));
buttons[1].click();
assert.equal(JSON.stringify(messages.pop()), JSON.stringify({ type: 'archive_open_email', entryId: 'copy', storeId: 'gmail' }));

new vm.Script(section("  $('jobStop').addEventListener", '  // ---------- Conversation turns ----------')).runInContext(context);
node('jobStop').click();
assert.equal(messages.pop().type, 'cancel');
// The bar showing another window's work stops that work, not this pane's.
context.jobShown = 'other';
context.otherJobs = [{ id: 7, window: 'finestra principale', action: 'Riepilogo', done: 1, total: 4 }];
node('jobStop').click();
assert.equal(JSON.stringify(messages.pop()), JSON.stringify({ type: 'stop_other', id: 7 }));

const cancelled = {
  current: {
    phase: { remove() {} }, body: element('div'), assistant: element('div'), turn: element('div'),
    text: '', startedAt: Date.now(), kind: 'action', action: 'Ricostruisci la pratica', cloud: false
  }, busy: true
};
const cancelContext = vm.createContext({
  state: cancelled, clearTimeout: () => {}, isNearBottom: () => false, scrollToBottom: () => {},
  renderMarkdown: text => text, announce: () => {}, el: element, openDrawerId: () => null,
  hideConfirm: () => {}, setBusy: value => { cancelled.busy = value; }
});
new vm.Script(section('  function finishTurn(result) {', '  // ---------- Toast ----------')).runInContext(cancelContext);
const turn = cancelled.current;
cancelContext.finishTurn({ status: 'cancelled', message: 'Invio al provider cloud annullato.' });
assert.equal(cancelled.current, null);
assert.equal(cancelled.busy, false);
assert.match(turn.assistant.children[0].textContent, /Invio al provider cloud annullato/);
assert.equal(descendants(turn.assistant).filter(e => e.className === 'msg-sources').length, 0);

console.log('PASS flow pane: missing originals, attachment request, sources, cancellation and full analysis beyond the view limit');

// E5: one logical message exposes every copy and grounds shared-reference links as text.
node('analysisDrawer').hidden = false; state.busy = false;
node('flowList').children = [];
deliver({ seed: { id: 1 }, metadataIncomplete: true, factsIncomplete: true, items: [{ ...root,
  outsideFilters: true, copies: [{ entryId: 'root', storeId: 'pst' }, { entryId: 'copy', storeId: 'other' }],
  linkReasons: ['Riferimento condiviso: Order ORD-2026/118'], linkFacts: [{ source: 'order.txt', context: '<img src=x onerror=alert(1)> ORD-2026/118' }]
}], coverage: [], possible: [] });
const logical = node('flowList').children[0];
assert.match(descendants(logical).map(e => e.textContent).join(' '), /correlata fuori dai filtri iniziali/);
assert.match(descendants(logical).map(e => e.textContent).join(' '), /Riferimento condiviso: Order ORD-2026\/118/);
assert.match(descendants(logical).map(e => e.textContent).join(' '), /order.txt: <img src=x onerror=alert\(1\)>/);
assert.match(node('flowStatus').textContent, /Intestazioni della catena incomplete/);
assert.match(node('flowStatus').textContent, /Estrazione dei fatti incompleta/);
descendants(logical).find(e => /^Apri copia/.test(e.textContent)).click();
assert.equal(messages.at(-1).entryId, 'copy'); assert.equal(messages.at(-1).storeId, 'other');

// A105: a new request clears the previous flow at once, and a refusal shows its reason in the panel.
context.openFlow(1, 'root', 'pst');
assert.equal(node('flowList').children.length, 0);
assert.equal(node('flowPossible').children.length, 0);
assert.equal(node('flowAnalyze').disabled, true);
assert.equal(messages.at(-1).type, 'mail_analysis');
assert.equal(messages.at(-1).goal.preset, 'reconstruct');
deliver({ seed: { id: 1 }, items: [root], possible: [root], coverage: [] });
deliver({ seed: { id: 1 }, items: [], possible: [], coverage: [],
  refused: "Questa cassetta è esclusa dall'archivio: il flusso non può essere ricostruito." });
assert.equal(node('flowList').children.length, 0);
assert.equal(node('flowPossible').children.length, 0);
assert.equal(node('flowAnalyze').disabled, true);
assert.equal(node('flowCoverage').textContent, '');
assert.equal(node('flowStatus').textContent, "Questa cassetta è esclusa dall'archivio: il flusso non può essere ricostruito.");
// A later valid result drops the refusal.
deliver({ seed: { id: 1 }, items: [root], possible: [], coverage: [] });
assert.match(node('flowStatus').textContent, /1 email nella pratica/);
console.log('PASS flow pane: refusal clears the previous flow and shows its reason');

// A174: untrusted long text is literal, actions have independent destinations, and refresh retains view state.
node('analysisDrawer').hidden = false;
const detailedMail = { ...root, subject: 'Oggetto' + 'SenzaSpazi'.repeat(40),
  copies: [{ entryId: 'copy-other', storeId: 'other', folderPath: 'Cartella' + 'Lunga'.repeat(40) }] };
const partialCoverage = [{ StoreId: 'other', DisplayName: 'Archivio secondario', State: 'cache', Emails: 8,
  BodiesIndexed: 7, AttachmentsIndexed: 3, Error: 'Cartella non accessibile', CacheChecked: false, AbsenceVerified: false }];
deliver({ seed: root, items: [detailedMail], coverage: partialCoverage });
let all = descendants(node('analysisDrawer'));
const detailsBefore = all.find(n => n.tag === 'details' && n.dataset.uiKey?.endsWith(':details'));
detailsBefore.open = true;
body.scrollTop = 245;
deliver({ seed: root, items: [detailedMail], coverage: partialCoverage });
all = descendants(node('analysisDrawer'));
assert.equal(body.scrollTop, 245);
assert.equal(all.find(n => n.dataset.uiKey === detailsBefore.dataset.uiKey).open, true);
assert.match(node('flowStatus').textContent, /Copertura incompleta/);
assert.match(descendants(node('flowWarnings')).map(n => n.textContent).join(' '), /Cartella non accessibile.*controllo completo/);
// A real folder reason (path of about 70 characters) stays whole; only a longer one is cut, at a word end.
const folderReason = 'Cartella «utente@example.test\\[Gmail]\\Cestino\\TEST OnlyWorkspace E7 (solo questo computer)»: Cartella non disponibile; nessuna eliminazione dedotta. Controlla in Outlook, anche nel Cestino; ripristinala solo se necessario, senza ricrearla se eliminata. Lascia aperta la cassetta per il prossimo controllo completo.';
const warningFor = error => { deliver({ seed: root, items: [detailedMail], coverage: [{ ...partialCoverage[0], Error: error }] });
  return descendants(node('flowWarnings')).map(n => n.textContent).join(' '); };
assert.ok(warningFor(folderReason).includes(folderReason));
const longReason = warningFor('parola '.repeat(80));
assert.match(longReason, /parola…/); assert.doesNotMatch(longReason, /parol…|par…/);
deliver({ seed: root, items: [detailedMail], coverage: partialCoverage });
all = descendants(node('analysisDrawer'));
const copyButton = all.find(n => /^Apri copia/.test(n.textContent));
copyButton.click();
assert.equal(messages.at(-1).entryId, 'copy-other'); assert.equal(messages.at(-1).storeId, 'other');
deliver({ seed: { ...root, entryId: 'different' }, items: [detailedMail], coverage: [] });
console.log('PASS A174 structured cards: literal subjects, partial coverage, exact destinations and stable focus/expansion');

// Reconstruction never emits an AI request until the explicit summary action.
state.configOk = false;
context.openFlow(1, 'root', 'pst');
assert.equal(messages.at(-1).goal.preset, 'reconstruct');
const pending = context.flow.requestId;
context.renderFlow({ requestId: pending - 1, seed: root, items: [root] });
assert.equal(node('flowList').children.length, 0, 'late reconstruction cannot replace the latest result');
deliver({ seed: root, items: [root] });
assert.equal(node('flowList').children.length, 1);
assert.equal(node('flowAnalyze').disabled, true, 'a summary still requires an AI profile');
assert.doesNotMatch(html, /id="flowDrawer"|id="flowCase"|case_close|case_reopen/);
assert.match(html, /data-preset="reconstruct"/);
console.log('PASS A208 local reconstruction, explicit synthesis and stale-request rejection');
