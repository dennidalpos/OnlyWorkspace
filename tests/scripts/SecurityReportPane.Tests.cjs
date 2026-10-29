const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const from = html.indexOf('  // ---------- Security check report ----------');
const to = html.indexOf('  function finishTurn(result) {', from);
assert.ok(from > 0 && to > from);

// Minimal DOM: enough for text nodes, attributes, hidden, replacement and click handlers.
function node(tag, cls, text) {
  const n = {
    tag, className: cls || '', children: [], attrs: {}, hidden: false, handlers: {}, parent: null,
    get childNodes() { return this.children; },
    get textContent() { return (this.text || '') + this.children.map(c => c.textContent).join(''); },
    set textContent(v) { this.text = v; this.children = []; },
    appendChild(c) { c.parent = this; this.children.push(c); return c; },
    insertBefore(c, ref) { c.parent = this; const i = this.children.indexOf(ref); this.children.splice(i < 0 ? this.children.length : i, 0, c); return c; },
    replaceWith(c) { const p = this.parent; const i = p.children.indexOf(this); c.parent = p; p.children[i] = c; },
    setAttribute(k, v) { this.attrs[k] = String(v); },
    getAttribute(k) { return this.attrs[k]; },
    addEventListener(type, fn) { this.handlers[type] = fn; },
    click() { this.handlers.click && this.handlers.click(); },
    querySelector(sel) {
      const m = /^\.([\w-]+)\[aria-expanded="(\w+)"\]$/.exec(sel);
      const all = []; (function walk(x) { x.children.forEach(c => { all.push(c); walk(c); }); })(this);
      return all.find(c => m ? c.className.split(' ').includes(m[1]) && c.attrs['aria-expanded'] === m[2] : c.className.split(' ').includes(sel.slice(1))) || null;
    },
    find(cls) { return this.querySelector('.' + cls); },
    findAll(cls) { const all = []; (function walk(x) { x.children.forEach(c => { if (c.className.split(' ').includes(cls)) all.push(c); walk(c); }); })(this); return all; }
  };
  if (text != null) n.text = String(text);
  return n;
}
const ctx = vm.createContext({ document: { createTextNode: t => node('#text', '', t) } });
ctx.el = node;
vm.runInContext(html.slice(from, to), ctx);

const done = {
  v: 1, state: 'done', outcome: 'suspicious', partial: true,
  reasons: ['Un link mostra «contoso.com» ma porta a evil.example.invalid.', 'Secondo motivo', 'Terzo motivo'],
  action: 'Non aprire il link; raggiungi il sito digitando tu l’indirizzo che conosci.',
  observations: [{ kind: 'deception', origin: 'local', text: 'Un link mostra «contoso.com»…', source: 'Email', quote: '<img src=x onerror=alert(1)>' },
                 { kind: 'context', origin: 'declared', text: 'Il server dichiara DMARC superato' }],
  defender: [{ status: 'threat', text: 'Defender ha segnalato una minaccia in «a.zip».' }, { status: 'clean', text: 'Defender non ha segnalato minacce in «b.pdf».' }],
  limits: ['«c.pdf»: protetto da password, contenuto non letto.'], model: 'Lettura IA del contenuto completata.'
};

// Fixed values only: colour class, symbol and label come from the table, at most two reasons.
const card = ctx.securityCard(done, false);
const badge = card.find('sec-badge');
assert.ok(badge.className.includes('sec-suspicious'));
assert.equal(badge.textContent, '✕Contenuto sospetto');
assert.equal(badge.find('sec-symbol').attrs['aria-hidden'], 'true');
assert.equal(card.find('sec-reasons').children.length, 2);
assert.ok(card.findAll('sec-chip').some(c => c.text === 'Controllo parziale'));
assert.match(card.find('sec-defender').textContent, /^Microsoft Defender: Defender ha segnalato una minaccia in «a\.zip»/);
assert.match(card.find('sec-action').textContent, /^Cosa fare: Non aprire il link/);
// Source text stays text: the quote is a text node value, never markup.
const details = card.find('sec-details');
assert.equal(details.hidden, true);
assert.ok(details.textContent.includes('«<img src=x onerror=alert(1)>»'));
assert.ok(details.textContent.includes('Dichiarato dal server · '));
assert.ok(details.textContent.includes('Limiti dei controlli'));

