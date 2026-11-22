// Builds public/samples/: every standalone algorithm of Algorithm.CSharp / Algorithm.Python plus index.json, so the
// submit page can offer them without any API. C# samples run as `builtin` classes of QuantConnect.Algorithm.CSharp.dll
// in the worker image; Python samples are uploaded as main.py (samples importing sibling files are skipped).
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const repo = process.env.LEAN_REPO_ROOT ?? join(root, '..', '..');
const out = join(root, 'public', 'samples');
rmSync(out, { recursive: true, force: true });

const summary = (text) => text.replace(/\s+/g, ' ').trim().slice(0, 240);

function csharp() {
  const dir = join(repo, 'Algorithm.CSharp');
  const items = [];
  for (const file of readdirSync(dir).filter((f) => f.endsWith('.cs'))) {
    const source = readFileSync(join(dir, file), 'utf8');
    const match = source.match(/public\s+(?:sealed\s+)?class\s+(\w+)\s*:\s*QCAlgorithm\b/);
    const ns = source.match(/namespace\s+([\w.]+)/);
    if (!match || !ns) continue;
    const doc = source.slice(0, match.index).match(/<summary>([\s\S]*?)<\/summary>(?![\s\S]*<summary>)/);
    items.push({
      language: 'CSharp',
      name: match[1],
      typeName: `${ns[1]}.${match[1]}`,
      file: `csharp/${file}`,
      description: doc ? summary(doc[1].replace(/\/\/\/?/g, '').replace(/<[^>]+>/g, '')) : '',
      regression: /IRegressionAlgorithmDefinition/.test(source),
      source,
    });
  }
  return items;
}

function python() {
  const dir = join(repo, 'Algorithm.Python');
  const files = readdirSync(dir).filter((f) => f.endsWith('.py'));
  const modules = new Set(files.map((f) => f.slice(0, -3)));
  const items = [];
  for (const file of files) {
    const source = readFileSync(join(dir, file), 'utf8');
    const match = source.match(/^class\s+(\w+)\s*\(\s*QCAlgorithm\s*\)/m);
    if (!match) continue;
    const imports = [...source.matchAll(/^\s*(?:from\s+(\w+)\s+import|import\s+(\w+))/gm)].map((m) => m[1] ?? m[2]);
    if (imports.some((m) => modules.has(m))) continue;
    const doc = source.slice(match.index).match(/^\s*class[^\n]*\n\s*(?:'''|""")([\s\S]*?)(?:'''|""")/);
    const comment = source.slice(0, match.index).match(/((?:^#[^\n]*\n)+)\s*$/m);
    items.push({
      language: 'Python',
      name: file.slice(0, -3),
      typeName: match[1],
      file: `python/${file}`,
      description: summary(doc?.[1] ?? (/QUANTCONNECT\.COM/.test(comment?.[1] ?? '') ? '' : comment?.[1]?.replace(/^#\s?/gm, '') ?? '')),
      regression: false,
      source,
    });
  }
  return items;
}

const index = [];
for (const item of [...csharp(), ...python()].sort((a, b) => a.name.localeCompare(b.name) || a.language.localeCompare(b.language))) {
  mkdirSync(join(out, dirname(item.file)), { recursive: true });
  writeFileSync(join(out, item.file), item.source);
  const { source: _source, ...entry } = item;
  index.push(entry);
}
writeFileSync(join(out, 'index.json'), JSON.stringify(index));
console.log(`samples: ${index.filter((s) => s.language === 'CSharp').length} C#, ${index.filter((s) => s.language === 'Python').length} Python -> ${out}`);
