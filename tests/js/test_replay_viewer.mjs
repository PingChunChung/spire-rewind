// Exercise the shipped viewer script with a small DOM model and deterministic timers.
// This verifies interaction logic; it does not measure browser layout or rendering.
import test from 'node:test';
import assert from 'node:assert/strict';
import { load, sample, template } from './viewer_harness.mjs';

test('sample renders seven room types, all curves and missing-summary state', () => {
  const v = load();
  assert.equal(v.$('timeline').children.length, 7);
  assert.equal(v.$('trends').children.length, 3);
  assert.equal(v.$('play').disabled, true);
  v.floor(1);
  assert.equal(v.$('position').textContent, '1 / 3');
  assert.match(v.$('frames').textContent, /舊版摘要/);
});

test('next and previous pause at the selected snapshot', () => {
  const v = load(); v.floor(1);
  v.$('next').click(); assert.equal(v.$('position').textContent, '2 / 3');
  v.$('prev').click(); assert.equal(v.$('position').textContent, '1 / 3');
  v.$('prev').click(); assert.equal(v.$('position').textContent, '1 / 3');
  assert.equal(v.timers.size, 0);
});

test('play advances and pause prevents timer advancement', () => {
  const v = load(); v.floor(1); v.$('play').click();
  v.tick(); assert.equal(v.$('position').textContent, '2 / 3');
  v.$('play').click(); assert.equal(v.timers.size, 0);
  v.tick(); assert.equal(v.$('position').textContent, '2 / 3');
});

test('1x, 2x and 4x update timer intervals and really advance', () => {
  for (const speed of [1, 2, 4]) {
    const v = load(); v.floor(1); v.$('speed').value = String(speed); v.$('play').click();
    assert.equal([...v.timers.values()][0].delay, 1000 / speed);
    v.tick(); assert.equal(v.$('position').textContent, '2 / 3');
    v.$('speed').value = String(speed === 4 ? 1 : 4); v.$('speed').onchange();
    assert.equal(v.timers.size, 1);
    assert.equal([...v.timers.values()][0].delay, 1000 / Number(v.$('speed').value));
  }
});

test('EOF pauses and play restarts from the first snapshot', () => {
  const v = load(); v.floor(1); v.$('play').click();
  v.tick(); v.tick(); v.tick();
  assert.equal(v.$('position').textContent, '3 / 3'); assert.equal(v.timers.size, 0);
  v.$('play').click(); assert.equal(v.$('position').textContent, '1 / 3');
  v.tick(); assert.equal(v.$('position').textContent, '2 / 3');
});

test('changing floor or combat stops playback and v2 shows captured details', () => {
  const v = load(); v.floor(1); v.$('play').click();
  v.$('combats').children[1].click(); assert.equal(v.timers.size, 0);
  assert.match(v.$('frames').textContent, /CARD.STRIKE/);
  assert.match(v.$('frames').textContent, /格擋 5/);
  v.$('play').click(); v.floor(3); assert.equal(v.timers.size, 0);
  assert.equal(v.$('play').disabled, true);
});

test('three languages translate room and choice categories', () => {
  const v = load(); v.floor(3);
  for (const [lang, shop, purchase] of [['zht', '商店', '購買'], ['zhs', '商店', '购买'], ['eng', 'Shop', 'purchase']]) {
    v.$('language').onchange({ target: { value: lang } });
    assert.equal(v.document.documentElement.lang, lang);
    assert.ok(v.$('timeline').textContent.includes(shop));
    assert.ok(v.$('choices').textContent.toLowerCase().includes(purchase.toLowerCase()));
  }
});

test('native room enum aliases translate as well as map node names', () => {
  const data = structuredClone(sample);
  data.checkpoints.find(x => x.floor === 4).room_type = 'Merchant';
  data.checkpoints.find(x => x.floor === 1).room_type = 'Combat';
  const v = load(data);
  assert.ok(v.$('timeline').textContent.includes('商店'));
  assert.ok(v.$('timeline').textContent.includes('戰鬥'));
  assert.ok(!v.$('timeline').textContent.includes('Merchant'));
});

test('AI reason is visible only where present, and hostile strings stay text', () => {
  const data = structuredClone(sample), hostile = '</script><img src=x onerror=alert(1)>';
  data.replays.find(x => x.snapshots?.format_version === 2).snapshots.frames[0].players[0].label = hostile;
  const v = load(data); v.floor(1); v.$('combats').children[1].click();
  assert.ok(v.$('frames').textContent.includes(hostile));
  assert.ok(!v.$('frames').textContent.includes('AI 理由'));
  v.floor(5); assert.ok(v.$('choices').textContent.includes('AI 理由'));
});

