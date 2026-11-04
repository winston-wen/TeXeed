#!/usr/bin/env node
'use strict';

// Import measured samples into a portable, reviewable performance history.
const fs = require('node:fs');
const crypto = require('node:crypto');
const path = require('node:path');

const HELP = `Performance history (all paths are local):
  node scripts/perf-history.cjs import --history FILE --manifest FILE --samples FILE --summary FILE
  node scripts/perf-history.cjs confirm --history FILE --runs ID,ID
  node scripts/perf-history.cjs compare --history FILE --current ID --before ID [--current-confirm ID --before-confirm ID] [--trend ID,ID,ID,...]
  node scripts/perf-history.cjs archive-report --archive FILE --scenario ID [--current-summary FILE]

Manifest JSON requires: scenario, fixture_sha256, method_sha256, method_description,
version, commit (full hex), execution_id, captured_at (ISO 8601), warmup_count,
configuration="Release", run_kind="production", feature_overrides=[], and
components mapping every measured application component to the same commit.
environment must contain nonempty id, os, arch, cpu, runtime, display, fonts and
renderer. Describe hardware, DPI, fonts, renderer and runtime accurately; change
the environment ID/fields whenever these conditions change. Record known version
feature differences in feature_notes, without disabling functionality at runtime.

Samples JSON has elapsed and/or allocated maps of metric names to exactly seven
positive samples. Summary JSON must contain the matching seven-sample medians.
The original JSON, all samples and manifest are embedded in history; later use
does not depend on the input paths. Never import synthetic diagnostic runs as
production. Scenario, fixture hash, method hash and complete environment must
match before comparing. Historical records lacking this evidence must be rerun.

Confirmation requires two independent executions of the same commit/cohort, with
distinct sample evidence. Each metric is eligible only if all imported batches
of that version/cohort agree within 10%; the slowest median is the reference.
Later inconsistent batches dynamically mark the version unstable and provisional.
The lowest eligible single batch remains a provisional historical best until
confirmed; comparison reports its status and evidence rather than hiding it.
Compare reports the original 30% AND 8 ms / 256 KiB alarms, signed differences
against both references, and stable increases when two batches on each side are
provided. The local tool cannot establish the truth of supplied provenance; the
manifest and the executable used must be reviewed before importing. Compare
prints all results and exits 2 if any historical reference alarms; a before-only
alarm is reported separately. Archive reports are read-only and explicitly mark
missing historical provenance and unverified method/environment compatibility;
their threshold comparisons are advisory, not confirmed benchmark results. An
explicit trend contains at least three chronologically ordered versions ending
with before,current. Its stable trend requires a confirmed pair for every point;
it describes the selected versions, not omitted releases.`;

