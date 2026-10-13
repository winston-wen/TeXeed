// Shared, offline-testable safeguards for opt-in model comparisons.
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { StringDecoder } = require('node:string_decoder');

function createRedactor(secrets = []) {
  const exact = secrets.filter(value => typeof value === 'string' && value.length > 0).sort((a, b) => b.length - a.length);
  const text = value => {
    let result = String(value);
    for (const secret of exact) result = result.replaceAll(secret, '[redacted]');
    return result.replace(/Bearer\s+[A-Za-z0-9_.~+/-]+={0,2}/gi, 'Bearer [redacted]')
      .replace(/\bsk-[A-Za-z0-9_-]+\b/g, '[redacted]');
  };
  const value = item => {
    if (typeof item === 'string') return text(item);
    if (Array.isArray(item)) return item.map(value);
    if (item && ['image', 'input_image', 'image_url'].includes(item.type)) return { type: item.type, content: '[binary omitted]' };
    if (item && typeof item === 'object') return Object.fromEntries(Object.entries(item).map(([name, entry]) => [name,
      /^(?:authorization|x-api-key|api[_-]?key|(?:access|refresh|id|auth)[_-]?token|ANTHROPIC_AUTH_TOKEN|encrypted_content)$/i.test(name)
        ? '[redacted]' : value(entry)]));
    return item;
  };
  return { text, value };
}

// Hold incomplete lines so a credential split between two chunks is never
// written before the remainder arrives. Decode UTF-8 without corrupting CJK.
function redactLines(redactor, emit) {
  const decoder = new StringDecoder('utf8');
  let pending = '';
  function drain(final) {
    let index;
    while ((index = pending.indexOf('\n')) >= 0) {
      emit(redactor.text(pending.slice(0, index + 1))); pending = pending.slice(index + 1);
    }
    if (pending.length > 1_000_000) { pending = ''; emit('[oversized diagnostic line omitted]\n'); }
    if (final && pending) { emit(redactor.text(pending)); pending = ''; }
  }
  return { push: chunk => { pending += decoder.write(chunk); drain(false); }, end: () => { pending += decoder.end(); drain(true); } };
}

function assertRestrictedHelp(help) {
  const normalized = String(help).replace(/\s+/g, ' ').toLowerCase();
  if (!normalized.includes('--restricted') || !normalized.includes('confines the file tools to the working directories'))
    throw new Error('This CLI does not document the required restricted filesystem boundary; comparison refused.');
}

function cleanChildEnvironment(source) {
  return Object.fromEntries(Object.entries(source).filter(([name]) =>
    !/(?:token|secret|api_?key|credential)/i.test(name) && !/^(?:ANTHROPIC|OPENAI|DEEPSEEK)_/i.test(name)));
}

async function reserveRequest(ledgerFile, provider, counter, waitMs = 15000) {
  const hardCap = { sol: 200, deepseek: 500 }[provider];
  if (!hardCap) throw new Error('Provider outside comparison authorization.');
  fs.mkdirSync(path.dirname(ledgerFile), { recursive: true });
  const lease = ledgerFile + '.lease'; const deadline = Date.now() + waitMs;
  let fd;
  while (fd === undefined) {
    try { fd = fs.openSync(lease, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      if (Date.now() >= deadline) throw new Error('Timed out waiting for the shared evaluation budget lease.');
      await new Promise(resolve => setTimeout(resolve, Math.min(100, Math.max(1, deadline - Date.now()))));
    }
  }
  try {
    // Check the local cap inside the lease too: concurrent CLI requests may
    // already have consumed the last local slot while this one was waiting.
    if (counter.used >= counter.limit) throw new Error(`Comparison case request cap (${counter.limit}) reached.`);
    const ledger = fs.existsSync(ledgerFile) ? JSON.parse(fs.readFileSync(ledgerFile, 'utf8')) : {};
    const count = ledger[provider] ?? 0;
    if (!Number.isSafeInteger(count) || count < 0) throw new Error('Invalid shared request ledger; refusing to reset it.');
    if (count >= hardCap) throw new Error(`Authorized ${provider} request cap (${hardCap}) reached.`);
    ledger[provider] = count + 1; ledger.updatedAt = new Date().toISOString();
    const temporary = ledgerFile + '.' + randomUUID() + '.tmp';
    try { fs.writeFileSync(temporary, JSON.stringify(ledger, null, 2)); fs.renameSync(temporary, ledgerFile); }
    finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
    counter.used++;
    return { total: ledger[provider], time: ledger.updatedAt };
  } finally { fs.closeSync(fd); fs.unlinkSync(lease); }
}

function successfulResult(result) { return result?.type === 'result' && result.subtype === 'success' && result.is_error !== true; }
module.exports = { createRedactor, redactLines, assertRestrictedHelp, cleanChildEnvironment, reserveRequest, successfulResult };
