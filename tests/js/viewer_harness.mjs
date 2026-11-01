// Shared DOM model and loader for the viewer tests. See test_replay_viewer.mjs.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

export const html = readFileSync(new URL('../../docs/sample-replay.html', import.meta.url), 'utf8');
export const sample = JSON.parse(html.match(/<script id="replay-data" type="application\/json">([\s\S]*?)<\/script>/)[1]);
export const template = readFileSync(new URL('../../tools/replay-viewer.html', import.meta.url), 'utf8');

export class Element {
  constructor(tag) { this.tagName = tag; this.children = []; this.attributes = {}; this.style = {}; this.value = ''; this.disabled = false; this.ownText = ''; }
  set textContent(text) { this.ownText = String(text); this.children = []; }
  get textContent() { return this.ownText + this.children.map(x => x.textContent).join(''); }
  set innerHTML(_) { throw new Error('Viewer must render data as text'); }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  append(...children) { this.children.push(...children); }
  prepend(...children) { this.children.unshift(...children); }
  replaceChildren(...children) { this.ownText = ''; this.children = children; }
  click() { if (!this.disabled) this.onclick?.({ target: this }); }
}

export function load(data = structuredClone(sample), viewerHtml = html) {
  const script = viewerHtml.match(/<\/script><script>([\s\S]*?)<\/script>/)[1];
  const nodes = new Map([...viewerHtml.matchAll(/\bid="([^"]+)"/g)].map(m => [m[1], new Element('div')]));
  nodes.get('replay-data').textContent = JSON.stringify(data);
  nodes.get('speed').value = '1';
  const timers = new Map(); let sequence = 0;
  const document = { documentElement: {}, getElementById: id => nodes.get(id), createElement: tag => new Element(tag), createElementNS: (_, tag) => new Element(tag) };
  vm.runInNewContext(script, { document, setInterval: (callback, delay) => { const id = ++sequence; timers.set(id, { callback, delay }); return id; }, clearInterval: id => timers.delete(id) }, { timeout: 1000 });
  return { $: id => nodes.get(id), document, timers, tick: () => [...timers.values()].forEach(t => t.callback()), floor: i => nodes.get('timeline').children[i].click() };
}
