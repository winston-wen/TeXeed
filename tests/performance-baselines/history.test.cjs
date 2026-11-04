const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const archive = path.join(__dirname, 'history');
const fixtureDirectory = path.resolve(__dirname, '../performance-fixtures');
const history = JSON.parse(fs.readFileSync(path.join(archive, 'history.json'), 'utf8'));
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const summaryFile = 'ui-performance.json';
const samplesFile = 'ui-performance-samples.json';
const expectedRecords = [
  '0.3.47-ordinary', '0.3.47-dense',
  '0.3.48-ordinary-batch1', '0.3.48-dense-batch1',
  '0.3.48-ordinary-batch2', '0.3.48-dense-batch2',
  '0.3.49-ordinary', '0.3.49-dense',
];
const sections = {
  elapsed: ['scroll_layout_ms', 'detail_toggle_ms', 'resize_ms'],
  allocated: ['scroll_allocated_bytes', 'detail_allocated_bytes', 'resize_allocated_bytes'],
};

function verifyMetrics(summary, samples, id) {
  assert.deepEqual(Object.keys(samples).sort(), Object.keys(sections).sort(), `${id}: sample sections`);
  for (const [section, names] of Object.entries(sections)) {
    assert.deepEqual(Object.keys(samples[section]).sort(), [...names].sort(), `${id}: ${section} metrics`);
    for (const name of names) {
      const values = samples[section][name];
      assert.ok(Array.isArray(values) && values.length === 7, `${id}: ${name} must have seven samples`);
      assert.ok(values.every(v => typeof v === 'number' && Number.isFinite(v) && v > 0), `${id}: invalid ${name} samples`);
      assert.ok(typeof summary[name] === 'number' && Number.isFinite(summary[name]), `${id}: missing ${name} summary`);
      const median = [...values].sort((a, b) => a - b)[3];
      assert.ok(Math.abs(median - summary[name]) < 1e-6, `${id}: ${name} median differs from summary`);
    }
  }
}

test('historical candidate fixtures and raw records remain intact', () => {
  assert.equal(history.schema, 'texeed-archived-performance-candidates-v1');
  assert.equal(history.status, 'unverified_historical_candidates');
  assert.equal(history.common_method.recorded_samples, 7);
  assert.deepEqual([...history.record_files].sort(), [samplesFile, summaryFile].sort());

  for (const [name, scenario] of Object.entries(history.scenarios)) {
    const fixture = path.resolve(archive, scenario.fixture);
    assert.equal(path.dirname(fixture), fixtureDirectory, `${name}: fixture must be in the fixture directory`);
    assert.match(scenario.fixture_sha256, /^[a-f0-9]{64}$/);
    assert.equal(sha256(fs.readFileSync(fixture)), scenario.fixture_sha256, `${name}: fixture changed`);
  }

  const ids = history.records.map(record => record.directory);
  assert.equal(new Set(ids).size, ids.length, 'duplicate record directory');
  for (const expected of expectedRecords) assert.ok(ids.includes(expected), `missing ${expected}`);
  assert.deepEqual(Object.keys(history.record_sha256).sort(), [...ids].sort(), 'record hash inventory differs');

  for (const record of history.records) {
    const id = record.directory;
    assert.match(id, /^[a-zA-Z0-9.-]+$/, 'invalid record directory');
    assert.ok(history.versions[record.version] && history.scenarios[record.scenario], `${id}: unknown version or scenario`);
    assert.deepEqual(Object.keys(history.record_sha256[id]).sort(), [samplesFile, summaryFile].sort(), `${id}: missing file hash`);
    const files = {};
    for (const name of [summaryFile, samplesFile]) {
      const bytes = fs.readFileSync(path.join(archive, id, name));
      assert.match(history.record_sha256[id][name], /^[a-f0-9]{64}$/);
      assert.equal(sha256(bytes), history.record_sha256[id][name], `${id}: ${name} changed`);
      files[name] = JSON.parse(bytes.toString('utf8'));
    }
    verifyMetrics(files[summaryFile], files[samplesFile], id);
  }
});

test('sample validation rejects a missing sample or a changed median', () => {
  const id = expectedRecords[0];
  const summary = JSON.parse(fs.readFileSync(path.join(archive, id, summaryFile), 'utf8'));
  const samples = JSON.parse(fs.readFileSync(path.join(archive, id, samplesFile), 'utf8'));
  const shortened = structuredClone(samples);
  shortened.elapsed.scroll_layout_ms.pop();
  assert.throws(() => verifyMetrics(summary, shortened, id), /seven samples/);
  const altered = structuredClone(summary);
  altered.scroll_layout_ms += 1;
  assert.throws(() => verifyMetrics(altered, samples, id), /median differs/);
});
