'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = __dirname;
const read = (base, name) => JSON.parse(fs.readFileSync(path.join(base, name), 'utf8'));
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];

test('heading hierarchy retains source, fixtures, and all performance samples', () => {
  for (const base of [root, path.join(root, 'diagnostics/pre-reuse')]) {
  const manifest = read(base, 'manifest.json');
  assert.equal(manifest.runs.length, 12);
  for (const [name, expected] of Object.entries(manifest.sources))
    assert.equal(hash(path.join(base, 'source-snapshot', name)), expected, name);
  for (const [name, expected] of Object.entries(manifest.fixtures))
    assert.equal(hash(path.resolve(root, '../../performance-fixtures', name)), expected, name);
  for (const run of manifest.runs) {
    for (const [name, expected] of Object.entries(run.files))
      assert.equal(hash(path.join(base, 'runs', run.id, name)), expected, `${run.id}/${name}`);
    const samples = read(base, path.join('runs', run.id, 'ui-performance-samples.json'));
    const summary = read(base, path.join('runs', run.id, 'ui-performance.json'));
    for (const group of Object.values(samples)) for (const [name, values] of Object.entries(group)) {
      assert.equal(values.length, 7);
      assert.equal(median(values), summary[name], `${run.id}/${name}`);
    }
    if (run.id.startsWith('multi-')) {
      assert.ok(summary.initial_layout_ms > 0, 'initial layout timing');
      assert.ok(summary.initial_allocated_bytes > 0, 'initial layout allocation');
    }
  }
  }
});
