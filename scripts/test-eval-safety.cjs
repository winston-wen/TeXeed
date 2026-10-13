// Completely offline: no credentials, CLI sessions, WPF, or model requests.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createRedactor, redactLines, assertRestrictedHelp, cleanChildEnvironment, reserveRequest, successfulResult } = require('./eval-safety.cjs');

async function main() {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'texeed-eval-safety-'));
  let checks = 0;
  const check = (value, message) => { assert.ok(value, message); checks++; };
  try {
    const secret = 'sk-FAKE-SECRET-FOR-OFFLINE-TEST'; const local = 'fake-local-token';
    const redactor = createRedactor([secret, local]);
    const scrubbed = JSON.stringify(redactor.value({ text: `token ${secret}`, nested: [{ input: local, Authorization: 'another-value', encrypted_content: 'hidden' }], usage: { input_tokens: 15 } }));
    check(!scrubbed.includes(secret) && !scrubbed.includes(local) && !scrubbed.includes('another-value') && !scrubbed.includes('hidden'), 'Nested logs must redact every credential sink.');
    check(scrubbed.includes('input_tokens') && scrubbed.includes('15'), 'Useful usage metadata must survive redaction.');
    check(!redactor.text('Bearer OTHER_ACCESS_TOKEN').includes('OTHER_ACCESS_TOKEN'), 'Unknown bearer values must be masked.');
    const emitted = []; const lines = redactLines(redactor, value => emitted.push(value));
    const bytes = Buffer.from(`中文 ${secret}\n尾行 ${local}`);
    for (let index = 0; index < bytes.length; index++) lines.push(bytes.subarray(index, index + 1));
    lines.end();
    check(!emitted.join('').includes(secret) && !emitted.join('').includes(local), 'Secrets split across arbitrary stream chunks must never be emitted.');
    check(emitted.join('').includes('中文') && emitted.join('').includes('尾行'), 'Chunk handling must preserve UTF-8.');
    const env = cleanChildEnvironment({ PATH: '/bin', SystemRoot: 'C:/Windows', ANTHROPIC_AUTH_TOKEN: secret, OPENAI_API_KEY: secret, EXTRA_SECRET: secret, HTTP_PROXY: 'http://localhost:7890' });
    check(Object.values(env).every(value => value !== secret) && env.PATH && env.HTTP_PROXY, 'Child environment must exclude provider credentials while retaining execution settings.');
    assertRestrictedHelp('--restricted Also confines the file\n tools to the working directories (--add-dir included).'); checks++;
    assert.throws(() => assertRestrictedHelp('--restricted removes command tools only.')); checks++;
    check(successfulResult({ type: 'result', subtype: 'success', is_error: false }), 'Normal result must count as terminal completion.');
    check(!successfulResult({ type: 'result', subtype: 'error_max_turns' }) && !successfulResult({ type: 'result', subtype: 'success', is_error: true }) && !successfulResult(null), 'Error result must never be reported as successful completion.');

    const ledger = path.join(temp, 'budget.json');
    fs.writeFileSync(ledger, JSON.stringify({ sol: 199, deepseek: 499 }));
    const counter = { used: 0, limit: 25 };
    const attempts = await Promise.allSettled(Array.from({ length: 3 }, () => reserveRequest(ledger, 'deepseek', counter)));
    check(attempts.filter(result => result.status === 'fulfilled').length === 1, 'Concurrent calls must not exceed the global DeepSeek cap.');
    check(JSON.parse(fs.readFileSync(ledger)).deepseek === 500 && counter.used === 1, 'Ledger must count exactly the accepted request.');
    await reserveRequest(ledger, 'sol', { used: 0, limit: 25 });
    await assert.rejects(() => reserveRequest(ledger, 'sol', { used: 0, limit: 25 })); checks++;
    check(JSON.parse(fs.readFileSync(ledger)).sol === 200 && JSON.parse(fs.readFileSync(ledger)).deepseek === 500, 'Both authorized global caps must be preserved.');

    fs.writeFileSync(ledger, JSON.stringify({ deepseek: 0 }));
    const localCap = { used: 0, limit: 1 };
    const localAttempts = await Promise.allSettled(Array.from({ length: 3 }, () => reserveRequest(ledger, 'deepseek', localCap)));
    check(localAttempts.filter(result => result.status === 'fulfilled').length === 1 && JSON.parse(fs.readFileSync(ledger)).deepseek === 1, 'Concurrent calls must respect the per-case cap too.');

    const lease = ledger + '.lease'; const owner = fs.openSync(lease, 'wx');
    try {
      await assert.rejects(() => reserveRequest(ledger, 'deepseek', { used: 0, limit: 25 }, 30), /Timed out/); checks++;
      check(fs.existsSync(lease), 'Timeout must not steal or delete another process lease.');
    } finally { fs.closeSync(owner); fs.unlinkSync(lease); }
    fs.writeFileSync(ledger, JSON.stringify({ deepseek: -1 }));
    await assert.rejects(() => reserveRequest(ledger, 'deepseek', { used: 0, limit: 25 }), /Invalid shared/); checks++;
    check(JSON.parse(fs.readFileSync(ledger)).deepseek === -1 && !fs.existsSync(lease), 'Corrupt counts must fail closed and release only our own lease.');
    console.log(`PASS ${checks} offline evaluation safety checks (no network, credentials, or WPF).`);
  } finally {
    const resolved = path.resolve(temp); const allowed = path.resolve(os.tmpdir()) + path.sep;
    if (!resolved.startsWith(allowed) || !path.basename(resolved).startsWith('texeed-eval-safety-')) throw new Error('Unsafe test cleanup path.');
    fs.rmSync(resolved, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
