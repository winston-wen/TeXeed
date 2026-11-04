'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { emptyHistory, importRun, confirm, compare, archiveReport } = require('./perf-history.cjs');

const commitA = 'a'.repeat(40), commitB = 'b'.repeat(40), commitC = 'c'.repeat(40);
function manifest(id, commit = commitA, extra = {}) {
  return { scenario: 'ai-reading/ordinary/scroll-down/v1', fixture_sha256: '1'.repeat(64), method_sha256: '2'.repeat(64),
    method_description: 'seven samples after warmup, through ApplicationIdle', version: `version-${commit[0]}`,
    commit, execution_id: id, captured_at: '2026-11-04T12:00:00+08:00', warmup_count: 1,
    configuration: 'Release', run_kind: 'production', feature_overrides: [],
    components: { TeXeed: commit, 'TeXeed.Core': commit },
    environment: { id: 'desktop-1', os: 'Windows build A', arch: 'x64', cpu: 'example CPU', runtime: '.NET 10', display: '100% DPI', fonts: 'font-set-1', renderer: 'WPF' },
    ...extra };
}
function evidence(time, allocation, jitter = .1, second = 30) {
  const values = n => [n - jitter, n + jitter, n, n + jitter / 2, n - jitter / 2, n + jitter / 3, n - jitter / 3];
  const sample = { elapsed: { scroll_layout_ms: values(time), resize_ms: values(second) }, allocated: { scroll_allocated_bytes: values(allocation) } };
  const summary = { scroll_layout_ms: time, resize_ms: second, scroll_allocated_bytes: allocation, visible_controls: 18 };
  return { samples: JSON.stringify(sample), summary: JSON.stringify(summary) };
}
function add(history, m, time, allocation, jitter = .1, second = 30) {
  const e = evidence(time, allocation, jitter, second);
  return importRun(history, m, e.samples, e.summary);
}

test('one authentic batch is provisional; confirmation requires independent, consistent evidence', () => {
  const h = emptyHistory();
  const old = add(h, manifest('old', commitA, { feature_notes: 'Predates later wave emphasis' }), 10, 1000000, .1, 40);
  const current = add(h, manifest('current', commitB), 24, 1400000, .2, 25);
  const provisional = compare(h, current.id, old.id).metrics;
  assert.equal(provisional.scroll_layout_ms.historical_best.value, 10);
  assert.equal(provisional.scroll_layout_ms.historical_best.status, 'provisional');
  assert.equal(provisional.scroll_layout_ms.historical_best.commit, commitA);
  assert.equal(provisional.scroll_layout_ms.historical_best.alarm, true);
  assert.equal(provisional.resize_ms.historical_best.commit, commitA);
  assert.equal(provisional.resize_ms.candidate_historical_best.commit, commitB);
  assert.throws(() => confirm(h, [old.id, old.id]), /independent/);
  const repeat = add(h, manifest('old-independent', commitA), 10.2, 1005000, .3, 40.2);
  const result = confirm(h, [old.id, repeat.id]);
  assert.equal(result.updated.length, 3);
  assert.equal(compare(h, current.id, old.id).metrics.scroll_layout_ms.historical_best.status, 'confirmed');
  assert.equal(compare(h, current.id, old.id).metrics.scroll_layout_ms.historical_best.value, 10.2);
  assert.equal(h.confirmations.find(b => b.metric === 'scroll_layout_ms').evidence.length, 2);
});

test('reject missing or invalid evidence, diagnostics, and mixed application components', () => {
  const h = emptyHistory(), e = evidence(10, 1000000);
  assert.throws(() => importRun(h, manifest('no-samples'), '{}', e.summary), /no supported/);
  assert.throws(() => importRun(h, manifest('invalid'), JSON.stringify({ elapsed: { scroll_layout_ms: [1, 2, 3, 4, 5, 6, 0] } }), e.summary), /seven positive/);
  assert.throws(() => importRun(h, manifest('mismatch'), e.samples, JSON.stringify({ scroll_layout_ms: 999 })), /median mismatch/);
  assert.throws(() => importRun(h, manifest('diagnostic', commitA, { run_kind: 'diagnostic' }), e.samples, e.summary), /production Release/);
  assert.throws(() => importRun(h, manifest('disabled', commitA, { feature_overrides: ['disable-waves'] }), e.samples, e.summary), /overrides/);
  assert.throws(() => importRun(h, manifest('mixed', commitA, { components: { App: commitA, Core: commitB } }), e.samples, e.summary), /does not match/);
  const first = importRun(h, manifest('first'), e.samples, e.summary);
  assert.throws(() => importRun(h, manifest('duplicate-samples'), e.samples, e.summary), /identical sample/);
  assert.throws(() => importRun(h, manifest('first'), evidence(11, 1000010).samples, evidence(11, 1000010).summary), /execution_id/);
  assert.ok(first.sample_text.includes('scroll_layout_ms'));
});

