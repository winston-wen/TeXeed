'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = __dirname;
const read = name => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
test('integrated marker and wave performance archive retains source and all samples', () => {
  const manifest = read('manifest.json');
  assert.equal(manifest.runs.length, 6);
  for (const [name, value] of Object.entries(manifest.sources))
    assert.equal(hash(path.join(root, 'source-snapshot', name)), value);
  for (const item of manifest.runs) {
    for (const [name, value] of Object.entries(item.files))
      assert.equal(hash(path.join(root, 'runs', item.id, name)), value);
    const samples = read(path.join('runs', item.id, 'ui-performance-samples.json'));
    const summary = read(path.join('runs', item.id, 'ui-performance.json'));
    for (const group of Object.values(samples)) for (const [name, values] of Object.entries(group)) {
      assert.equal(values.length, 7);
      assert.equal(median(values), summary[name], `${item.id}/${name}`);
    }
  }
  assert.equal(hash(path.resolve(root, '../../performance-fixtures/high-density-unique-width-fixture.cs')), manifest.fixture_sha256);
  assert.equal(hash(path.resolve(root, '../../performance-fixtures/wpf-history-host.cs')), manifest.host_sha256);
});
