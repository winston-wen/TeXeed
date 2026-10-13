// Opt-in comparison with an already installed Claude Code CLI and the explicitly
// authorized local DeepSeek connection. Usage: node eval-claude.cjs <cli> <copy>
// <prompt.txt> <output-dir> <shared-ledger.json>. Read-only tools, no shell access.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn, spawnSync } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const { StringDecoder } = require('node:string_decoder');
const { createRedactor, redactLines, assertRestrictedHelp, cleanChildEnvironment, reserveRequest, successfulResult } = require('./eval-safety.cjs');
if (process.argv.length !== 7) throw new Error('Usage: node eval-claude.cjs <cli> <copy> <prompt.txt> <output-dir> <shared-ledger.json>');
const [cli, copy, promptFile, outputDir, ledgerFile] = process.argv.slice(2).map(p => path.resolve(p));
if (!fs.existsSync(path.join(copy, '.texeed-evaluation-copy'))) throw new Error('Only a marked project copy may be evaluated.');
// This is an offline capability check. Older CLIs whose restricted mode does
// not document file-tool confinement must not silently run the comparison.
const help = spawnSync(cli, ['--help'], { encoding: 'utf8', windowsHide: true, timeout: 10000,
  env: { ...cleanChildEnvironment(process.env), CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' } });
if (help.error || help.status !== 0) throw new Error('Could not verify the installed CLI restricted-mode boundary.');
assertRestrictedHelp(help.stdout);
if (['events.jsonl', 'result.json', 'answer.md'].some(name => fs.existsSync(path.join(outputDir, name))))
  throw new Error('Use a fresh comparison output directory to avoid mixing runs.');
let settings;
try { settings = JSON.parse(fs.readFileSync(path.join(process.env.USERPROFILE, '.claude/settings.json'), 'utf8')); }
catch { throw new Error('Could not read the authorized local DeepSeek configuration.'); }
if (settings.env?.ANTHROPIC_BASE_URL?.replace(/\/$/, '') !== 'https://api.deepseek.com/anthropic') throw new Error('Expected the authorized official DeepSeek connection.');
const key = settings.env.ANTHROPIC_AUTH_TOKEN;
if (!key) throw new Error('No DeepSeek connection.');
fs.mkdirSync(outputDir, { recursive: true });
const eventFile = path.join(outputDir, 'events.jsonl');
const localToken = randomUUID(); const runId = randomUUID();
const redactor = createRedactor([key, localToken]);
const log = value => fs.appendFileSync(eventFile, JSON.stringify(redactor.value({ runId, ...value })) + '\n');
const counter = { used: 0, limit: 25 };
async function count() {
  const { total, time } = await reserveRequest(ledgerFile, 'deepseek', counter);
  log({ type: 'request', time, provider: 'deepseek', harness: 'claude-code', requests: counter.used, total });
  console.log(`Claude/DeepSeek request ${counter.used}/25; total ${total}/500`);
}
const server = http.createServer(async (req, res) => {
  try {
    const requestUrl = new URL(req.url, 'http://localhost'); const endpoint = requestUrl.pathname;
    if (req.method !== 'POST' || !['/anthropic/v1/messages', '/anthropic/v1/messages/count_tokens'].includes(endpoint))
      throw new Error('Endpoint outside comparison scope: ' + endpoint);
    if (req.headers.authorization !== `Bearer ${localToken}` && req.headers['x-api-key'] !== localToken) throw new Error('Unrecognized local client.');
    const chunks = []; let bytes = 0;
    for await (const chunk of req) { bytes += chunk.length; if (bytes > 12e6) throw new Error('Request too large.'); chunks.push(chunk); }
    const body = Buffer.concat(chunks); const json = JSON.parse(body);
    if (!String(json.model).startsWith('deepseek-flash')) throw new Error('Unexpected model.');
    if (endpoint.endsWith('/messages')) await count();
    const upstream = new URL(endpoint, 'https://api.deepseek.com'); upstream.search = requestUrl.search;
    const response = await fetch(upstream, {
      method: 'POST', body,
      headers: { 'content-type': 'application/json', authorization: `Bearer ${key}`, 'anthropic-version': req.headers['anthropic-version'] || '2023-06-01' },
      signal: AbortSignal.timeout(240000)
    });
    res.writeHead(response.status, { 'content-type': response.headers.get('content-type') || 'application/json' });
    for await (const chunk of response.body) res.write(chunk);
    res.end();
  } catch (error) {
    if (!res.headersSent) res.writeHead(400, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ type: 'error', error: { type: 'invalid_request_error', message: redactor.text(error.message) } }));
  }
});
server.listen(0, '127.0.0.1', () => {
  const env = { ...cleanChildEnvironment(process.env),
    ANTHROPIC_BASE_URL: `http://127.0.0.1:${server.address().port}/anthropic`,
    ANTHROPIC_AUTH_TOKEN: localToken, ANTHROPIC_API_KEY: localToken,
    CLAUDE_CONFIG_DIR: path.join(outputDir, 'profile'), CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
    DISABLE_NON_ESSENTIAL_MODEL_CALLS: '1', DISABLE_TELEMETRY: '1', CLAUDE_CODE_EFFORT_LEVEL: 'low' };
  const prompt = fs.readFileSync(promptFile, 'utf8');
  const child = spawn(cli, ['--print', '--restricted', '--tools', 'Read,Glob,Grep', '--allowedTools', 'Read,Glob,Grep',
    '--permission-mode', 'dontAsk', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}',
    '--no-session-persistence', '--disable-slash-commands', '--model', 'deepseek-flash', '--effort', 'low',
    '--output-format', 'stream-json', '--verbose', prompt], { cwd: copy, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let partial = ''; let final; const stdoutDecoder = new StringDecoder('utf8');
  function lineReceived(line) {
    try {
      const event = JSON.parse(line);
      const content = Array.isArray(event.message?.content) ? event.message.content : [];
      if (event.type === 'assistant') log({ type: 'assistant', content: content.filter(b => ['text', 'tool_use'].includes(b.type)) });
      if (event.type === 'user') log({ type: 'tool-results', content: content.filter(b => b.type === 'tool_result') });
      if (event.type === 'result') { final = redactor.value(event); log(final); fs.writeFileSync(path.join(outputDir, 'answer.md'), redactor.text(event.result || '')); }
    } catch { /* Do not log arbitrary startup output or credential-bearing diagnostics. */ }
  }
  child.stdout.on('data', chunk => {
    partial += stdoutDecoder.write(chunk);
    let index;
    while ((index = partial.indexOf('\n')) >= 0) {
      const line = partial.slice(0, index); partial = partial.slice(index + 1);
      lineReceived(line);
    }
  });
  child.stdout.on('end', () => { partial += stdoutDecoder.end(); if (partial.trim()) lineReceived(partial); partial = ''; });
  const stderr = redactLines(redactor, error => {
    fs.appendFileSync(path.join(outputDir, 'stderr.txt'), error);
    console.error(error.slice(0, 1000));
  });
  child.stderr.on('data', chunk => stderr.push(chunk));
  const deadline = setTimeout(() => child.kill(), 360000);
  child.on('close', code => {
    clearTimeout(deadline); stderr.end(); server.close();
    const completed = code === 0 && successfulResult(final);
    fs.writeFileSync(path.join(outputDir, 'result.json'), JSON.stringify(redactor.value({ runId, harness: 'Claude Code', model: 'deepseek-flash', effort: 'low', requests: counter.used, exitCode: code, completed, final }), null, 2));
    console.log(`Claude comparison finished: ${counter.used} requests; exit ${code}; completed ${completed}`); process.exitCode = completed ? 0 : 1;
  });
  child.on('error', error => { clearTimeout(deadline); stderr.end(); server.close(); console.error(redactor.text(error.message)); process.exitCode = 1; });
});