// Details open and close from the keyboard-reachable button, with aria-expanded kept in step.
const toggle = card.find('sec-toggle');
assert.equal(toggle.attrs['aria-controls'], details.id);
toggle.click();
assert.equal(details.hidden, false);
assert.equal(toggle.attrs['aria-expanded'], 'true');

// Running: no early green; a problem already found stays visible with «Verifica in corso».
const running = ctx.securityCard({ v: 1, state: 'running', outcome: 'none', reasons: [], observations: [], defender: [], limits: [] }, false);
assert.equal(running.find('sec-badge').textContent, '…Verifica in corso');
assert.equal(running.find('sec-action'), null);
const runningRed = ctx.securityCard(Object.assign({}, done, { state: 'running' }), false);
assert.ok(runningRed.find('sec-badge').className.includes('sec-suspicious'));
assert.ok(runningRed.findAll('sec-chip').some(c => c.text === 'Verifica in corso'));

// Green says only what the checks found, never «sicura».
const green = ctx.securityCard({ v: 1, state: 'done', outcome: 'none', partial: false, reasons: [], action: 'Nessuna azione suggerita.', observations: [], defender: [], limits: [] }, false);
assert.equal(green.find('sec-badge').textContent, '✓Nessuna anomalia rilevata');
assert.equal(green.find('sec-reasons').children[0].text, 'Nessun problema emerso nei controlli eseguiti.');
assert.ok(!/sicur|pulit|attendibil/i.test(green.textContent));

// Values outside the table are ignored: no badge from model output or the source's HTML.
for (const bad of [null, {}, { v: 2, state: 'done', outcome: 'none' }, { v: 1, state: 'done', outcome: 'safe' }, { v: 1, state: 'x', outcome: 'none' }])
  assert.equal(ctx.securityCard(bad, false), null);

// The final report replaces the provisional one in the same turn, keeping open details open.
const assistant = node('div', 'msg');
const body = assistant.appendChild(node('div', 'msg-body'));
const turn = { assistant, body };
ctx.showSecurityReport(turn, Object.assign({}, done, { state: 'running' }));
turn.securityEl.find('sec-toggle').click();
ctx.showSecurityReport(turn, done);
assert.equal(assistant.findAll('sec-report').length, 1);
assert.equal(assistant.children[0], turn.securityEl);
assert.equal(turn.securityEl.find('sec-details').hidden, false);
ctx.showSecurityReport(turn, { v: 1, state: 'done', outcome: 'bogus' });
assert.equal(turn.securityReport.outcome, 'suspicious');

assert.match(ctx.securityPlainText(done), /^Esito: Contenuto sospetto \(controllo parziale\)\n- Un link mostra/);

// History: an entry with the structured report shows the card; older ones keep their text.
ctx.conv = node('div');
ctx.clearConversation = () => { ctx.conv.children = []; };
ctx.hideEmptyState = ctx.scrollToBottom = ctx.focusComposer = ctx.updateActions = ctx.addSources = ctx.addResponseActions = () => {};
ctx.mailChip = () => node('span');
ctx.renderMarkdown = text => text;
const historyFrom = html.indexOf('  function renderLoadedConversation(data)');
const historyTo = html.indexOf("  $('btnCheckHealth')", historyFrom);
vm.runInContext(html.slice(historyFrom, historyTo), ctx);
ctx.renderLoadedConversation({ messages: [
  { role: 'user', content: 'Verifica sicurezza: x', action: 'Verifica sicurezza' },
  { role: 'assistant', content: '**Esito: Contenuto sospetto**\n\n**Riferimenti web (separati)**\nNessuna', security: done },
  { role: 'user', content: 'Verifica sicurezza: vecchia', action: 'Verifica sicurezza' },
  { role: 'assistant', content: '**Giudizio finale: Alto rischio**' }
] });
const turns = ctx.conv.children.filter(c => c.className === 'turn');
const newer = turns[0].children.find(c => c.className.includes('msg-assistant'));
assert.ok(newer.find('sec-report'));
assert.equal(newer.find('msg-body').innerHTML, '**Riferimenti web (separati)**\nNessuna');
const older = turns[1].children.find(c => c.className.includes('msg-assistant'));
assert.equal(older.find('sec-report'), null);
assert.equal(older.find('msg-body').innerHTML, '**Giudizio finale: Alto rischio**');

console.log('PASS security badge from fixed values, details, provisional report, plain text and history');
