'use strict';
// Rebuild/verify the index from emoji-datasource metadata and the clean RGBA sprite sheets.
const fs = require('node:fs'), zlib = require('node:zlib');
const [metadataPath, googlePath, twitterPath, outputPath] = process.argv.slice(2);
if (!metadataPath || !googlePath || !twitterPath) {
  console.error('Usage: node scripts/emoji-index.cjs emoji.json noto.png twemoji.png [output-index.json]');
  process.exit(2);
}
function readSheet(path) {
  const data = fs.readFileSync(path), signature = '89504e470d0a1a0a';
  if (data.subarray(0, 8).toString('hex') !== signature || data[24] !== 8 || data[25] !== 6 || data[28] !== 0)
    throw new Error(`${path}: expected non-interlaced 8-bit RGBA PNG`);
  const width = data.readUInt32BE(16), height = data.readUInt32BE(20), chunks = [];
  for (let at = 8; at < data.length;) {
    const length = data.readUInt32BE(at), kind = data.toString('ascii', at + 4, at + 8);
    if (kind === 'IDAT') chunks.push(data.subarray(at + 8, at + 8 + length));
    at += length + 12;
    if (kind === 'IEND') break;
  }
  const raw = zlib.inflateSync(Buffer.concat(chunks)), stride = width * 4, pixels = Buffer.alloc(height * stride);
  if (raw.length !== height * (stride + 1)) throw new Error(`${path}: unexpected scanline length`);
  const paeth = (a, b, c) => { const p = a + b - c, x = Math.abs(p - a), y = Math.abs(p - b), z = Math.abs(p - c); return x <= y && x <= z ? a : y <= z ? b : c; };
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)], row = y * stride, input = y * (stride + 1) + 1;
    if (filter > 4) throw new Error(`${path}: unsupported PNG filter ${filter}`);
    for (let x = 0; x < stride; x++) {
      const left = x >= 4 ? pixels[row + x - 4] : 0, up = y ? pixels[row + x - stride] : 0, diagonal = y && x >= 4 ? pixels[row + x - stride - 4] : 0;
      const delta = filter === 0 ? 0 : filter === 1 ? left : filter === 2 ? up : filter === 3 ? Math.floor((left + up) / 2) : paeth(left, up, diagonal);
      pixels[row + x] = (raw[input + x] + delta) & 255;
    }
  }
  return { width, height, pixels };
}
function hasInk(sheet, x, y) {
  const left = x * 34 + 1, top = y * 34 + 1;
  if (left < 0 || top < 0 || left + 32 > sheet.width || top + 32 > sheet.height) return false;
  for (let row = top; row < top + 32; row++) for (let column = left; column < left + 32; column++)
    if (sheet.pixels[(row * sheet.width + column) * 4 + 3] !== 0) return true;
  return false;
}
function unicode(hex) { return String.fromCodePoint(...hex.split('-').map(part => Number.parseInt(part, 16))); }
const metadata = JSON.parse(fs.readFileSync(metadataPath, 'utf8'));
const google = readSheet(googlePath), twitter = readSheet(twitterPath);
const index = {}, unsupported = [], counts = { records: 0, google: 0, twitterFallback: 0, missing: 0 };
for (const entry of metadata) for (const item of [entry, ...Object.values(entry.skin_variations || {})]) {
  const x = item.sheet_x, y = item.sheet_y;
  const hasGoogle = item.has_img_google === true && hasInk(google, x, y);
  const hasTwitter = item.has_img_twitter === true && hasInk(twitter, x, y);
  counts.records++;
  if (!hasGoogle && !hasTwitter) { counts.missing++; unsupported.push(item.unified); continue; }
  const source = hasGoogle ? 0 : 1; counts[source ? 'twitterFallback' : 'google']++;
  const value = [x, y, source], keys = [item.unified];
  // Unqualified text-default symbols remain text, while emoji-default bases keep their alias.
  if (item.non_qualified && Number.parseInt(item.non_qualified.split('-')[0], 16) >= 0x1f000) keys.push(item.non_qualified);
  for (const key of keys) index[unicode(key)] = value;
}
if (outputPath) fs.writeFileSync(outputPath, JSON.stringify(index));
else {
  const current = JSON.parse(fs.readFileSync('src/TeXeed.App/Assets/Emoji/index.json', 'utf8'));
  const keys = Object.keys(index), mismatched = keys.filter(key => JSON.stringify(index[key]) !== JSON.stringify(current[key]));
  const extra = Object.keys(current).filter(key => !Object.hasOwn(index, key));
  if (mismatched.length || extra.length) throw new Error(`Index differs: ${mismatched.length} missing/mismatched, ${extra.length} extra.`);
}
console.log(JSON.stringify({ ...counts, keys: Object.keys(index).length, unsupported: unsupported.slice(0, 12), output: outputPath || 'verified current index' }));
