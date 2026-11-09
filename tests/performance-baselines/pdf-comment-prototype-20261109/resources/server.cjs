'use strict';
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const {execFile} = require('node:child_process');
const {promisify} = require('node:util');
const run = promisify(execFile);
const staticRoot = __dirname;
const workRoot = process.env.TEXEED_PDF_PROTOTYPE_WORK_ROOT ? path.resolve(process.env.TEXEED_PDF_PROTOTYPE_WORK_ROOT) : path.join(os.tmpdir(), 'texeed-pdf-review-prototype');
const xelatex = 'C:/texlive/2026/bin/windows/xelatex.exe';
const synctex = 'C:/texlive/2026/bin/windows/synctex.exe';
const poppler = path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/native/poppler/Library/bin');
const pdfinfo = path.join(poppler, 'pdfinfo.exe');
const pdftoppm = path.join(poppler, 'pdftoppm.exe');
const jobs = new Map();
const validId = id => id === 'session' || id === 'project';
const validRevision = revision => /^[a-f0-9]{64}$/.test(revision || '');
const jobDir = (id, rev) => path.join(workRoot, id, rev);
const respond = (res, status, body) => { res.writeHead(status, {'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}); res.end(JSON.stringify(body)); };
async function readBody(req) {
 if (!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] || '')) throw Error('请求必须使用 JSON。');
 const parts = []; let bytes = 0;
 for await (const chunk of req) { bytes += chunk.length; if (bytes > 500000) throw Error('源码超过原型限制（500 KB）。'); parts.push(chunk); }
 return JSON.parse(Buffer.concat(parts).toString('utf8'));
}
async function compile(id, source) {
 const rev = crypto.createHash('sha256').update(source, 'utf8').digest('hex');
 const key = id + '/' + rev;
 if (!jobs.has(key)) jobs.set(key, (async () => {
  const dir = jobDir(id, rev);
  await fs.mkdir(dir, {recursive:true});
  await fs.writeFile(path.join(dir, 'main.tex'), source, 'utf8');
  try {
   await run(xelatex, ['-no-shell-escape','-interaction=nonstopmode','-halt-on-error','-synctex=1','-output-directory', dir, 'main.tex'], {cwd:dir, timeout:30000, maxBuffer:2*1024*1024, windowsHide:true});
   await fs.access(path.join(dir, 'main.pdf'));
   await fs.access(path.join(dir, 'main.synctex.gz'));
  } catch (error) { throw Error(('编译失败：' + (error.stderr || error.stdout || error.message)).slice(-5000)); }
  const info = await run(pdfinfo, [path.join(dir,'main.pdf')], {timeout:10000, maxBuffer:1024*1024, windowsHide:true});
  const pageCount = Number(/^Pages:\s*(\d+)/m.exec(info.stdout)?.[1]);
  if (!Number.isInteger(pageCount) || pageCount < 1) throw Error('无法读取 PDF 页数。');
  return {ok:true,revision:rev,pdfUrl:'/api/pdf/' + id + '/' + rev,pageCount};
 })().catch(error => { jobs.delete(key); throw error; }));
 return jobs.get(key);
}
async function locate(id, rev, line, column) {
 const dir = jobDir(id,rev);
 await fs.access(path.join(dir, 'main.pdf'));
 const sourcePath = path.join(dir, 'main.tex').replaceAll('\\', '/');
 const result = await run(synctex, ['view','-i',`${line}:${column}:${sourcePath}`,'-o','main.pdf'], {cwd:dir, timeout:10000, maxBuffer:1024*1024, windowsHide:true});
 const text = result.stdout;
 const fields = {};
 for (const match of text.matchAll(/^(Page|x|y|h|v|W|H):\s*([^\r\n]+)/gm)) if (!(match[1] in fields)) fields[match[1]] = Number(match[2]);
 if (!Number.isFinite(fields.Page) || !Number.isFinite(fields.h) || !Number.isFinite(fields.v)) throw Error('SyncTeX 未找到该行的 PDF 位置。');
 return {ok:true,page:fields.Page,x:fields.h,y:fields.v,width:fields.W||0,height:fields.H||0,precision:'line'};
}
const server = http.createServer(async (req,res) => {
 try {
  const url = new URL(req.url, 'http://127.0.0.1');
  const origin = req.headers.origin;
  if (origin && origin !== `http://127.0.0.1:${server.address().port}` && origin !== `http://localhost:${server.address().port}`) return respond(res,403,{ok:false,error:'来源不受支持。'});
  if (req.method === 'GET' && url.pathname === '/api/health') return respond(res,200,{ok:true,service:'texeed-pdf-comment-review',pid:process.pid});
  if (req.method === 'POST' && url.pathname === '/api/compile') {
   const body = await readBody(req);
   if (!validId(body.documentId) || typeof body.source !== 'string' || !body.source.trim()) throw Error('无效的文稿或源码。');
   return respond(res,200,await compile(body.documentId,body.source));
  }
  if (req.method === 'POST' && url.pathname === '/api/locate') {
   const body = await readBody(req);
   if (!validId(body.documentId) || !validRevision(body.revision) || !Number.isSafeInteger(body.line) || body.line < 1 || !Number.isSafeInteger(body.column || 1) || (body.column || 1) < 1) throw Error('无效的源码位置。');
   return respond(res,200,await locate(body.documentId,body.revision,body.line,body.column || 1));
  }
  const pdf = /^\/api\/pdf\/(session|project)\/([a-f0-9]{64})$/.exec(url.pathname);
  if (req.method === 'GET' && pdf) { const bytes = await fs.readFile(path.join(jobDir(pdf[1],pdf[2]),'main.pdf')); res.writeHead(200,{'Content-Type':'application/pdf','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}); return res.end(bytes); }
  const page = /^\/api\/page\/(session|project)\/([a-f0-9]{64})\/(\d+)\.png$/.exec(url.pathname);
  if (req.method === 'GET' && page) {
   const number = Number(page[3]); if (!Number.isSafeInteger(number) || number < 1 || number > 10000) throw Error('无效的 PDF 页码。');
   const dir = jobDir(page[1],page[2]), output = path.join(dir, 'page-' + number);
   try { await fs.access(output + '.png'); } catch { await run(pdftoppm, ['-f',String(number),'-l',String(number),'-singlefile','-png','-r','120',path.join(dir,'main.pdf'),output], {cwd:dir,timeout:30000,maxBuffer:1024*1024,windowsHide:true}); }
   const bytes = await fs.readFile(output + '.png'); res.writeHead(200,{'Content-Type':'image/png','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}); return res.end(bytes);
  }
  if (req.method !== 'GET') return respond(res,405,{ok:false,error:'不支持此方法。'});
  const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  if (!/^[a-zA-Z0-9._-]+$/.test(name)) return respond(res,404,{ok:false,error:'文件不存在。'});
  const ext = path.extname(name); const mime = {'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8'}[ext];
  if (!mime) return respond(res,404,{ok:false,error:'文件不存在。'});
  const bytes = await fs.readFile(path.join(staticRoot,name)); res.writeHead(200,{'Content-Type':mime,'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}); res.end(bytes);
 } catch (error) { respond(res,422,{ok:false,error:String(error.message || error)}); }
});
server.listen(Number(process.env.TEXEED_PDF_PROTOTYPE_PORT || 4179),'127.0.0.1',() => console.log(`PDF 评论原型：http://127.0.0.1:${server.address().port}/`));
