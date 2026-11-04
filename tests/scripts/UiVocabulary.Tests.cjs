const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

const root = path.join(__dirname, '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const html = read('src/OnlyWorkspace.Outlook/UI/TaskPane.html');

// One glyph per StatusLight state, defined once as --glyph and inherited by every pane badge
// (docs/tecnico/interfaccia-vocabolario.md).
const glyphs = { Ok: '✓', Error: '!', Paused: '‖', Waiting: '…', Working: '', Off: '–' };
assert.match(html, /\[data-light\] \{ --glyph: '–'; \}/, 'default glyph (Off) is not defined');
for (const [state, glyph] of Object.entries(glyphs)) {
  if (state === 'Off') continue; // default of the [data-light] rule
  const rule = new RegExp(`\\[data-light='${state}'\\] \\{ --glyph: '(.*?)'; \\}`).exec(html);
  assert.ok(rule, `no --glyph for ${state}`);
  assert.equal(rule[1], glyph, `${state} glyph`);
}
assert.match(html, /\.ind-badge::before, \.state-symbol::before, \.status-text::before \{ content: var\(--glyph/, 'badges must read --glyph');
// No second copy of the glyphs: not in JS (stateBadge) and not in per-badge CSS rules.
assert.doesNotMatch(html, /Ok: '✓'/, 'glyph map duplicated in JavaScript');
assert.doesNotMatch(html, /::before \{ content: '[✓!‖…–]'/, 'glyph repeated in a per-badge CSS rule');
// «In corso» colours come from one pair of variables, not repeated literals (the dark theme overrides the edge).
assert.doesNotMatch(html.replace(/--work-(fill|edge): #[0-9a-f]{6};/g, ''), /#fcd116|#8a5a00|#e0a030/i, 'hard-coded working colours outside the variables');
assert.match(html, /prefers-color-scheme: dark[\s\S]*--work-edge: #[0-9a-f]{6};/, 'dark theme has no --work-edge override');
// Decorative duplicates stay removed; the warning marker is the shared icon, not a text symbol.
for (const unused of ['person', 'pc', 'contact', 'pulse', 'attach']) {
  assert.doesNotMatch(html, new RegExp(`<symbol id="i-${unused}"`), `unused icon i-${unused} is back`);
}
assert.doesNotMatch(html, /'⚠ '/, 'text warning symbol instead of icon("warning")');
// Every <symbol> is referenced somewhere, statically or through icon()/actionButton().
for (const [, id] of html.matchAll(/<symbol id="i-([a-z-]+)"/g)) {
  const used = html.includes(`href="#i-${id}"`) || html.includes(`icon('${id}')`) || html.includes(`actionButton('${id}'`)
    || new RegExp(`['"]${id}['"]`).test(html.replace(/<symbol[^\n]*<\/symbol>/g, ''));
  assert.ok(used, `icon i-${id} is defined but never used`);
}

// Glossary: the pane's Interrompi button produces «interrotta», never «annullata» (ConfigTool buttons say Annulla).
const coordinators = fs.readdirSync(path.join(root, 'src/OnlyWorkspace.Outlook/UI')).filter(file => /^TaskPaneCoordinator.*\.cs$/.test(file));
for (const file of [...coordinators.map(name => 'src/OnlyWorkspace.Outlook/UI/' + name), 'src/OnlyWorkspace.Outlook/UI/TaskPane.html']) {
  assert.doesNotMatch(read(file), /Operazione annullata/, `${file}: usa «Operazione interrotta»`);
}
// ConfigTool label draws the same glyph as the pane for every state.
const label = read('src/OnlyWorkspace.ConfigTool/Forms/OperationalStatusLabel.cs');
assert.match(label, /StatusLight\.Paused \? "‖"/, 'OperationalStatusLabel must draw ‖ for Paused');
assert.doesNotMatch(label, /"▲"/, 'OperationalStatusLabel still draws ▲');
// Texts stay short (A202): one line per Guida entry, long details go in docs/utente/riquadro.md. Privacy and egress texts are exempt.
for (const [, inner] of html.matchAll(/<li>(.*?)<\/li>/g)) {
  const text = inner.replace(/<[^>]+>/g, '');
  if (/profilo IA|ricerca web/.test(text)) assert.ok(text.length <= 180, `Guida privacy text too long (${text.length}): ${text}`);
  else assert.ok(text.length <= 165, `Guida entry too long (${text.length}): ${text}`);
}
for (const [, title] of html.matchAll(/ title="([^"]{1,})"/g)) {
  assert.ok(title.length <= 130, `tooltip too long (${title.length}): ${title}`);
}
console.log('PASS UI vocabulary: one glyph per state, shared working colours, no unused icons, short Guida texts, interrotta/annullata by control');