test('fixture, method and environment mismatches cannot compare or confirm', () => {
  for (const update of [
    { scenario: 'other' }, { fixture_sha256: '3'.repeat(64) }, { method_sha256: '4'.repeat(64) },
    { environment: { ...manifest('x').environment, display: '150% DPI' } }
  ]) {
    const h = emptyHistory(), a = add(h, manifest('a'), 10, 1000000), b = add(h, manifest('b', commitA, update), 10.1, 1000001, .2);
    assert.throws(() => compare(h, b.id, a.id), /scenario, fixture, method or environment/);
    assert.throws(() => confirm(h, [a.id, b.id]), /scenario, fixture, method or environment/);
  }
});

test('unstable confirmations do not become formal baselines; independent upward shifts are reported', () => {
  const h = emptyHistory();
  const a = add(h, manifest('a', commitA), 10, 1000000, .1, 30);
  const a2 = add(h, manifest('a2', commitA), 10.2, 1000100, .2, 30.1);
  const b = add(h, manifest('b', commitB), 24, 1500000, .3, 50);
  const b2 = add(h, manifest('b2', commitB), 24.2, 1500100, .4, 50.1);
  const unstable = add(h, manifest('a3', commitA), 20, 1000200, .5, 30.2);
  assert.ok(confirm(h, [a.id, unstable.id]).uncertain.includes('scroll_layout_ms'));
  confirm(h, [a.id, a2.id]);
  const result = compare(h, b.id, a.id, b2.id, a2.id).metrics;
  assert.equal(result.scroll_layout_ms.before_version_stability, 'unstable');
  assert.equal(result.scroll_layout_ms.stable_increase, null);
  assert.equal(result.scroll_layout_ms.before.alarm, true);
  assert.equal(result.scroll_allocated_bytes.before.alarm, true);
  assert.throws(() => compare(h, b.id, a.id, b2.id), /both sides/);
  const faster = add(h, manifest('c', commitC), 9, 990000, .6, 51);
  const best = compare(h, b.id, a.id).metrics.scroll_layout_ms.historical_best;
  assert.equal(best.status, 'provisional'); assert.equal(best.commit, commitC); assert.equal(best.value, 9);
  assert.ok(faster.id);
});

test('small release-by-release slowdowns still alarm against the earliest historical best', () => {
  const h = emptyHistory();
  const versions = [10, 12.9, 16.5, 21.1].map((value, i) =>
    add(h, manifest(`release-${i}`, String.fromCharCode(97 + i).repeat(40)), value, 1000000 + i * 20000, .1 + i / 10, 30 + i));
  for (let i = 1; i < versions.length; i++)
    assert.ok(versions[i].metrics.scroll_layout_ms.median / versions[i - 1].metrics.scroll_layout_ms.median < 1.3);
  const comparison = compare(h, versions[3].id, versions[2].id).metrics.scroll_layout_ms;
  assert.equal(comparison.before.alarm, false);
  assert.equal(comparison.historical_best.value, 10);
  assert.equal(comparison.historical_best.commit, versions[0].manifest.commit);
  assert.equal(comparison.historical_best.status, 'provisional');
  assert.equal(comparison.historical_best.alarm, true);
});