const hex = (n) => new RegExp(`^[a-f0-9]{${n}}$`);
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const sha256 = value => crypto.createHash('sha256').update(value).digest('hex');
const stable = value => value === null || typeof value !== 'object' ? JSON.stringify(value) :
  Array.isArray(value) ? `[${value.map(stable).join(',')}]` :
    `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${stable(value[key])}`).join(',')}}`;
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const string = (value, name) => { assert(typeof value === 'string' && value.trim(), `${name} must be nonempty`); };

function cohortOf(manifest) {
  return { scenario: manifest.scenario, fixture_sha256: manifest.fixture_sha256,
    method_sha256: manifest.method_sha256, method_description: manifest.method_description,
    environment: manifest.environment };
}
function validateManifest(m) {
  assert(m && typeof m === 'object' && !Array.isArray(m), 'invalid manifest');
  for (const name of ['scenario', 'method_description', 'version', 'execution_id', 'captured_at']) string(m[name], name);
  assert(Number.isFinite(Date.parse(m.captured_at)) && /(?:Z|[+-]\d\d:\d\d)$/.test(m.captured_at), 'captured_at must be an ISO timestamp with timezone');
  for (const name of ['fixture_sha256', 'method_sha256']) assert(hex(64).test(m[name] || ''), `${name} must be a lowercase SHA-256`);
  assert(/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(m.commit || ''), 'commit must be a full lowercase Git object ID');
  assert(Number.isInteger(m.warmup_count) && m.warmup_count >= 1, 'warmup_count must be a positive integer');
  assert(m.configuration === 'Release' && m.run_kind === 'production', 'only production Release measurements are eligible');
  assert(Array.isArray(m.feature_overrides) && m.feature_overrides.length === 0, 'feature overrides or disabled-feature diagnostics are not eligible');
  assert(m.components && typeof m.components === 'object' && !Array.isArray(m.components) && Object.keys(m.components).length > 0,
    'components must enumerate the measured application assemblies');
  for (const [name, commit] of Object.entries(m.components)) {
    string(name, 'component name'); assert(commit === m.commit, `component ${name} does not match the application commit`);
  }
  assert(m.environment && typeof m.environment === 'object' && !Array.isArray(m.environment), 'environment is required');
  for (const name of ['id', 'os', 'arch', 'cpu', 'runtime', 'display', 'fonts', 'renderer']) string(m.environment[name], `environment.${name}`);
  if (m.feature_notes !== undefined) assert(typeof m.feature_notes === 'string', 'feature_notes must be a string');
}

function emptyHistory() { return { schema: 1, runs: [], confirmations: [] }; }
function validateHistory(history) {
  assert(history && history.schema === 1 && Array.isArray(history.runs) && Array.isArray(history.confirmations), 'invalid history schema');
  const checked = emptyHistory();
  for (const run of history.runs) {
    assert(typeof run.sample_text === 'string' && typeof run.summary_text === 'string', 'history is missing embedded source evidence');
    const rebuilt = importRunUnchecked(checked, run.manifest, run.sample_text, run.summary_text);
    assert(stable(rebuilt) === stable(run), `history evidence does not match run ${run.id}`);
  }
  for (const entry of history.confirmations) {
    assert(Array.isArray(entry.evidence) && entry.evidence.length >= 2 && new Set(entry.evidence).size === entry.evidence.length, 'invalid best evidence');
    const runs = entry.evidence.map(id => getRun(checked, id)), a = runs[0];
    for (const run of runs.slice(1)) independentPair(a, run);
    const values = runs.map(run => run.metrics[entry.metric]);
    assert(values.every(value => value && value.kind === entry.kind) && stable(entry.cohort) === stable(a.cohort) &&
      entry.commit === a.manifest.commit && entry.version === a.manifest.version &&
      Math.max(...values.map(v => v.median)) === entry.value && entry.value <= Math.min(...values.map(v => v.median)) * 1.1,
      `invalid historical best for ${entry.metric}`);
  }
  const keys = history.confirmations.map(entry => stable([entry.cohort, entry.metric, entry.kind, [...entry.evidence].sort()]));
  assert(new Set(keys).size === keys.length, 'duplicate historical confirmation');
}
function importRun(history, manifest, sampleText, summaryText) {
  validateHistory(history);
  return importRunUnchecked(history, manifest, sampleText, summaryText);
}
function importRunUnchecked(history, manifest, sampleText, summaryText) {
  validateManifest(manifest);
  const sample = JSON.parse(sampleText), summary = JSON.parse(summaryText), metrics = evidenceMetrics(sample, summary);
  const sampleHash = sha256(Buffer.from(sampleText, 'utf8'));
  assert(!history.runs.some(run => run.manifest.execution_id === manifest.execution_id), 'execution_id already imported');
  assert(!history.runs.some(run => run.sample_sha256 === sampleHash), 'identical sample evidence already imported');
  const id = sha256(stable({ manifest, sample, summary }));
  const run = { id, cohort: cohortOf(manifest), manifest, sample_sha256: sampleHash,
    summary_sha256: sha256(Buffer.from(summaryText, 'utf8')), sample_text: sampleText, summary_text: summaryText, sample, summary, metrics };
  history.runs.push(run);
  return run;
}
function evidenceMetrics(sample, summary) {
  const metrics = {};
  assert(sample && typeof sample === 'object' && summary && typeof summary === 'object', 'invalid evidence JSON');
  for (const group of ['elapsed', 'allocated']) {
    if (sample[group] === undefined) continue;
    assert(sample[group] && typeof sample[group] === 'object' && !Array.isArray(sample[group]), `invalid ${group} metrics`);
    for (const [name, values] of Object.entries(sample[group])) {
      string(name, 'metric name'); assert(!Object.hasOwn(metrics, name), `duplicate metric ${name}`);
      assert(Array.isArray(values) && values.length === 7 && values.every(v => typeof v === 'number' && Number.isFinite(v) && v > 0), `${name} requires exactly seven positive finite samples`);
      const value = median(values);
      assert(typeof summary[name] === 'number' && Number.isFinite(summary[name]) && Math.abs(summary[name] - value) <= Math.max(1e-7, Math.abs(value) * 1e-8), `summary median mismatch: ${name}`);
      metrics[name] = { kind: group === 'elapsed' ? 'milliseconds' : 'bytes', median: value };
    }
  }
  assert(Object.keys(metrics).length > 0, 'samples contain no supported metrics');
  return metrics;
}
function getRun(history, id) {
  const found = history.runs.filter(run => run.id === id);
  assert(found.length === 1, `unknown run ${id}`); return found[0];
}
function sameCohort(a, b) { return stable(a.cohort) === stable(b.cohort); }
function compatible(a, b) { assert(sameCohort(a, b), 'scenario, fixture, method or environment differ'); }
function independentPair(a, b) {
  assert(a.id !== b.id && a.sample_sha256 !== b.sample_sha256 && a.manifest.execution_id !== b.manifest.execution_id, 'two independent executions are required');
  compatible(a, b);
  assert(a.manifest.commit === b.manifest.commit && a.manifest.version === b.manifest.version, 'confirmation batches must use the same version and commit');
  assert(stable(Object.keys(a.metrics).sort()) === stable(Object.keys(b.metrics).sort()), 'confirmation metric sets differ');
}
function versionStability(history, reference, name, kind, excludedIds = [], cutoff = Infinity) {
  const excluded = new Set(excludedIds);
  const relevant = history.runs.filter(run => !excluded.has(run.id) && sameCohort(reference, run) &&
    run.manifest.commit === reference.manifest.commit && run.manifest.version === reference.manifest.version &&
    Date.parse(run.manifest.captured_at) <= cutoff);
  const valid = relevant.filter(run => run.metrics[name]?.kind === kind);
  if (valid.length !== relevant.length) return { status: 'incomplete_metrics', runs: valid };
  const values = valid.map(run => run.metrics[name].median);
  if (values.length < 2) return { status: 'single_batch', runs: valid };
  const min = Math.min(...values), max = Math.max(...values);
  return { status: max <= min * 1.1 ? 'stable' : 'unstable', runs: valid, min, max };
}
function confirm(history, ids) {
  validateHistory(history); assert(Array.isArray(ids) && ids.length === 2, 'exactly two run IDs are required');
  const [a, b] = ids.map(id => getRun(history, id)); independentPair(a, b);
  const updated = [], uncertain = [];
  for (const [name, metric] of Object.entries(a.metrics)) {
    const other = b.metrics[name];
    if (!other || other.kind !== metric.kind) { uncertain.push(name); continue; }
    const group = versionStability(history, a, name, metric.kind);
    if (group.status !== 'stable') { uncertain.push(name); continue; }
    const evidence = group.runs.map(run => run.id).sort();
    const existing = history.confirmations.some(item => stable(item.cohort) === stable(a.cohort) && item.metric === name && item.kind === metric.kind &&
      stable([...item.evidence].sort()) === stable(evidence));
    if (!existing) {
      const entry = { cohort: a.cohort, metric: name, kind: metric.kind, value: group.max,
        version: a.manifest.version, commit: a.manifest.commit, evidence };
      history.confirmations.push(entry);
      updated.push(entry);
    }
  }
  return { updated, uncertain };
}
function delta(current, previous, kind) {
  const difference = current - previous;
  const absoluteLimit = kind === 'milliseconds' ? 8 : 262144;
  return { value: previous, difference, percent: difference / previous * 100,
    alarm: current > previous * 1.3 && difference > absoluteLimit };
}
function historicalBest(history, current, name, kind, excludedIds = []) {
  const groups = [];
  const excluded = new Set(excludedIds), currentTime = Date.parse(current.manifest.captured_at);
  const confirmed = history.confirmations.filter(entry => stable(entry.cohort) === stable(current.cohort) && entry.metric === name && entry.kind === kind &&
    entry.evidence.every(id => !excluded.has(id) && Date.parse(getRun(history, id).manifest.captured_at) <= currentTime));
  const confirmedEvidence = new Set();
  for (const entry of confirmed) {
    const reference = getRun(history, entry.evidence[0]);
    const allKnown = versionStability(history, reference, name, kind);
    if (allKnown.status !== 'stable') continue;
    const group = versionStability(history, reference, name, kind, excludedIds, currentTime);
    if (group.status !== 'stable') continue;
    const evidence = group.runs.map(run => run.id).sort();
    for (const id of evidence) confirmedEvidence.add(id);
    groups.push({ value: group.max, status: 'confirmed', version_stability: 'stable',
      commit: entry.commit, version: entry.version, evidence, confirmation_evidence: entry.evidence });
  }
  for (const run of history.runs) {
    const metric = run.metrics[name];
    if (!sameCohort(run, current) || !metric || metric.kind !== kind || confirmedEvidence.has(run.id) || excluded.has(run.id) ||
        Date.parse(run.manifest.captured_at) > currentTime) continue;
    const group = versionStability(history, run, name, kind);
    groups.push({ value: metric.median, status: 'provisional', version_stability: group.status,
      commit: run.manifest.commit, version: run.manifest.version, evidence: [run.id] });
  }
  return groups.sort((a, b) => a.value - b.value || (a.status === 'confirmed' ? -1 : 1))[0] || null;
}
function selectedVersionTrend(history, ids, current, before) {
  assert(ids.length >= 3 && ids.at(-1) === current.id && ids.at(-2) === before.id, 'trend needs at least three ordered runs ending with before,current');
  const runs = ids.map(id => getRun(history, id));
  assert(new Set(runs.map(run => run.manifest.commit)).size === runs.length, 'trend versions must use distinct commits');
  for (let i = 0; i < runs.length; i++) {
    compatible(current, runs[i]);
    assert(stable(Object.keys(runs[i].metrics).sort()) === stable(Object.keys(current.metrics).sort()), 'trend metric sets differ');
    if (i) assert(Date.parse(runs[i - 1].manifest.captured_at) <= Date.parse(runs[i].manifest.captured_at), 'trend runs are not chronological');
  }
  const result = {};
  for (const [name, metric] of Object.entries(current.metrics)) {
    const points = runs.map(run => {
      const group = versionStability(history, run, name, metric.kind);
      if (group.status === 'unstable') return { version: run.manifest.version, commit: run.manifest.commit, status: 'unstable' };
      const confirmation = history.confirmations.filter(entry => entry.metric === name && entry.kind === metric.kind &&
        stable(entry.cohort) === stable(run.cohort) && entry.evidence.includes(run.id)).sort((a, b) => a.value - b.value)[0];
      if (!confirmation || group.status !== 'stable') return { version: run.manifest.version, commit: run.manifest.commit, status: 'unconfirmed' };
      return { version: run.manifest.version, commit: run.manifest.commit, status: 'confirmed',
        min_batch_median: group.min, max_batch_median: group.max, evidence: group.runs.map(item => item.id) };
    });
    const complete = points.every(point => point.status === 'confirmed');
    result[name] = { status: complete ? 'confirmed_pairs' : 'insufficient_evidence',
      stable_increase: complete ? points.slice(1).every((point, i) => point.min_batch_median > points[i].max_batch_median) : null, points };
  }
  return result;
}
function compare(history, currentId, beforeId, currentConfirmId, beforeConfirmId, trendIds) {
  validateHistory(history);
  const current = getRun(history, currentId), before = getRun(history, beforeId);
  compatible(current, before);
  assert(current.id !== before.id, 'current and before must be distinct executions');
  assert(Date.parse(before.manifest.captured_at) <= Date.parse(current.manifest.captured_at), 'before run is later than current');
  assert(stable(Object.keys(current.metrics).sort()) === stable(Object.keys(before.metrics).sort()), 'metric sets differ');
  assert(Boolean(currentConfirmId) === Boolean(beforeConfirmId), 'stable growth requires confirmations on both sides');
  let currentConfirm, beforeConfirm;
  if (currentConfirmId) {
    currentConfirm = getRun(history, currentConfirmId); beforeConfirm = getRun(history, beforeConfirmId);
    independentPair(current, currentConfirm); independentPair(before, beforeConfirm);
    assert(new Set([current.id, before.id, currentConfirm.id, beforeConfirm.id]).size === 4, 'confirmation batches must be independent across both sides');
  }
  const metrics = {};
  for (const [name, value] of Object.entries(current.metrics)) {
    const previous = before.metrics[name];
    assert(previous && previous.kind === value.kind, `before run lacks matching metric ${name}`);
    const best = historicalBest(history, current, name, value.kind, [currentId, currentConfirmId]);
    const candidate = historicalBest(history, current, name, value.kind);
    const currentStability = versionStability(history, current, name, value.kind);
    const beforeStability = versionStability(history, before, name, value.kind);
    let stableIncrease = null;
    if (currentConfirm && beforeConfirm) {
      const c = currentConfirm.metrics[name], b = beforeConfirm.metrics[name];
      assert(c && b && c.kind === value.kind && b.kind === value.kind, `confirmation lacks matching metric ${name}`);
      if (currentStability.status === 'stable' && beforeStability.status === 'stable')
        stableIncrease = currentStability.min > beforeStability.max;
    }
    metrics[name] = { kind: value.kind, current: value.median, before: delta(value.median, previous.median, value.kind),
      historical_best: best ? { ...delta(value.median, best.value, value.kind), status: best.status, version_stability: best.version_stability, commit: best.commit, version: best.version, evidence: best.evidence, confirmation_evidence: best.confirmation_evidence ?? null } : null,
      candidate_historical_best: candidate ? { value: candidate.value, status: candidate.status, version_stability: candidate.version_stability, commit: candidate.commit, version: candidate.version, evidence: candidate.evidence, confirmation_evidence: candidate.confirmation_evidence ?? null } : null,
      current_version_stability: currentStability.status, before_version_stability: beforeStability.status, stable_increase: stableIncrease };
  }
  const values = Object.values(metrics);
  return { cohort: current.cohort, current_commit: current.manifest.commit, before_commit: before.manifest.commit,
    historical_alarm: values.some(item => item.historical_best?.alarm), before_alarm: values.some(item => item.before.alarm),
    selected_version_trend: trendIds ? selectedVersionTrend(history, trendIds, current, before) : null, metrics };
}

function archiveReport(archiveFile, scenario, currentSummaryFile) {
  string(scenario, '--scenario');
  const archivePath = path.resolve(archiveFile), root = path.dirname(archivePath);
  const archive = JSON.parse(fs.readFileSync(archivePath, 'utf8'));
  assert(archive.schema === 'texeed-archived-performance-candidates-v1' && archive.status === 'unverified_historical_candidates', 'unrecognized historical candidate archive');
  assert(archive.scenarios && Object.hasOwn(archive.scenarios, scenario), `unknown archived scenario ${scenario}`);
  const description = archive.scenarios[scenario];
  assert(hex(64).test(description.fixture_sha256 || ''), 'archived scenario lacks fixture hash');
  const fixture = fs.readFileSync(path.resolve(root, description.fixture));
  assert(sha256(fixture) === description.fixture_sha256, 'archived fixture hash mismatch');
  assert(Array.isArray(archive.records) && Array.isArray(archive.limitations) && archive.limitations.length > 0, 'archive lacks records or limitations');
  const winners = {}, records = archive.records.filter(record => record.scenario === scenario);
  assert(records.length > 0, 'archived scenario has no records');
  for (const record of records) {
    string(record.directory, 'archived record directory'); string(record.version, 'archived version');
    assert(!path.isAbsolute(record.directory), 'archived record directory must be relative');
    const directory = path.resolve(root, record.directory), relative = path.relative(root, directory);
    assert(relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative), 'archived record is outside archive');
    const summaryFile = path.join(directory, 'ui-performance.json'), sampleFile = path.join(directory, 'ui-performance-samples.json');
    const summaryText = fs.readFileSync(summaryFile, 'utf8'), sampleText = fs.readFileSync(sampleFile, 'utf8');
    const hashes = archive.record_sha256?.[record.directory];
    assert(hashes && hex(64).test(hashes['ui-performance.json'] || '') && hex(64).test(hashes['ui-performance-samples.json'] || ''), 'archived record lacks evidence hashes');
    assert(sha256(Buffer.from(summaryText, 'utf8')) === hashes['ui-performance.json'] && sha256(Buffer.from(sampleText, 'utf8')) === hashes['ui-performance-samples.json'], 'archived evidence hash mismatch');
    const metrics = evidenceMetrics(JSON.parse(sampleText), JSON.parse(summaryText));
    for (const [name, metric] of Object.entries(metrics)) {
      const previous = winners[name];
      if (!previous || metric.median < previous.value) winners[name] = { value: metric.median, kind: metric.kind,
        version: record.version, related_commit_not_verified: archive.versions?.[record.version]?.related_commit ?? null,
        evidence: { summary: path.relative(root, summaryFile), samples: path.relative(root, sampleFile) },
        status: 'unverified_historical_candidate' };
    }
  }
  let current = null;
  if (currentSummaryFile) {
    const summary = JSON.parse(fs.readFileSync(currentSummaryFile, 'utf8'));
    current = {};
    for (const [name, best] of Object.entries(winners)) {
      assert(typeof summary[name] === 'number' && Number.isFinite(summary[name]) && summary[name] > 0, `current summary lacks positive metric ${name}`);
      current[name] = { current: summary[name], historical: delta(summary[name], best.value, best.kind),
        status: 'method_and_environment_unverified' };
    }
  }
  return { status: 'unverified_historical_candidates', comparison_status: current ? 'method_and_environment_unverified' : null,
    scenario, scenario_description: description, method_as_archived: archive.common_method,
    limitations: archive.limitations, records_considered: records.length, metrics: winners, current };
}

function args(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i += 2) {
    assert(argv[i].startsWith('--') && argv[i + 1] !== undefined && !argv[i + 1].startsWith('--'), `expected --option value: ${argv[i]}`);
    assert(!Object.hasOwn(result, argv[i].slice(2)), `duplicate option ${argv[i]}`);
    result[argv[i].slice(2)] = argv[i + 1];
  }
  return result;
}
function readHistory(file) { return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : emptyHistory(); }
function save(file, history) {
  const temp = path.join(path.dirname(path.resolve(file)), `.${path.basename(file)}.${process.pid}.${crypto.randomBytes(6).toString('hex')}.tmp`);
  try { fs.writeFileSync(temp, JSON.stringify(history, null, 2) + '\n', { flag: 'wx' }); fs.renameSync(temp, file); }
  finally { if (fs.existsSync(temp)) fs.unlinkSync(temp); }
}
function main(argv) {
  if (!argv.length || argv[0] === '--help') { process.stdout.write(HELP + '\n'); return; }
  const [command, ...tail] = argv, options = args(tail);
  if (command === 'archive-report') {
    string(options.archive, '--archive');
    process.stdout.write(JSON.stringify(archiveReport(options.archive, options.scenario, options['current-summary']), null, 2) + '\n');
    return;
  }
  string(options.history, '--history');
  const history = readHistory(options.history); let result;
  if (command === 'import') {
    for (const key of ['manifest', 'samples', 'summary']) string(options[key], `--${key}`);
    result = importRun(history, JSON.parse(fs.readFileSync(options.manifest, 'utf8')), fs.readFileSync(options.samples, 'utf8'), fs.readFileSync(options.summary, 'utf8'));
    save(options.history, history); result = { imported: result.id, metrics: result.metrics };
  } else if (command === 'confirm') {
    string(options.runs, '--runs'); result = confirm(history, options.runs.split(',')); save(options.history, history);
  } else if (command === 'compare') {
    string(options.current, '--current'); string(options.before, '--before');
    result = compare(history, options.current, options.before, options['current-confirm'], options['before-confirm'], options.trend?.split(','));
  } else throw new Error(`unknown command: ${command}`);
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
  if (command === 'compare' && result.historical_alarm) process.exitCode = 2;
}
if (require.main === module) try { main(process.argv.slice(2)); } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
module.exports = { emptyHistory, importRun, confirm, compare, archiveReport, main };
