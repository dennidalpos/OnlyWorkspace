const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

const root = path.join(__dirname, '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const html = read('src/OnlyWorkspace.Outlook/UI/TaskPane.html');

// One glyph per StatusLight state in every pane badge (docs/tecnico/interfaccia-vocabolario.md).
const glyphs = { Ok: '✓', Error: '!', Paused: '‖', Waiting: '…', Off: '–' };
const symbols = html.match(/\(\{ Ok: '(.)', Error: '(.)', Working: '', Paused: '(.)', Waiting: '(.)' \}\)\[light\] \|\| '(.)'/);
assert.ok(symbols, 'stateBadge glyph map not found');
assert.deepEqual(symbols.slice(1), [glyphs.Ok, glyphs.Error, glyphs.Paused, glyphs.Waiting, glyphs.Off], 'stateBadge glyphs');
for (const cls of ['ind-badge', 'status-text']) {
  for (const [state, glyph] of Object.entries(glyphs)) {
    const rule = new RegExp(`\\[data-light='${state}'\\](?: > \\.${cls}|\\.${cls}\\[data-light='${state}'\\])?[^{]*${cls}[^{]*::before \\{ content: '(.)'`).exec(html)
      || new RegExp(`\\.${cls}\\[data-light='${state}'\\]::before \\{ content: '(.)'`).exec(html);
    if (state === 'Off') continue; // default content of the badge
    assert.ok(rule, `${cls} has no glyph for ${state}`);
    assert.equal(rule[1], glyph, `${cls} ${state} glyph`);
  }
}
// «In corso» colours come from one pair of variables, not repeated literals.
assert.doesNotMatch(html.replace(/--work-(fill|edge): #[0-9a-f]{6};/g, ''), /#fcd116|#8a5a00/i, 'hard-coded working colours outside the variables');

// Glossary: the pane's Interrompi button produces «interrotta», never «annullata» (ConfigTool buttons say Annulla).
const coordinators = fs.readdirSync(path.join(root, 'src/OnlyWorkspace.Outlook/UI')).filter(file => /^TaskPaneCoordinator.*\.cs$/.test(file));
for (const file of [...coordinators.map(name => 'src/OnlyWorkspace.Outlook/UI/' + name), 'src/OnlyWorkspace.Outlook/UI/TaskPane.html']) {
  assert.doesNotMatch(read(file), /Operazione annullata/, `${file}: usa «Operazione interrotta»`);
}
console.log('PASS UI vocabulary: one glyph per state, shared working colours, interrotta/annullata by control');
