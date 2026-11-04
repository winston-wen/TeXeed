'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

test('browser performance candidates retain their original evidence and fixture', () => {
  const root = __dirname;
  const archive = JSON.parse(fs.readFileSync(path.join(root, 'candidates.json'), 'utf8'));
  const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  assert.equal(archive.status, 'unverified_historical_candidates');
  assert.equal(hash(path.resolve(root, archive.common_scenario.reconstructed_fixture)), archive.common_scenario.fixture_sha256);
  assert.ok(archive.records.length >= 2);
  for (const record of archive.records) {
    const file = path.resolve(root, record.file);
    assert.ok(file.startsWith(path.join(root, 'candidates') + path.sep));
    assert.equal(hash(file), record.sha256, `candidate evidence changed: ${record.file}`);
    assert.doesNotThrow(() => JSON.parse(fs.readFileSync(file, 'utf8')));
  }
});