test('largest observed loss counts HP falls despite later healing and links to battle', () => {
  const data = structuredClone(sample), r = data.replays.find(x => x.snapshots);
  r.snapshots.frames.forEach((f, i) => { f.players[0].hp = [80, 20, 80][i]; });
  const v = load(data);
  assert.match(v.$('loss').textContent, /60/);
  v.$('loss').click(); assert.equal(v.$('position').textContent, '1 / 3');
});

test('empty run stays readable with playback disabled', () => {
  const v = load({ format_version: 1, run_id: 'a'.repeat(64), language: 'eng', checkpoints: [], replays: [], choices: [], warnings: [] });
  assert.equal(v.$('timeline').children.length, 0);
  assert.equal(v.$('play').disabled, true);
  assert.equal(v.$('position').textContent, 'No data recorded');
});


test('AI observations preserve unknown data, text, loss and controls', () => {
  const data = { format_version: 1, language: 'eng', ai_run: { source: 'ai', seed: 'SEED', character: 'Hunter', ascension: 10, outcome: 'victory', summary: 'Deck plan\nBoss strategy <img>', limitations: ['Incomplete'] }, checkpoints: [{ source: 'ai', capture_kind: 'ai_observation', act: 1, floor: 1, hp: null, max_hp: null, gold: null, deck_count: null }], choices: [{ act: 1, floor: 1, kind: 'future_kind' }], replays: [{ id: 'fight', metadata: { source: 'ai', act: 1, floor: 1 }, snapshots: { format_version: 2, frames: [
    { turn: null, players: [{ label: 'Hero', hp: 80, max_hp: null }], enemies: null, cards: null, actions: null, action: 'attack', ai_reason: 'why', ai_reason_source: 'notes' },
    { turn: 2, players: [{ label: 'Hero', hp: null, max_hp: null }], enemies: null },
    { turn: 3, players: [{ label: 'Hero', hp: 70, max_hp: null }], enemies: null },
    { turn: 4, players: [{ label: 'Hero', hp: 65, max_hp: null }], enemies: [] }
  ] } }], warnings: [] };
  const v = load(data, template);
  assert.match(v.$('ai-run').textContent, /SEED.*Hunter.*10.*victory/);
  assert.ok(v.$('ai-run').textContent.includes('Deck plan\nBoss strategy <img>'));
  assert.match(v.$('ai-run').textContent, /Incomplete/);
  assert.match(v.$('detail').textContent, /Observation only.*cannot be rewound/);
  assert.match(v.$('restore').textContent, /cannot be rewound/);
  assert.match(v.$('detail').textContent, /HP ?\/?/);
  assert.match(v.$('choices').textContent, /future_kind/);
  assert.match(v.$('frames').textContent, /Requested action: attack.*AI reason: why.*Reason source: notes/);
  assert.match(v.$('frames').textContent, /Turn —/);
  assert.match(v.$('frames').textContent, /EnemiesNo data recorded/);
  assert.match(v.$('frames').textContent, /Hand: No data recorded/);
  assert.match(v.$('loss').textContent, /: 5 /);
  v.$('next').click(); assert.match(v.$('frames').textContent, /HP ?\/?/);
  v.$('play').click(); v.tick(); assert.equal(v.$('position').textContent, '3 / 4');
  v.$('speed').value = '2'; v.$('speed').onchange(); assert.equal([...v.timers.values()][0].delay, 500);
  v.$('play').click(); assert.equal(v.timers.size, 0);
  v.$('language').onchange({ target: { value: 'zht' } }); assert.match(v.$('restore').textContent, /僅供觀測/);
  v.$('language').onchange({ target: { value: 'zhs' } }); assert.match(v.$('restore').textContent, /仅供观察/);
  assert.match(template, /default-src 'none'/);
  assert.doesNotMatch(template, /<(?:script|link|img)[^>]+(?:src|href)=["']https?:/i);
});

test('AI loss requires adjacent known HP values', () => {
  const data = structuredClone(sample), r = data.replays.find(x => x.snapshots);
  data.replays = [r]; r.snapshots.frames.forEach((f, i) => { f.players[0].hp = [80, null, 40][i]; });
  const v = load(data, template); assert.match(v.$('loss').textContent, /沒有紀錄/);
});
