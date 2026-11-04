const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const perf = require('../../scripts/perf-history.cjs');

const root = path.resolve(__dirname, '../..');
const read = relative => fs.readFileSync(path.join(root, relative));
const json = relative => JSON.parse(read(relative).toString('utf8'));
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

test('verified history can be rebuilt from its evidence and checked-in method', () => {
  const history = json('tests/performance-baselines/verified-history.json');
  const methodPath = 'tests/performance-baselines/methods/dense-unique-width-v1.json';
  const methodBytes = read(methodPath);
  const method = JSON.parse(methodBytes.toString('utf8'));
  const fixtureHash = sha256(read('tests/performance-fixtures/high-density-unique-width-fixture.cs'));
  const hostHash = sha256(read('tests/performance-fixtures/wpf-history-host.cs'));
  const envPath = 'tests/performance-baselines/environments/win10-19045-ryzen5700x-wpf192dpi.json';
  const environment = json(envPath).environment;
  const buildPath = 'tests/performance-baselines/environments/build-evidence-2026-11-04.json';
  const builds = json(buildPath).versions;
  assert.equal(method.fixture_sha256, fixtureHash);
  assert.equal(method.program_cs_sha256, hostHash);
  assert.equal(history.runs.length, 6);

  const rebuilt = perf.emptyHistory();
  for (const run of history.runs) {
    const manifest = run.manifest;
    const build = builds.find(item => item.version === manifest.version);
    assert.ok(build, `unknown measured version ${manifest.version}`);
    assert.equal(manifest.commit, build.commit);
    assert.equal(manifest.fixture_sha256, fixtureHash);
    assert.equal(manifest.method_sha256, sha256(methodBytes));
    assert.deepEqual(manifest.environment, environment);
    assert.equal(manifest.evidence.method_file, methodPath);
    assert.equal(manifest.evidence.environment_file, envPath);
    assert.equal(manifest.evidence.build_evidence_file, buildPath);
    assert.equal(manifest.evidence.application_assembly_sha256, build.app_sha256);
    assert.equal(manifest.evidence.core_assembly_sha256, build.core_sha256);
    assert.equal(manifest.evidence.test_assembly_sha256, build.test_host_sha256);
    assert.equal(perf.importRun(rebuilt, manifest, run.sample_text, run.summary_text).id, run.id);
  }
  assert.deepEqual(rebuilt.runs, history.runs);
  const byExecution = name => history.runs.find(run => run.manifest.execution_id === `20261104-dense-${name}`).id;
  const old = perf.confirm(rebuilt, [byExecution('old-1'), byExecution('old-2')]);
  const current = perf.confirm(rebuilt, [byExecution('new-1'), byExecution('new-2')]);
  assert.deepEqual(old.uncertain, ['detail_toggle_ms']);
  assert.deepEqual(current.uncertain.sort(), ['resize_ms', 'scroll_layout_ms']);
  assert.deepEqual(rebuilt.confirmations, history.confirmations);
});
