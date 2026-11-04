'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = __dirname;
const load = name => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));
const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];

test('bookmark performance archive retains source, fixtures and every measured sample', () => {
  const manifest = load('manifest.json');
  assert.equal(manifest.status, 'uncommitted_working_tree_candidate_not_confirmed_history');
  assert.equal(manifest.records.length, 7);
  assert.equal(sha(path.join(root, 'source-manifest.json')), manifest.source_manifest_sha256);
  assert.equal(sha(path.resolve(root, manifest.fixture_reference)), manifest.fixture_sha256);
  assert.equal(sha(path.join(root, 'AiUiPerformanceTests.cs.txt')), manifest.fixture_sha256);
  assert.equal(sha(path.join(root, 'PerformanceHost.Program.cs.txt')), manifest.host_sha256);
  for (const [name, hash] of Object.entries(manifest.host_support_files)) assert.equal(sha(path.join(root, name)), hash);
  assert.ok(fs.existsSync(path.resolve(root, manifest.dense_method_file)));
  const sources = load('source-manifest.json');
  assert.equal(sources.source_base_commit, manifest.source_base_commit);
  for (const name of sources.modified_or_new) {
    const source = sources.all_source_files.find(file => file.path === name);
    assert.ok(source, `source metadata missing: ${name}`);
    assert.equal(sha(path.join(root, 'source-snapshot', name + '.txt')), source.sha256, name);
  }
  for (const record of manifest.records) {
    for (const [name, expected] of Object.entries(record.files))
      assert.equal(sha(path.join(root, 'runs', record.id, name)), expected, `${record.id}/${name}`);
    const samples = load(path.join('runs', record.id, 'ui-performance-samples.json'));
    const summary = load(path.join('runs', record.id, 'ui-performance.json'));
    for (const group of Object.values(samples)) for (const [name, values] of Object.entries(group)) {
      assert.equal(values.length, 7, `${record.id}/${name}`);
      assert.ok(values.every(value => Number.isFinite(value) && value > 0));
      assert.equal(median(values), summary[name], `${record.id}/${name}`);
    }
  }
  assert.equal(sha(path.join(root, 'bookmark-ui-performance.json')), manifest.menu.sha256);
  assert.equal(sha(path.join(root, manifest.menu.test_source)), manifest.menu.test_source_sha256);
  const menu = load('bookmark-ui-performance.json');
  assert.equal(menu.turns, 120); assert.equal(menu.indexed, 360);
  for (const values of Object.values(menu.searchTiming)) assert.equal(values.length, 7);
  assert.equal(sha(path.join(root, manifest.index_cost.test_source)), manifest.index_cost.test_source_sha256);
  assert.equal(manifest.index_cost.records.length, 4);
  for (const item of manifest.index_cost.records) {
    assert.equal(sha(path.join(root, item.file)), item.sha256);
    const index = load(item.file);
    assert.equal(index.turns, 120); assert.equal(index.bookmarks, 600);
    assert.ok(index.firstMs > 0 && index.firstBytes > 0 && index.reusedMs > 0 && index.reusedBytes > 0);
  }
  assert.equal(manifest.historical_best_comparison.resize_allocated_bytes.candidate -
    manifest.historical_best_comparison.resize_allocated_bytes['confirmed_0.3.47'],
    manifest.historical_best_comparison.resize_allocated_bytes.difference);
});
