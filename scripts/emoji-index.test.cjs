'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), os = require('node:os'), path = require('node:path'), cp = require('node:child_process'), zlib = require('node:zlib');
const root = path.resolve(__dirname, '..'), temp = fs.mkdtempSync(path.join(os.tmpdir(), 'texeed-emoji-index-'));
function chunk(name, payload) {
  const type = Buffer.from(name), input = Buffer.concat([type, payload]); let crc = 0xffffffff;
  for (const byte of input) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = crc & 1 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1; }
  const head = Buffer.alloc(4), tail = Buffer.alloc(4); head.writeUInt32BE(payload.length); tail.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
  return Buffer.concat([head, input, tail]);
}
function sheet(firstCellVisible) {
  const width = 68, height = 34, header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 6;
  const stride = width * 4 + 1, data = Buffer.alloc(stride * height);
  if (firstCellVisible) data[stride + 1 + 1 * 4 + 3] = 255;
  data[stride + 1 + 35 * 4 + 3] = 255;
  return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(data)), chunk('IEND', Buffer.alloc(0))]);
}
try {
  const metadata = path.join(temp, 'emoji.json'), output = path.join(temp, 'index.json'), google = path.join(temp, 'noto.png'), twitter = path.join(temp, 'twemoji.png');
  fs.writeFileSync(google, sheet(false)); fs.writeFileSync(twitter, sheet(true));
  fs.writeFileSync(metadata, JSON.stringify([
    { unified: '1F600', non_qualified: null, sheet_x: 0, sheet_y: 0, has_img_google: true, has_img_twitter: true },
    { unified: '1F603', non_qualified: null, sheet_x: 1, sheet_y: 0, has_img_google: true, has_img_twitter: true },
    { unified: '1F604', non_qualified: null, sheet_x: 1000, sheet_y: 1000, has_img_google: true, has_img_twitter: true }
  ]));
  const run = cp.spawnSync(process.execPath, [path.join(root, 'scripts/emoji-index.cjs'), metadata, google, twitter, output], { encoding: 'utf8', cwd: root });
  assert.equal(run.status, 0, run.stderr);
  const index = JSON.parse(fs.readFileSync(output, 'utf8'));
  assert.deepEqual(index['😀'], [0, 0, 1], 'when metadata claims Noto exists but pixels are empty choose Twemoji');
  assert.deepEqual(index['😃'], [1, 0, 0], 'prefer Noto when both exist');
  assert.equal(Object.hasOwn(index, '😄'), false, 'skip empty or out-of-range cells');
  const counts = JSON.parse(run.stdout);
  assert.equal(counts.twitterFallback, 1); assert.equal(counts.missing, 1);
  console.log('PASS emoji index: Noto preference, Twemoji-only fallback and missing pixels.');
} finally { for (const name of ['emoji.json', 'index.json', 'noto.png', 'twemoji.png']) { const file = path.join(temp, name); if (fs.existsSync(file)) fs.unlinkSync(file); } fs.rmdirSync(temp); }