test('selected-version trend requires independent confirmed pairs at every step', () => {
  const h = emptyHistory(), commits = [commitA, commitB, commitC];
  const batches = commits.map((commit, i) => [
    add(h, manifest(`trend-${i}-a`, commit), 10 + i * 5, 1000000 + i * 100000, .1 + i / 10, 30 + i * 3),
    add(h, manifest(`trend-${i}-b`, commit), 10.2 + i * 5, 1000100 + i * 100000, .2 + i / 10, 30.1 + i * 3)
  ]);
  const ids = batches.map(pair => pair[0].id);
  let trend = compare(h, ids[2], ids[1], undefined, undefined, ids).selected_version_trend.scroll_layout_ms;
  assert.equal(trend.status, 'insufficient_evidence'); assert.equal(trend.stable_increase, null);
  for (const pair of batches) confirm(h, pair.map(run => run.id));
  trend = compare(h, ids[2], ids[1], undefined, undefined, ids).selected_version_trend.scroll_layout_ms;
  assert.equal(trend.status, 'confirmed_pairs'); assert.equal(trend.stable_increase, true);
  assert.equal(trend.points.length, 3);
  assert.throws(() => compare(h, ids[2], ids[1], undefined, undefined, [ids[2], ids[0], ids[1]]), /ending with before,current/);
});

test('all imported batches count: a divergent third batch prevents and revokes stable classification', () => {
  const h = emptyHistory();
  const first = add(h, manifest('third-check-first', commitA), 10, 1000000, .1, 30);
  const second = add(h, manifest('third-check-second', commitA), 10.1, 1000100, .2, 30.1);
  confirm(h, [first.id, second.id]);
  const newVersion = add(h, manifest('third-check-new', commitB), 25, 1300000, .3, 50);
  let metric = compare(h, newVersion.id, first.id).metrics.scroll_layout_ms;
  assert.equal(metric.historical_best.status, 'confirmed');
  const outlier = add(h, manifest('third-check-outlier', commitA, { captured_at: '2026-11-05T12:00:00+08:00' }), 17, 1000200, .4, 30.2);
  const retry = confirm(h, [first.id, second.id]);
  assert.ok(retry.uncertain.includes('scroll_layout_ms'));
  metric = compare(h, newVersion.id, first.id).metrics.scroll_layout_ms;
  assert.equal(metric.historical_best.status, 'provisional');
  assert.equal(metric.historical_best.version_stability, 'unstable');
  assert.equal(metric.before_version_stability, 'unstable');
  assert.equal(metric.historical_best.value, 10);
  assert.equal(outlier.manifest.commit, first.manifest.commit);
  const sameVersionComparison = compare(h, outlier.id, first.id).metrics.scroll_layout_ms;
  assert.equal(sameVersionComparison.historical_best.status, 'provisional');
  assert.equal(sameVersionComparison.historical_best.version_stability, 'unstable');
});

test('confirmation includes all stable same-version batches rather than selecting a favorable pair', () => {
  const h = emptyHistory();
  const a = add(h, manifest('all-a', commitA), 10, 1000000, .1, 30);
  const b = add(h, manifest('all-b', commitA), 10.1, 1000100, .2, 30.1);
  const c = add(h, manifest('all-c', commitA), 10.4, 1000200, .3, 30.2);
  const accepted = confirm(h, [a.id, b.id]).updated.find(entry => entry.metric === 'scroll_layout_ms');
  assert.equal(accepted.value, 10.4); assert.equal(accepted.evidence.length, 3);
  assert.ok(accepted.evidence.includes(c.id));
});

test('independent metrics retain different historical winners and later single-batch improvements remain provisional', () => {
  const h = emptyHistory();
  const first = add(h, manifest('first', commitA), 10, 1000000, .1, 100);
  const firstRepeat = add(h, manifest('first-repeat', commitA), 10.1, 1000100, .2, 100.2);
  confirm(h, [first.id, firstRepeat.id]);
  const second = add(h, manifest('second', commitB), 15, 1100000, .3, 20);
  let result = compare(h, second.id, first.id).metrics;
  assert.equal(result.scroll_layout_ms.historical_best.commit, commitA);
  assert.equal(result.resize_ms.candidate_historical_best.commit, commitB);
  const later = add(h, manifest('later', commitC), 20, 1200000, .35, 50);
  result = compare(h, later.id, second.id).metrics;
  assert.equal(result.scroll_layout_ms.historical_best.commit, commitA);
  assert.equal(result.resize_ms.historical_best.commit, commitB);
  const fasterSingle = add(h, manifest('new-single', commitA), 8, 1000200, .4, 100.3);
  result = compare(h, second.id, first.id).metrics;
  assert.equal(result.scroll_layout_ms.historical_best.status, 'provisional');
  assert.equal(result.scroll_layout_ms.historical_best.value, 8);
  assert.equal(result.scroll_layout_ms.historical_best.evidence[0], fasterSingle.id);
  assert.equal(result.resize_ms.candidate_historical_best.commit, commitB);
});

