'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const root = __dirname, manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
for (const [file, expected] of Object.entries(manifest.files))
  assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(root, file))).digest('hex'), expected, file);
const run = JSON.parse(fs.readFileSync(path.join(root, 'run-1.json'), 'utf8'));
for (const condition of [run.noMarkerCost, run.manyMarkerCost])
  for (const name of ['Scroll', 'Resize']) {
    for (const [suffix, value] of [['Ms', condition[name + 'Ms']], ['Bytes', condition[name + 'Bytes']]]) {
      const samples = condition[name + 'Samples' + suffix];
      assert.equal(samples.length, 7);
      assert.equal([...samples].sort((a, b) => a - b)[3], value);
    }
  }
console.log('Bookmark marker fixture, source snapshot, and seven samples verified.');