test('CLI keeps complete evidence after source files are removed and detects modified history', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'texeed-perf-history-'));
  const cli = path.join(__dirname, 'perf-history.cjs'), history = path.join(temp, 'history.json');
  const run = argv => spawnSync(process.execPath, [cli, ...argv], { encoding: 'utf8' });
  function importCli(id, commit, time) {
    const m = path.join(temp, `${id}-manifest.json`), s = path.join(temp, `${id}-sample.json`), q = path.join(temp, `${id}-summary.json`);
    const e = evidence(time, 1000000 + time, .1 + time / 1000);
    fs.writeFileSync(m, JSON.stringify(manifest(id, commit))); fs.writeFileSync(s, e.samples); fs.writeFileSync(q, e.summary);
    const result = run(['import', '--history', history, '--manifest', m, '--samples', s, '--summary', q]);
    assert.equal(result.status, 0, result.stderr);
    fs.unlinkSync(m); fs.unlinkSync(s); fs.unlinkSync(q);
    return JSON.parse(result.stdout).imported;
  }
  try {
    const before = importCli('before', commitA, 10), current = importCli('current', commitB, 12);
    const result = run(['compare', '--history', history, '--current', current, '--before', before]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(result.stdout).metrics.scroll_layout_ms.historical_best.status, 'provisional');
    const slow = importCli('slow', commitC, 30);
    const alarm = run(['compare', '--history', history, '--current', slow, '--before', current]);
    assert.equal(alarm.status, 2); assert.equal(JSON.parse(alarm.stdout).historical_alarm, true);
    assert.equal(JSON.parse(alarm.stdout).before_alarm, true);
    const stored = JSON.parse(fs.readFileSync(history));
    assert.equal(stored.runs[0].sample.elapsed.scroll_layout_ms.length, 7);
    stored.runs[0].sample.elapsed.scroll_layout_ms[0] = 999;
    fs.writeFileSync(history, JSON.stringify(stored));
    assert.match(run(['compare', '--history', history, '--current', current, '--before', before]).stderr, /evidence does not match/);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

test('archived historical candidates are reported with raw evidence and never treated as confirmed', () => {
  const archive = path.join(__dirname, '..', 'tests', 'performance-baselines', 'history', 'history.json');
  const result = archiveReport(archive, 'ordinary', path.join(path.dirname(archive), '0.3.49-ordinary', 'ui-performance.json'));
  assert.equal(result.status, 'unverified_historical_candidates');
  assert.equal(result.comparison_status, 'method_and_environment_unverified');
  assert.equal(result.records_considered, 4);
  assert.equal(result.metrics.scroll_layout_ms.value, 20.1807);
  assert.equal(result.metrics.scroll_layout_ms.version, '0.3.47');
  assert.equal(result.metrics.detail_toggle_ms.version, '0.3.48');
  assert.equal(result.metrics.scroll_layout_ms.status, 'unverified_historical_candidate');
  assert.equal(result.current.scroll_layout_ms.current, 22.7015);
  assert.equal(result.current.scroll_layout_ms.status, 'method_and_environment_unverified');
  assert.throws(() => archiveReport(archive, 'missing'), /unknown archived scenario/);
});

test('archived evidence hash mismatches are rejected without changing the source archive', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'texeed-perf-archive-'));
  const source = path.join(__dirname, '..', 'tests'), base = path.join(temp, 'tests');
  try {
    fs.mkdirSync(base, { recursive: true });
    fs.cpSync(path.join(source, 'performance-baselines'), path.join(base, 'performance-baselines'), { recursive: true });
    fs.cpSync(path.join(source, 'performance-fixtures'), path.join(base, 'performance-fixtures'), { recursive: true });
    const archive = path.join(base, 'performance-baselines', 'history', 'history.json');
    fs.appendFileSync(path.join(path.dirname(archive), '0.3.47-ordinary', 'ui-performance.json'), ' ');
    assert.throws(() => archiveReport(archive, 'ordinary'), /evidence hash mismatch/);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});
