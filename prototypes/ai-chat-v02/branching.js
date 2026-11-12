(() => {
  'use strict';
  const KEY = 'texeed-branch-reading-prototype-v1';
  const draftKey = id => KEY + '/draft/' + id, viewportKey = id => KEY + '/viewport/' + id, referencesKey = id => KEY + '/references/' + id;
  const $ = id => document.getElementById(id);
  const reader = $('reader'), transcript = $('transcript'), composer = $('composer');
  const uid = () => (globalThis.crypto && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2));
  const block = (kind, text) => ({ id: uid(), kind, text });
  const longMath = Array.from({ length: 16 }, (_, i) => `A_{${i + 1}}`).join('+');
  const formulaAssets = { '\\mathbb{Z}_q = \\mathbb{Z}/q\\mathbb{Z}': 'assets/branch-math-1.svg', 'R_q = \\mathbb{Z}_q[x]/(x^n+1)': 'assets/branch-math-2.svg', '7 \\equiv 2 \\pmod{5}': 'assets/branch-math-3.svg', [longMath]: 'assets/branch-math-4.svg' };
  const demoAssetBlocks = () => [block('paragraph', '下面是一张用于演示右键引用的图片：'), { id: uid(), kind: 'image', text: '交互示例图片', alt: '示例图：图表比较', src: 'assets/flight-comparison.svg' }, block('paragraph', '下面是带编号的超宽公式：'), { id: uid(), kind: 'math', text: longMath, number: '(3)', large: true, svg: 'assets/branch-math-4.svg' }, block('paragraph', '接下来是没有编号的超宽公式。'), { id: uid(), kind: 'math', text: longMath, large: true, svg: 'assets/branch-math-4.svg' }, block('paragraph', '这段文字位于无编号公式之后。')];
  const rootBlocks = () => [
    block('heading', '先建立一个整体图景'),
    block('paragraph', '理解 Saber 与 OT 时，先把协议目标和代数计算分开：OT 描述参与者应当获得什么信息；底层代数结构提供可用于构造和分析的运算对象。这里的内容仅为阅读交互示例，不是对 Saber OT 的完整讲解。'),
    block('paragraph', '学习时可以先问三个问题：元素是什么，允许做什么运算，以及哪些信息在计算之后被保留。每个问题都可以单独追问，不必在还没有理解前面的定义时继续读完整篇。'),
    block('heading', '从整数模运算开始'),
    block('paragraph', '先看一个较简单的模型。整数按模数 q 计算时，相差 q 的倍数被视为同一个余数类。加法与乘法都在这些余数类之间进行。例如，模 5 时 4 + 3 与 2 属于同一个余数类。'),
    block('math', '\\mathbb{Z}_q = \\mathbb{Z}/q\\mathbb{Z}'),
    block('paragraph', '这一记号表达的是：把相差 q 的倍数的整数放在同一类中。关于符号、余数类和运算是否良定义，你都可以在此选中文字并发起追问。'),
    block('heading', '从数扩展到多项式'),
    block('paragraph', '接下来可以考虑系数按模 q 运算的多项式。多项式同样可以相加和相乘；如果还规定按照一个多项式取模，就可以把不同的多项式代表视为同一个元素。'),
    block('math', 'R_q = \\mathbb{Z}_q[x]/(x^n+1)'),
    block('paragraph', '这个式子是一个用于说明交互的示例：它把系数的模运算与多项式的模运算写在一起。不要把这里的参数当作对具体协议实现的断言。你可以追问“为什么能这样写”，也可以先问分母符号的意义。'),
    block('paragraph', '在这类结构中，实际计算不仅要看代数式是否有意义，还要看元素如何编码、随机量如何采样，以及计算结果在协议中承担什么角色。这几个问题相关，但不是同一个问题。'),
    block('heading', '区分结构与安全性'),
    block('paragraph', '即使我们理解了运算所在的代数结构，也不能仅凭这个结构断定一个协议安全。还需要检查假设、参与方模型、随机性以及协议证明。一个正确的定义与一个完整的安全结论之间，仍然有许多需要核对的步骤。'),
    block('paragraph', '阅读时遇到不清楚的地方，可以选中句子或公式并选择“从这里追问”；也可以只引用选区，继续留在当前会话。实际决定权在你，图中记录的是你实际创建的会话关系。'),
    block('heading', '回到原文继续阅读'),
    block('paragraph', '完成一个问题的讨论后，你可以返回提问出处。你也可以主动给文中任何选区或段落添加书签，留下自己希望回看的位置。'),
    block('paragraph', '继续阅读之后，如果在另一处遇到疑问，可以再创建一条追问会话。会话关系图会显示它们来自同一篇回答还是另一条追问，并允许你重新打开已经讨论过的问题。'), ...demoAssetBlocks()
  ];
  const initial = () => ({ version: 1, active: 'root', selected: 'root', pending: null, bookmarks: [], sessions: [{ id: 'root', title: 'Saber OT 的代数结构', parent: null, source: null, draft: '', turns: [{ id: 'root-turn', question: '请给我讲讲 Saber OT 底层的代数结构。', answer: rootBlocks(), demo: true }] }] });
  const readState = () => { try { const value = JSON.parse(localStorage.getItem(KEY) || 'null'); const loaded = value && value.version === 1 && Array.isArray(value.sessions) && value.sessions.length ? value : initial(); loaded.pendingBySession ||= {}; loaded.bookmarks ||= []; if (loaded.pending?.parentId && !loaded.pendingBySession[loaded.pending.parentId]) loaded.pendingBySession[loaded.pending.parentId] = loaded.pending; for (const s of loaded.sessions) { s.references ||= []; s.attachments ||= []; const references = JSON.parse(localStorage.getItem(referencesKey(s.id)) || 'null'); if (Array.isArray(references)) s.references = references; const viewport = localStorage.getItem(viewportKey(s.id)); if (viewport !== null && Number.isFinite(Number(viewport))) s.viewport = Number(viewport); const draft = JSON.parse(localStorage.getItem(draftKey(s.id)) || 'null'); if (draft) { s.draft = draft.draft; if (draft.attachments) s.attachments = draft.attachments; if (draft.pending) loaded.pendingBySession[s.id] = draft.pending; else delete loaded.pendingBySession[s.id]; } const old = loaded.pendingBySession[s.id]; if (old?.kind === 'quote') { if (!s.references.some(r => r.legacyPending)) s.references.push({ id: uid(), blockId: old.blockId, quote: old.quote, textOffset: old.sourceTextOffset, comment: '', selected: true, attachments: [], legacyPending: old }); delete loaded.pendingBySession[s.id]; } } loaded.pending = loaded.pendingBySession[loaded.active] || null; return loaded; } catch { return initial(); } };
  let state = readState(), currentSelection = null, focusedBlock = null, bookmarkCandidate = null, editingReference = null, mapOpen = false, relationAnchor = null, pendingFileReads = 0;
  state.pausedFollowups ||= {};
  for (const [id, items] of Object.entries(state.followups || {})) if (items.length) state.pausedFollowups[id] = true;
  const running = new Set();
  const session = id => state.sessions.find(s => s.id === id);
  const current = () => session(state.active) || state.sessions[0];
  function isBlankSession(s) { return s.title === '新对话' && !s.parent && !s.source && !(s.turns || []).length && !s.draft && !(s.references || []).length && !(s.attachments || []).length && !state.pendingBySession?.[s.id] && !(state.followups?.[s.id] || []).length && !(state.bookmarks || []).some(b => b.sessionId === s.id) && !state.sessions.some(child => child.parent === s.id) && Object.keys(s).every(k => ['id','title','parent','source','draft','turns','references','attachments','viewport'].includes(k)); }
  function trimBlankSessions() {
    const blanks = state.sessions.filter(isBlankSession); if (blanks.length < 2) return;
    const keep = blanks.find(s => s.id === state.active) || blanks[0];
    state.sessions = state.sessions.filter(s => !isBlankSession(s) || s === keep);
    if (!session(state.selected)) state.selected = state.active;
  }
  trimBlankSessions();
  function ensureSession(s) { s.references ||= []; s.attachments ||= []; return s; }
  function save() { try { state.pendingBySession ||= {}; if (state.pending?.parentId === state.active) state.pendingBySession[state.active] = state.pending; else delete state.pendingBySession[state.active]; localStorage.setItem(KEY, JSON.stringify(state)); localStorage.removeItem(draftKey(state.active)); localStorage.removeItem(referencesKey(state.active)); return true; } catch { $('status').textContent = '当前浏览器无法保存示例会话。'; return false; } }
  function saveDraft() { try { localStorage.setItem(draftKey(state.active), JSON.stringify({ draft: current().draft, attachments: ensureSession(current()).attachments, pending: state.pending?.parentId === state.active ? state.pending : null })); return true; } catch { $('status').textContent = '当前浏览器无法保存示例草稿。'; return false; } }
  function saveReferences() { try { localStorage.setItem(referencesKey(state.active), JSON.stringify(ensureSession(current()).references)); return true; } catch { $('status').textContent = '当前浏览器无法保存引用。'; return false; } }
  let pendingAnchor = null;
  function cancelPendingAnchor() {
    if (!pendingAnchor) return;
    const pending = pendingAnchor; pendingAnchor = null;
    cancelAnimationFrame(pending.frame);
    pending.cleanup();
  }
  function jumpToAnchor(pos) {
    cancelPendingAnchor();
    const pending = { frame: 0, cleanup: () => {} };
    pendingAnchor = pending;
    if (pos?.center) {
      const cancel = () => cancelPendingAnchor();
      const key = event => {
        if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', ' '].includes(event.key)) return;
        const target = event.target;
        if (target instanceof Element && target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]')) return;
        cancel();
      };
      const hidden = () => { if (document.hidden) cancel(); };
      reader.addEventListener('wheel', cancel, { passive: true });
      reader.addEventListener('pointerdown', cancel, { passive: true });
      reader.addEventListener('touchstart', cancel, { passive: true });
      document.addEventListener('keydown', key, true);
      document.addEventListener('visibilitychange', hidden);
      window.addEventListener('pagehide', cancel);
      pending.cleanup = () => {
        reader.removeEventListener('wheel', cancel);
        reader.removeEventListener('pointerdown', cancel);
        reader.removeEventListener('touchstart', cancel);
        document.removeEventListener('keydown', key, true);
        document.removeEventListener('visibilitychange', hidden);
        window.removeEventListener('pagehide', cancel);
      };
    }
    pending.frame = requestAnimationFrame(() => {
      if (pendingAnchor !== pending) return;
      pendingAnchor = null; pending.cleanup();
      const el = pos && pos.blockId ? transcript.querySelector(`[data-block-id="${CSS.escape(pos.blockId)}"]`) : null;
      if (pos?.successMessage) $('status').textContent = el ? pos.successMessage : '无法定位提问出处。';
      let anchorRect = null;
      if (el && Number.isInteger(pos.textOffset)) {
        const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, { acceptNode: node => node.parentElement?.closest('.associated') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT }); let node, left = pos.textOffset, target = null;
        while ((node = walker.nextNode())) { if (left <= node.length) { target = node; break; } left -= node.length; }
        if (target) { const range = document.createRange(); range.setStart(target, Math.min(left, target.length)); range.collapse(true); anchorRect = range.getBoundingClientRect(); }
      }
      if (el && pos.center) {
        const rect = anchorRect || el.getBoundingClientRect();
        const anchorY = rect.top + Math.min(rect.height, 24) / 2;
        reader.scrollTop += anchorY - reader.getBoundingClientRect().top - reader.clientHeight / 2;
      }
      else if (el && anchorRect) reader.scrollTop += anchorRect.top - reader.getBoundingClientRect().top - (pos.lineY || 0);
      else if (el) reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().top + (pos.offset || 0);
      if (el) { el.classList.add('source-flash'); setTimeout(() => el.classList.remove('source-flash'), 3000); }
    });
  }
  function persistCurrent() {
    if (state.pending && state.pending.kind === 'branch' && state.pending.parentId === state.active) state.pending.draft = composer.value;
    else current().draft = composer.value;
    save();
  }
  function appendText(tag, cls, text, parent) { const el = document.createElement(tag); if (cls) el.className = cls; el.textContent = text; parent.appendChild(el); return el; }
  function quoteElement(parent, label, text) { const quote = document.createElement('div'); quote.className = 'quoted'; const prefix = document.createElement('span'); prefix.textContent = label + '：'; quote.append(prefix, document.createTextNode(text)); parent.appendChild(quote); }
  function renderReferencePreview(parent, ref) {
    const box = document.createElement('div'); box.className = 'reference-preview';
    if (ref.kind === 'math' && ref.svg && !ref.large) {
      const img = document.createElement('img'); img.src = ref.svg; img.className = 'formula-svg'; img.alt = ref.quote || '公式';
      const fallback = document.createElement('div'); fallback.className = 'reference-caption'; fallback.textContent = ref.quote || '公式'; fallback.hidden = true;
      const useFallback = () => { img.hidden = true; fallback.hidden = false; };
      img.addEventListener('load', () => { if (img.naturalWidth > 420 || img.naturalHeight > 55) useFallback(); });
      img.addEventListener('error', useFallback); box.append(img, fallback); parent.appendChild(box); return box;
    }
    appendText('div', ref.kind === 'math' ? 'reference-caption' : '', ref.quote || '所选内容', box); parent.appendChild(box); return box;
  }
  function attachmentGroups(attachments) {
    const groups = [], directories = new Map();
    for (const file of attachments || []) {
      const path = (file.path || file.name || '').replaceAll('\\', '/');
      if (file.referenceKind === 'file') { groups.push({ files: [file], label: path, directory: false }); continue; }
      const directory = file.directory || (path.includes('/') ? path.split('/')[0] + '/' : '');
      if (!directory) { groups.push({ files: [file], label: path, directory: false }); continue; }
      const key = file.attachmentGroup || 'legacy:' + directory;
      let group = directories.get(key);
      if (!group) { group = { files: [], label: directory.replace(/[\\/]+$/, '') + '/', directory: true }; directories.set(key, group); groups.push(group); }
      group.files.push(file);
    }
    return groups;
  }
  function renderAttachments(parent, attachments, scope, ownerId) { for (const group of attachmentGroups(attachments)) { const file = group.files[0], chip = document.createElement('span'); chip.className = 'attachment-chip'; const image = !group.directory && file.type?.startsWith('image/') && file.data; const icon = parent.id === 'attachment-list' ? document.createElement('div') : chip; if (icon !== chip) { icon.className = 'attachment-icon'; chip.appendChild(icon); } if (image) { const img = document.createElement('img'); img.src = file.data; img.alt = file.name; icon.appendChild(img); } if (group.directory) { const label = document.createElement('span'); label.className = 'folder-path'; label.title = group.label; const path = group.label.slice(0, -1); const separator = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')); const leaf = path.slice(separator + 1); const points = Array.from(leaf); const tail = separator >= 0 && points.length <= 12 ? path.slice(separator) : points.slice(-12).join(''); appendText('span', 'folder-path-stem', path.slice(0, path.length - tail.length), label); appendText('span', 'folder-path-tail', tail, label); appendText('span', 'folder-path-slash', '/', label); chip.appendChild(label); } else appendText('span', '', group.label, chip); if (scope) { const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = '×'; remove.dataset.attachmentRemove = file.id; remove.dataset.attachmentScope = scope; if (ownerId) remove.dataset.referenceId = ownerId; remove.setAttribute('aria-label', '移除附件 ' + group.label); chip.appendChild(remove); } parent.appendChild(chip); } }
  function textWithoutLinks(node) {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue || '';
    if (node.nodeType !== Node.ELEMENT_NODE && node.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return '';
    if (node.nodeType === Node.ELEMENT_NODE && node.matches('.associated, .unresolved-associated')) return '';
    const children = Array.from(node.childNodes), parts = [];
    for (const child of children) {
      const text = textWithoutLinks(child); if (!text) continue;
      if (parts.length && child.nodeType === Node.ELEMENT_NODE && child.matches('.passage')) parts.push('\n');
      parts.push(text);
    }
    return parts.join('');
  }
  function occurrenceOffsets(text, fragment) {
    const matches = []; if (!fragment) return matches;
    for (let at = 0; (at = text.indexOf(fragment, at)) !== -1; at++) matches.push(at);
    return matches;
  }
  function legacyLinkOffset(item, source, markerLength) {
    const text = item.text || '', quote = source.quote;
    if (!quote || quote.length >= 2000) return null;
    if (source.blockId === item.id) {
      const start = Number.isInteger(source.textOffset) ? Math.max(0, source.textOffset - markerLength) : null;
      if (start !== null) { const suffix = text.slice(start), leading = suffix.length - suffix.trimStart().length; if (suffix.slice(leading).startsWith(quote)) return start + leading + quote.length; }
      const matches = occurrenceOffsets(text, quote); return matches.length === 1 ? matches[0] + quote.length : null;
    }
    const pieces = quote.split(/\r?\n+/).map(part => part.trim()).filter(Boolean);
    if (pieces.length < 2) return null;
    const turn = current().turns.find(t => t.answer.some(block => block.id === source.blockId) && t.answer.some(block => block.id === item.id));
    if (!turn) return null;
    const firstIndex = turn.answer.findIndex(block => block.id === source.blockId), lastIndex = turn.answer.findIndex(block => block.id === item.id);
    if (lastIndex <= firstIndex) return null;
    const between = turn.answer.slice(firstIndex + 1, lastIndex), first = turn.answer[firstIndex];
    if (first.kind !== 'paragraph' || between.some(block => block.kind !== 'paragraph') || pieces.length !== between.length + 2) return null;
    if (between.some((block, i) => (block.text || '').trim() !== pieces[i + 1])) return null;
    const firstText = first.text || '', start = Number.isInteger(source.textOffset) ? Math.max(0, source.textOffset) : null;
    if (start !== null) { if (firstText.slice(start).trim() !== pieces[0]) return null; }
    else if (occurrenceOffsets(firstText, pieces[0]).filter(at => firstText.slice(at).trim() === pieces[0]).length !== 1) return null;
    const last = pieces.at(-1), leading = text.length - text.trimStart().length;
    return text.slice(leading).startsWith(last) ? leading + last.length : null;
  }
  function branchLink(child) { const link = document.createElement('button'); link.type = 'button'; link.className = 'associated'; link.textContent = '🗨️ ' + child.title; link.title = child.title; link.dataset.openSession = child.id; return link; }
  function renderBlock(parent, item, number, heading) {
    let el, markerLength = 0;
    if (item.kind === 'heading') { el = document.createElement('div'); el.className = 'passage passage-heading'; el.setAttribute('role', 'button'); el.setAttribute('tabindex', '0'); el.dataset.headingId = item.id; const mark = document.createElement('span'); mark.className = 'heading-number'; mark.textContent = `[${number}:${heading}]`; markerLength = mark.textContent.length; el.append(mark); }
    else if (item.kind === 'math') { el = document.createElement('div'); el.className = 'passage formula'; try { katex.render(item.text, el, { displayMode: true, throwOnError: false, output: 'html' }); } catch { el.textContent = item.text; } if (item.number) appendText('span', 'formula-number', item.number, el); }
    else if (item.kind === 'image') { el = document.createElement('div'); el.className = 'passage'; const img = document.createElement('img'); img.src = item.src; img.alt = item.alt || ''; img.className = 'sample-image'; el.appendChild(img); }
    else { el = document.createElement('p'); el.className = 'passage'; }
    const related = state.sessions.filter(s => s.parent === current().id && s.source && (s.source.endBlockId || s.source.blockId) === item.id);
    if (item.kind !== 'math' && item.kind !== 'image') {
      const placements = [], unresolved = [];
      for (const child of related) {
        const saved = child.source.endTextOffset;
        const offset = Number.isInteger(saved) ? saved - markerLength : legacyLinkOffset(item, child.source, markerLength);
        if (offset === null || offset < 0 || offset > item.text.length) unresolved.push(child);
        else placements.push({ child, offset });
      }
      placements.sort((a, b) => a.offset - b.offset);
      let cursor = 0;
      for (const placement of placements) { el.appendChild(document.createTextNode(item.text.slice(cursor, placement.offset))); el.appendChild(branchLink(placement.child)); cursor = placement.offset; }
      el.appendChild(document.createTextNode(item.text.slice(cursor)));
      el.dataset.blockId = item.id; parent.appendChild(el);
      if (unresolved.length) { const box = document.createElement('div'); box.className = 'unresolved-associated'; box.dataset.linkFor = item.id; appendText('span', '', '来源位置待确认：', box); for (const child of unresolved) box.appendChild(branchLink(child)); parent.appendChild(box); }
      return;
    }
    for (const child of related) el.appendChild(branchLink(child));
    el.dataset.blockId = item.id; parent.appendChild(el);
  }
  function headingLevel(item) { return Number.isInteger(item.level) && item.level > 0 ? item.level : 1; }
  function syncFoldVisibility(wrap, turn) {
    const elements = new Map(Array.from(wrap.querySelectorAll('[data-block-id]'), el => [el.dataset.blockId, el]));
    const ancestors = [];
    for (const item of turn.answer) {
      if (item.kind === 'heading') { const level = headingLevel(item); while (ancestors.length && ancestors.at(-1).level >= level) ancestors.pop(); }
      const hidden = ancestors.some(ancestor => ancestor.collapsed), el = elements.get(item.id);
      if (el) {
        el.hidden = hidden;
        const links = el.nextElementSibling; if (links?.dataset.linkFor === item.id) links.hidden = hidden;
        if (item.kind === 'heading') el.setAttribute('aria-expanded', String(!turn.collapsedSections?.[item.id]));
      }
      if (item.kind === 'heading') ancestors.push({ level: headingLevel(item), collapsed: Boolean(turn.collapsedSections?.[item.id]) });
    }
  }
  function renderTurn(turn, i) {
      const wrap = document.createElement('section'); wrap.className = 'turn';
      const q = appendText('div', 'turn-question', turn.question, wrap); const num = document.createElement('span'); num.className = 'turn-number'; num.textContent = `[${i + 1}]`; q.prepend(num);
      if (i === 0 && current().parent && current().source?.blockId && session(current().parent)) {
        const origin = document.createElement('button'); origin.type = 'button'; origin.className = 'sent-origin'; origin.dataset.sourceSession = current().id;
        const sourceText = (current().source.quote || '提问出处').replace(/\s+/g, ' ').trim();
        origin.textContent = '引用：' + Array.from(sourceText).slice(0, 80).join(''); origin.title = sourceText; wrap.appendChild(origin);
      }
      appendText('div', 'time', '示例时间　发送', wrap);
      if (turn.quote && !(i === 0 && current().parent && turn.quote === current().source?.quote)) quoteElement(wrap, '引用', turn.quote);
      for (const ref of turn.references || []) { const matches = state.sessions.filter(s => s.turns.some(t => t.answer.some(b => b.id === ref.blockId))); const origin = ref.sessionId ? session(ref.sessionId) : matches.length === 1 ? matches[0] : null; const target = origin && matches.some(s => s.id === origin.id) ? document.createElement('button') : document.createElement('div'); if (origin && target.tagName === 'BUTTON') { target.type = 'button'; target.className = 'sent-reference'; target.dataset.referenceSession = origin.id; target.dataset.referenceBlock = ref.blockId; if (Number.isInteger(ref.textOffset)) target.dataset.referenceOffset = ref.textOffset; } renderReferencePreview(target, ref); wrap.appendChild(target); if (ref.comment) appendText('p', 'passage', ref.comment, wrap); if (ref.attachments?.length) { const media = document.createElement('div'); media.className = 'attachment-list'; renderAttachments(media, ref.attachments); wrap.appendChild(media); } }
      if (turn.attachments?.length) { const media = document.createElement('div'); media.className = 'attachment-list'; renderAttachments(media, turn.attachments); wrap.appendChild(media); }
      const rule = document.createElement('hr'); rule.className = 'answer-rule'; wrap.appendChild(rule);
      if (turn.demo) appendText('div', 'example-label', '示例答复 · 未连接 AI', wrap);
      let heading = 0; const ancestors = [];
      for (const item of turn.answer) {
        if (item.kind === 'heading') { heading++; const level = headingLevel(item); while (ancestors.length && ancestors.at(-1).level >= level) ancestors.pop(); }
        const before = wrap.children.length, hidden = ancestors.some(ancestor => ancestor.collapsed);
        renderBlock(wrap, item, i + 1, heading);
        const el = wrap.children[before], links = wrap.children[before + 1];
        if (el) { el.hidden = hidden; if (item.kind === 'heading') el.setAttribute('aria-expanded', String(!turn.collapsedSections?.[item.id])); }
        if (links?.dataset.linkFor === item.id) links.hidden = hidden;
        if (item.kind === 'heading') ancestors.push({ level: headingLevel(item), collapsed: Boolean(turn.collapsedSections?.[item.id]) });
      }
      for (const steer of turn.steers || []) {
        const activity = document.createElement('div'); activity.className = 'example-steer';
        appendText('div', '', '引导：' + (steer.question || '引用与附件'), activity);
        for (const ref of steer.references || []) { renderReferencePreview(activity, ref); if (ref.comment) appendText('div', '', ref.comment, activity); if (ref.attachments?.length) { const media = document.createElement('div'); media.className = 'attachment-list'; renderAttachments(media, ref.attachments); activity.appendChild(media); } }
        if (steer.attachments?.length) { const files = document.createElement('div'); files.className = 'attachment-list'; renderAttachments(files, steer.attachments); activity.appendChild(files); }
        wrap.appendChild(activity);
      }
      const end = document.createElement('hr'); end.className = 'answer-rule'; wrap.appendChild(end);
      appendText('div', 'time', '示例时间　答复', wrap); return wrap;
  }
  function renderTranscript() {
    transcript.replaceChildren();
    const s = current();
    s.turns.forEach((turn, i) => transcript.appendChild(renderTurn(turn, i)));
    $('session-title').textContent = s.title;
    $('back-source').disabled = !s.parent;
    $('child-sessions').disabled = !state.sessions.some(child => child.parent === s.id);
    $('child-menu').hidden = true;
    focusedBlock = null; $('bookmark-paragraph').disabled = true;
    renderComposerContext(); renderReferencesButton(); renderMainAttachments(); renderFollowups();
  }
  function renderComposerContext() {
    const box = $('composer-context'); box.replaceChildren();
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    box.hidden = !pending;
    if (pending) { const text = document.createElement('div'); text.className = 'context-text'; const strong = document.createElement('strong'); strong.textContent = pending.kind === 'branch' ? '新开追问型会话' : '引用到当前会话'; text.append(strong, document.createTextNode('　' + pending.quote)); if (pending.sourceKind === 'math') renderReferencePreview(text, { kind: 'math', quote: pending.quote, svg: pending.svg, large: pending.large }); box.appendChild(text); const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = '取消'; cancel.id = 'cancel-context'; box.appendChild(cancel); }
    composer.placeholder = pending && pending.kind === 'branch' ? '写下追问；发送后创建追问会话……' : '继续当前会话……';
  }
  function setComposer() {
    $('mention-menu').hidden = true;
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    composer.value = pending && pending.kind === 'branch' ? (pending.draft || '') : (current().draft || '');
    renderComposerContext(); renderReferencesButton(); renderMainAttachments(); renderFollowups();
  }
  function renderFollowups() {
    const active = running.has(current().id), blocked = state.pending?.kind === 'branch';
    $('steer').disabled = $('enqueue').disabled = !active || blocked;
    $('send').disabled = active || pendingFileReads > 0;
    $('demo-running-status').hidden = !active;
    $('demo-running').textContent = active ? '结束当前模拟轮' : '模拟运行';
    $('demo-running').setAttribute('aria-pressed', String(active));
    const box = $('followup-queue'), items = state.followups?.[current().id] || [];
    box.replaceChildren(); box.hidden = !items.length;
    if (items.length) {
      const header = document.createElement('div'); header.className = 'followup-header';
      appendText('span', '', state.pausedFollowups?.[current().id] ? '待处理消息 · 已暂停' : '待处理消息', header);
      const pause = document.createElement('button'); pause.type = 'button'; pause.textContent = state.pausedFollowups?.[current().id] ? '继续' : '暂停';
      pause.addEventListener('click', () => { state.pausedFollowups ||= {}; const id = current().id, old = !!state.pausedFollowups[id]; state.pausedFollowups[id] = !old; if (!save()) { state.pausedFollowups[id] = old; return; } if (old && !running.has(id)) { running.add(id); dispatchFollowup(); } renderFollowups(); });
      header.appendChild(pause); box.appendChild(header);
    }
    for (const item of items) {
      const row = document.createElement('div'); row.className = 'followup-row';
      const text = document.createElement('input'); text.value = item.question; text.setAttribute('aria-label', '编辑排队消息');
      text.addEventListener('change', () => { const before = item.question; item.question = text.value; if (!save()) { item.question = before; text.value = before; } });
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = '×'; remove.setAttribute('aria-label', '移除排队消息');
      remove.addEventListener('click', () => { const index = items.indexOf(item); items.splice(index, 1); if (!save()) { items.splice(index, 0, item); return; } renderFollowups(); });
      const move = (offset) => { const index = items.indexOf(item), next = index + offset; if (next < 0 || next >= items.length) return; [items[index], items[next]] = [items[next], items[index]]; if (!save()) { [items[index], items[next]] = [items[next], items[index]]; return; } renderFollowups(); };
      const up = document.createElement('button'); up.type = 'button'; up.textContent = '↑'; up.setAttribute('aria-label', '上移排队消息'); up.disabled = items.indexOf(item) === 0; up.addEventListener('click', () => move(-1));
      const down = document.createElement('button'); down.type = 'button'; down.textContent = '↓'; down.setAttribute('aria-label', '下移排队消息'); down.disabled = items.indexOf(item) === items.length - 1; down.addEventListener('click', () => move(1));
      const convert = document.createElement('button'); convert.type = 'button'; convert.textContent = '转为引导'; convert.disabled = !active; convert.addEventListener('click', () => { const index = items.indexOf(item), turn = current().turns.at(-1); if (!turn) return; turn.steers ||= []; turn.steers.push(item); items.splice(index, 1); if (!save()) { items.splice(index, 0, item); turn.steers.pop(); return; } renderTranscript(); $('status').textContent = '示例消息已转为引导，未连接 AI。'; });
      row.append(text, convert, up, down, remove); box.appendChild(row);
    }
  }
  function submitFollowup(queued) {
    if (!running.has(current().id) || state.pending?.kind === 'branch' || pendingFileReads) return;
    const refs = ensureSession(current()).references, selected = refs.filter(ref => ref.selected !== false), files = mainAttachments();
    const question = composer.value.trim(); if (!question && !selected.length && !files.length) { composer.focus(); return; }
    if (!queued && !current().turns.length) { $('status').textContent = '没有可引导的模拟轮次。'; return; }
    const before = JSON.stringify(state), item = { id: uid(), question, references: structuredClone(selected), attachments: structuredClone(files) };
    current().references = refs.filter(ref => ref.selected === false); current().attachments = []; current().draft = '';
    if (queued) { state.followups ||= {}; (state.followups[current().id] ||= []).push(item); }
    else { const turn = current().turns.at(-1); if (!turn) { $('status').textContent = '没有可引导的模拟轮次。'; return; } (turn.steers ||= []).push(item); }
    if (!save()) { state = JSON.parse(before); $('status').textContent = '无法保存，输入内容已保留。'; return; }
    composer.value = ''; if (!queued) renderTranscript(); setComposer();
    $('status').textContent = queued ? '已加入示例队列；可修改或移除。' : '已发送示例引导；未连接 AI。';
  }
  $('steer').addEventListener('click', () => submitFollowup(false));
  $('enqueue').addEventListener('click', () => submitFollowup(true));
  function dispatchFollowup() {
    const id = current().id, items = state.followups?.[id] || [];
    if (state.pausedFollowups?.[id] || !items.length) { running.delete(id); return false; }
    const item = items.shift(); current().turns.push({ ...item, answer: demoAnswer(item.question, ''), demo: true });
    if (!save()) { current().turns.pop(); items.unshift(item); running.delete(id); return false; }
    running.add(id); renderTranscript(); $('status').textContent = '示例队列已发送一条；未连接 AI。'; return true;
  }
  $('demo-stop').addEventListener('click', () => { const id = current().id; running.delete(id); state.pausedFollowups ||= {}; state.pausedFollowups[id] = true; if (save()) { $('status').textContent = '模拟已中止；待处理消息已暂停。'; renderFollowups(); } });
  $('demo-running').addEventListener('click', () => {
    const id = current().id;
    if (!running.has(id)) { running.add(id); $('status').textContent = ''; }
    else {
      if (!dispatchFollowup()) $('status').textContent = state.pausedFollowups?.[id] ? '模拟运行已结束；队列仍处于暂停状态。' : '模拟运行已结束。';
    }
    renderFollowups();
  });
  function navigate(id, position) {
    if (!session(id)) return;
    cancelPendingAnchor();
    if (id === state.active && !position) { closeMap(); return; }
    persistCurrent(); state.active = id; state.selected = id; state.pending = state.pendingBySession?.[id] || null; currentSelection = null;
    const viewport = current().viewport || 0;
    $('selection-tools').hidden = true; closeMap(); renderTranscript(); setComposer();
    if (position) jumpToAnchor(position); else requestAnimationFrame(() => { reader.scrollTop = viewport; }); save();
  }
  function unfoldFor(target, blockId) {
    if (!target || !blockId) return;
    for (const turn of target.turns) {
      const ancestors = [];
      for (const item of turn.answer) {
        if (item.kind === 'heading') { const level = headingLevel(item); while (ancestors.length && ancestors.at(-1).level >= level) ancestors.pop(); }
        if (item.id === blockId) { if (turn.collapsedSections) for (const heading of ancestors) turn.collapsedSections[heading.id] = false; return; }
        if (item.kind === 'heading') ancestors.push(item);
      }
    }
  }
  function parentSource(target) {
    if (!target || !target.parent) return;
    const source = target.source || {};
    unfoldFor(session(target.parent), source.blockId);
    $('status').textContent = '';
    navigate(target.parent, { blockId: source.blockId, textOffset: source.textOffset, center: true, successMessage: '已定位提问出处。' });
  }
  function blockContext(el) {
    if (!el?.dataset.blockId) return null;
    const id = el.dataset.blockId; let item, before = '', after = '';
    for (const turn of current().turns) { const idx = turn.answer.findIndex(b => b.id === id); if (idx < 0) continue; item = turn.answer[idx]; before = [...turn.answer.slice(0, idx)].reverse().find(b => b.kind === 'paragraph')?.text || ''; after = turn.answer.slice(idx + 1).find(b => b.kind === 'paragraph')?.text || ''; break; }
    if (!item) return null;
    if (item.kind === 'math') return { blockId: id, kind: 'math', quote: item.number ? '公式 ' + item.number : '公式附近：' + [before.slice(-70), after.slice(0, 70)].filter(Boolean).join(' … '), svg: item.svg || formulaAssets[item.text], math: item.text, number: item.number, large: item.large };
    if (item.kind === 'image') return { blockId: id, kind: 'image', quote: item.alt || [before.slice(-80), after.slice(0, 80)].filter(Boolean).join(' … ') || '图片', src: item.src };
    return { blockId: id, kind: 'text', quote: item.text };
  }
  function selectionInfo() {
    const sel = window.getSelection(); if (!sel || sel.isCollapsed || !sel.rangeCount) return null;
    const range = sel.getRangeAt(0); if (!transcript.contains(range.commonAncestorContainer)) return null;
    const quote = textWithoutLinks(range.cloneContents()).trim(); if (!quote) return null;
    const element = range.startContainer.nodeType === Node.ELEMENT_NODE ? range.startContainer : range.startContainer.parentElement;
    const source = element && element.closest('[data-block-id]'); if (!source) return null;
    const context = blockContext(source); if (!context) return null; if (context.kind === 'math' || context.kind === 'image') return { ...context, rect: range.getBoundingClientRect() };
    let textOffset;
    if (!source.classList.contains('formula') && source.contains(range.startContainer)) { const prefix = document.createRange(); prefix.selectNodeContents(source); prefix.setEnd(range.startContainer, range.startOffset); textOffset = textWithoutLinks(prefix.cloneContents()).length; }
    const endElement = range.endContainer.nodeType === Node.ELEMENT_NODE ? range.endContainer : range.endContainer.parentElement;
    const endBlock = endElement?.closest('[data-block-id]');
    let endTextOffset;
    if (endBlock && !endBlock.classList.contains('formula') && endBlock.contains(range.endContainer)) { const prefix = document.createRange(); prefix.selectNodeContents(endBlock); prefix.setEnd(range.endContainer, range.endOffset); endTextOffset = textWithoutLinks(prefix.cloneContents()).length; }
    return { ...context, quote: quote.slice(0, 2000), textOffset, endBlockId: endBlock?.dataset.blockId, endTextOffset, rect: range.getBoundingClientRect() };
  }
  function showSelection() {
    const info = selectionInfo(); if (!info) { $('selection-tools').hidden = true; return; }
    currentSelection = info; const tools = $('selection-tools'); tools.hidden = false;
    requestAnimationFrame(() => { const width = tools.offsetWidth, height = tools.offsetHeight; tools.style.left = Math.max(6, Math.min(innerWidth - width - 6, info.rect.left)) + 'px'; tools.style.top = Math.max(55, Math.min(innerHeight - height - 6, info.rect.top - height - 6)) + 'px'; });
  }
  function setPending(kind) {
    if (!currentSelection) return;
    if (kind === 'quote') { addReference(currentSelection); return; }
    persistCurrent(); state.pending = { kind, parentId: state.active, quote: currentSelection.quote, blockId: currentSelection.blockId, endBlockId: currentSelection.endBlockId, endTextOffset: currentSelection.endTextOffset, sourceTextOffset: currentSelection.textOffset, sourceKind: currentSelection.kind, svg: currentSelection.svg, src: currentSelection.src, large: currentSelection.large, draft: '' };
    $('selection-tools').hidden = true; window.getSelection()?.removeAllRanges(); setComposer(); composer.focus(); save();
  }
  function renderReferencesButton() { $('references-toggle').textContent = '引用 · ' + ensureSession(current()).references.length; }
  function renderReferenceEditor() { const ref = ensureSession(current()).references.find(r => r.id === editingReference); if (!ref) return; const preview = $('reference-preview'); preview.replaceChildren(); renderReferencePreview(preview, ref); $('reference-comment').value = ref.comment || ''; const media = $('reference-attachments'); media.replaceChildren(); renderAttachments(media, ref.attachments, 'reference', ref.id); }
  function openReference(id) { editingReference = id; if ($('references-panel').open) $('references-panel').close(); renderReferenceEditor(); $('reference-editor').showModal(); $('reference-comment').focus(); }
  function addReference(context) {
    const refs = ensureSession(current()).references; const ref = { id: uid(), sessionId: state.active, blockId: context.blockId, textOffset: context.textOffset, quote: context.quote, kind: context.kind || 'text', svg: context.svg, src: context.src, number: context.number, large: context.large, comment: '', selected: true, attachments: [] };
    refs.push(ref); if (!saveReferences()) { refs.pop(); return; } $('selection-tools').hidden = true; $('context-menu').hidden = true; window.getSelection()?.removeAllRanges(); renderReferencesButton(); openReference(ref.id);
  }
  function renderReferenceList() {
    const list = $('references-list'); list.replaceChildren(); const refs = ensureSession(current()).references;
    if (!refs.length) { appendText('p', 'bookmarks-empty', '还没有引用。选择内容后可添加。', list); return; }
    refs.forEach((ref, i) => { const row = document.createElement('section'); row.className = 'reference-row'; const head = document.createElement('div'); head.className = 'reference-head'; const check = document.createElement('input'); check.type = 'checkbox'; check.checked = ref.selected !== false; check.dataset.referenceSelected = ref.id; check.setAttribute('aria-label', '携带引用 ' + (i + 1)); head.appendChild(check); appendText('span', '', '引用 ' + (i + 1), head); const edit = document.createElement('button'); edit.type = 'button'; edit.dataset.referenceEdit = ref.id; edit.textContent = '编辑'; head.appendChild(edit); const remove = document.createElement('button'); remove.type = 'button'; remove.dataset.referenceRemove = ref.id; remove.textContent = '删除'; head.appendChild(remove); row.appendChild(head); renderReferencePreview(row, ref); if (ref.comment) appendText('div', '', ref.comment, row); renderAttachments(row, ref.attachments); list.appendChild(row); });
  }
  function mainAttachments() { const pending = state.pending?.kind === 'branch' && state.pending.parentId === state.active ? state.pending : null; if (pending) { pending.attachments ||= []; return pending.attachments; } return ensureSession(current()).attachments; }
  function renderMainAttachments() { const box = $('attachment-list'); box.replaceChildren(); const attachments = mainAttachments(); renderAttachments(box, attachments, 'main'); if (!attachments.length) appendText('p', 'bookmarks-empty', '还没有附件。', box); $('attachments-toggle').textContent = '附件 · ' + attachmentGroups(attachments).length; }
  function addPathReferences(names, kind) {
    const target = mainAttachments(), added = names.map(name => kind === 'directory'
      ? { id: uid(), name, path: name + '/', directory: name + '/', referenceKind: kind }
      : { id: uid(), name, path: name, referenceKind: kind });
    target.push(...added);
    if (!saveDraft()) { target.splice(target.length - added.length, added.length); return; }
    renderMainAttachments(); $('status').textContent = '已添加路径引用示例；原型不能读取本机路径。';
  }
  const mentionExamples = [{ path: 'main.tex', kind: 'file' }, { path: 'chapters/', kind: 'directory' }, { path: 'chapters/introduction.tex', kind: 'file' }];
  let mentionSelection = 0, mentionHistoryInput = false;
  const mentionUndo = new Map();
  function mentionQuery() {
    const before = composer.value.slice(0, composer.selectionStart), match = before.match(/(?:^|[\s([])@([^\s@]*)$/);
    return match ? { start: before.length - match[1].length - 1, query: match[1].toLowerCase() } : null;
  }
  function showMentions() {
    const menu = $('mention-menu'), query = mentionQuery(); menu.replaceChildren();
    if (!query) { menu.hidden = true; return; }
    const items = mentionExamples.filter(item => item.path.toLowerCase().includes(query.query));
    if (!items.length) { menu.hidden = true; return; }
    mentionSelection = Math.min(mentionSelection, items.length - 1);
    items.forEach((item, index) => { const button = document.createElement('button'); button.type = 'button'; button.textContent = item.path; button.setAttribute('role', 'option'); button.setAttribute('aria-selected', String(index === mentionSelection)); button.addEventListener('mousedown', event => event.preventDefault()); button.addEventListener('click', () => insertMention(item)); menu.appendChild(button); });
    menu.hidden = false;
  }
  function insertMention(item) {
    const query = mentionQuery(); if (!query) return;
    const token = '@' + item.path, end = composer.selectionStart;
    composer.setRangeText(token + ' ', query.start, end, 'end');
    const target = mainAttachments(), name = item.kind === 'directory' ? item.path.replace(/\/$/, '') : item.path;
    const undoKey = state.active + (state.pending?.kind === 'branch' ? ':branch' : ''); mentionUndo.set(undoKey, (mentionUndo.get(undoKey) || []).filter(old => old.mentionToken !== token));
    target.push({ id: uid(), name, path: item.path, ...(item.kind === 'directory' ? { directory: item.path } : {}), referenceKind: item.kind, mentionToken: token });
    composer.dispatchEvent(new Event('input', { bubbles: true })); renderMainAttachments(); $('mention-menu').hidden = true; composer.focus();
  }
  function mentionCount(text, token) { let count = 0, position = 0; while ((position = text.indexOf(token, position)) >= 0) { const end = position + token.length; if ((position === 0 || /[\s([]/.test(text[position - 1])) && (end === text.length || /[\s,.)\]]/.test(text[end]))) count++; position = end; } return count; }
  function reconcileMentions(attachments, stash) {
    const groups = new Map(); for (const item of attachments) if (item.mentionToken) { const group = groups.get(item.mentionToken) || []; group.push(item); groups.set(item.mentionToken, group); }
    const removed = []; for (const [token, group] of groups) removed.push(...group.slice(mentionCount(composer.value, token)));
    if (removed.length) { stash.push(...removed); attachments.splice(0, attachments.length, ...attachments.filter(item => !removed.includes(item))); }
    for (let i = stash.length - 1; i >= 0; i--) { const item = stash[i], needed = mentionCount(composer.value, item.mentionToken) - attachments.filter(existing => existing.mentionToken === item.mentionToken).length; if (needed <= 0) continue; if (mentionHistoryInput) attachments.push(item); stash.splice(i, 1); }
    return removed.length > 0 || attachments.some(item => item.mentionToken);
  }
  async function appendFiles(files, referenceId) {
    const originSession = state.active;
    const ref = referenceId ? ensureSession(current()).references.find(r => r.id === referenceId) : null; if (referenceId && !ref) return;
    const target = ref ? (ref.attachments ||= []) : mainAttachments(); const staged = [], directoryGroups = new Map();
    pendingFileReads++; $('send').disabled = true; $('status').textContent = '正在读取附件。';
    try { for (const entry of files) { const file = entry.file || entry; const data = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = () => reject(reader.error); reader.readAsDataURL(file); }); const path = (entry.path || file.webkitRelativePath || file.name).replaceAll('\\', '/'); const directory = path.includes('/') ? path.split('/')[0] + '/' : ''; if (directory && !directoryGroups.has(directory)) directoryGroups.set(directory, uid()); staged.push({ id: uid(), name: file.name, path, ...(directory ? { directory, attachmentGroup: directoryGroups.get(directory) } : {}), type: file.type, size: file.size, data }); } if (ref && !session(originSession)?.references?.includes(ref)) throw new Error('reference removed'); target.push(...staged); const moved = state.active !== originSession; const ok = moved ? save() : ref ? saveReferences() : saveDraft(); if (!ok) throw new Error('storage'); if (moved) { localStorage.removeItem(referencesKey(originSession)); localStorage.removeItem(draftKey(originSession)); } else { if (ref) renderReferenceEditor(); else renderMainAttachments(); } $('status').textContent = '附件已保存在此浏览器的示例会话中。'; }
    catch { for (const file of staged) { const i = target.indexOf(file); if (i >= 0) target.splice(i, 1); } $('status').textContent = '无法保存附件；文件可能超过浏览器存储容量。'; }
    finally { pendingFileReads--; $('send').disabled = pendingFileReads > 0 || running.has(current().id); }
  }
  function demoAnswer(question, quote) {
    const subject = (question + ' ' + (quote || '')).toLowerCase();
    if (/例|比如|举/.test(subject)) return [block('paragraph', '示例：若先用整数模 5 作为简单模型，可以把 7 与 2 看作同一个余数类。这里仅演示追问的呈现方式，不能替代对原问题的实际推导。'), block('math', '7 \\equiv 2 \\pmod{5}')];
    if (/公式|多项式|环|结构|条件|为什么/.test(subject)) return [block('paragraph', '这里可以先区分符号代表的对象，以及它在这个具体推导中起到的作用。需要核实的细节应结合原文和完整上下文回答。'), block('paragraph', '这是预设示例答复，并没有调用模型。你可以继续输入问题，或再选中这段答复创建下一层追问。')];
    return [block('paragraph', '这是用于体验交互的预设示例答复，没有连接 AI，也不会对你的问题进行真实推理。'), block('paragraph', '你可以继续当前会话、选中此处创建更深一层的追问，或者返回上层继续阅读。')];
  }
  function send() {
    if (running.has(current().id)) return;
    if (pendingFileReads) { $('status').textContent = '请等待附件读取完成。'; return; }
    const question = composer.value.trim(); const refs = ensureSession(current()).references; const selected = refs.filter(ref => ref.selected !== false); const files = mainAttachments();
    if (!question && !selected.length && !files.length) { composer.focus(); return; }
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    const previousState = JSON.stringify(state);
    const previousSessionId = state.active;
    const sentReferences = JSON.parse(JSON.stringify(selected)), sentAttachments = JSON.parse(JSON.stringify(files));
    current().references = refs.filter(ref => ref.selected === false);
    const branching = pending && pending.kind === 'branch';
    if (branching) {
      const parent = current();
      const child = { id: uid(), title: (question || selected[0]?.comment || '追问').slice(0, 36), parent: parent.id, source: { blockId: pending.blockId, endBlockId: pending.endBlockId, endTextOffset: pending.endTextOffset, textOffset: pending.sourceTextOffset, quote: pending.quote, kind: pending.sourceKind, svg: pending.svg, src: pending.src }, draft: '', references: [], attachments: [], turns: [] };
      if (state.pendingBySession) delete state.pendingBySession[parent.id]; state.sessions.push(child); state.active = child.id; state.selected = child.id; state.pending = null;
      child.turns.push({ id: uid(), question, references: sentReferences, attachments: sentAttachments, answer: demoAnswer(question, pending.quote), demo: true });
    } else {
      current().turns.push({ id: uid(), question, references: sentReferences, attachments: sentAttachments, quote: pending && pending.kind === 'quote' ? pending.quote : '', answer: demoAnswer(question, pending && pending.quote), demo: true });
      current().draft = ''; current().attachments = []; state.pending = null;
    }
    if (!save()) { state = JSON.parse(previousState); $('status').textContent = '无法保存本次问题，输入内容已保留。'; composer.focus(); return; }
    mentionUndo.delete(previousSessionId); mentionUndo.delete(previousSessionId + ':branch'); mentionHistoryInput = false;
    if (branching) { try { localStorage.removeItem(draftKey(previousSessionId)); localStorage.removeItem(referencesKey(previousSessionId)); } catch { /* The sent snapshot has already been saved. */ } }
    composer.value = ''; if (branching) renderTranscript(); else transcript.appendChild(renderTurn(current().turns.at(-1), current().turns.length - 1)); setComposer();
    requestAnimationFrame(() => { reader.scrollTop = reader.scrollHeight; });
    $('status').textContent = '已保存示例问题和附件；下方是预设答复，未连接 AI。';
  }
  function openBookmark(candidate) {
    if (!candidate) return;
    bookmarkCandidate = { sessionId: state.active, blockId: candidate.blockId, textOffset: candidate.textOffset, quote: candidate.quote };
    $('selection-tools').hidden = true; window.getSelection()?.removeAllRanges(); $('bookmark-quote').textContent = candidate.quote;
    $('bookmark-name').value = candidate.quote.replace(/\s+/g, ' ').slice(0, 20);
    $('bookmark-error').hidden = true; $('bookmark-dialog').showModal(); $('bookmark-name').focus(); $('bookmark-name').select();
  }
  function renderBookmarks() {
    const list = $('bookmarks-list'); list.replaceChildren();
    if (!state.bookmarks.length) { appendText('p', 'bookmarks-empty', '还没有书签。选中文字或点击段落后可以添加。', list); return; }
    for (const bookmark of state.bookmarks) {
      const row = document.createElement('div'); row.className = 'bookmark-row';
      const open = document.createElement('button'); open.type = 'button'; open.className = 'bookmark-open'; open.dataset.bookmarkOpen = bookmark.id;
      appendText('span', '', bookmark.label, open); appendText('small', '', session(bookmark.sessionId)?.title || '未找到的会话', open); row.appendChild(open);
      const remove = document.createElement('button'); remove.type = 'button'; remove.className = 'bookmark-delete'; remove.dataset.bookmarkDelete = bookmark.id; remove.textContent = '删除'; remove.setAttribute('aria-label', '删除书签 ' + bookmark.label); row.appendChild(remove); list.appendChild(row);
    }
  }
  function setBookmarksOpen(open) { $('bookmarks-panel').hidden = !open; $('bookmarks-toggle').setAttribute('aria-expanded', String(open)); if (open) renderBookmarks(); }
  function goBookmark(bookmark) {
    const target = session(bookmark.sessionId); if (!target) { $('status').textContent = '该书签的会话不可用。'; return; }
    const exists = target.turns.some(turn => turn.answer.some(item => item.id === bookmark.blockId));
    if (!exists) { $('status').textContent = '书签原文不可用。'; return; }
    unfoldFor(target, bookmark.blockId); setBookmarksOpen(false);
    navigate(bookmark.sessionId, { blockId: bookmark.blockId, textOffset: bookmark.textOffset, lineY: 18, offset: -18 });
    $('status').textContent = '已跳转到书签：' + bookmark.label;
  }
  function makeNode(s, visited = new Set()) {
    visited.add(s.id);
    const branch = document.createElement('div'); branch.className = 'tree-node';
    const node = document.createElement('button'); node.type = 'button'; node.className = 'node-card'; node.dataset.nodeId = s.id; node.setAttribute('aria-current', String(s.id === state.selected));
    appendText('span', 'node-title', s.title, node); appendText('span', 'node-meta', s.parent ? `${s.turns.length} 轮讨论` : '主会话', node); branch.appendChild(node);
    const children = state.sessions.filter(other => other.parent === s.id && !visited.has(other.id));
    if (children.length) { const wrap = document.createElement('div'); wrap.className = 'tree-children'; for (const child of children) { const container = document.createElement('div'); container.className = 'tree-child'; container.appendChild(makeNode(child, new Set(visited))); wrap.appendChild(container); } branch.appendChild(wrap); }
    return branch;
  }
  function mapDetails() {
    const s = session(state.selected) || current(), box = $('map-detail'); box.replaceChildren();
    const parent = s.parent ? session(s.parent) : null; const turn = parent?.turns.findIndex(t => t.answer.some(b => b.id === s.source?.blockId));
    appendText('span', '', s.parent ? '来自「' + (parent?.title || '上层会话') + '」' + (turn >= 0 ? ` · 第 ${turn + 1} 轮回答` : '') + '：' + (s.source?.quote || '') : '主会话', box);
    const open = document.createElement('button'); open.type = 'button'; open.dataset.mapOpen = s.id; open.textContent = '打开会话'; box.appendChild(open);
    if (s.parent) { const source = document.createElement('button'); source.type = 'button'; source.dataset.mapSource = s.id; source.textContent = '查看提问出处'; box.appendChild(source); }
  }
  function renderMap() { $('map-tree').replaceChildren(); let root = session(relationAnchor) || current(); const visited = new Set(); while (root.parent && session(root.parent) && !visited.has(root.parent)) { visited.add(root.id); root = session(root.parent); } $('map-tree').appendChild(makeNode(root)); mapDetails(); }
  function openMap(fresh = true) { persistCurrent(); if (fresh || !relationAnchor) { relationAnchor = state.active; state.selected = state.active; } mapOpen = true; $('relation-tab-wrap').hidden = false; $('conversation-toolbar').hidden = true; $('reader-view').hidden = true; $('composer-pane').hidden = true; $('relation-map').hidden = false; $('map-toggle').setAttribute('aria-expanded', 'true'); $('conversation-tab').classList.remove('active'); $('relation-tab').classList.add('active'); $('selection-tools').hidden = true; renderMap(); save(); }
  function closeMap() { mapOpen = false; $('conversation-toolbar').hidden = false; $('reader-view').hidden = false; $('composer-pane').hidden = false; $('relation-map').hidden = true; $('map-toggle').setAttribute('aria-expanded', 'false'); $('conversation-tab').classList.add('active'); $('relation-tab').classList.remove('active'); }
  $('map-toggle').addEventListener('click', () => openMap()); $('map-close').addEventListener('click', closeMap);
  $('conversation-tab').addEventListener('click', closeMap); $('relation-tab').addEventListener('click', () => openMap(false));
  $('relation-tab-close').addEventListener('click', () => { closeMap(); relationAnchor = null; $('relation-tab-wrap').hidden = true; });
  $('new-conversation').addEventListener('click', () => { persistCurrent(); trimBlankSessions(); let blank = state.sessions.find(isBlankSession); if (!blank) { blank = { id: uid(), title: '新对话', parent: null, source: null, draft: '', turns: [], references: [], attachments: [] }; state.sessions.push(blank); } navigate(blank.id); save(); });
  $('back-source').addEventListener('click', () => parentSource(current()));
  $('child-sessions').addEventListener('click', () => { const children = state.sessions.filter(s => s.parent === current().id); if (children.length === 1) { navigate(children[0].id); return; } const menu = $('child-menu'); menu.replaceChildren(); for (const child of children) { const button = document.createElement('button'); button.type = 'button'; button.textContent = child.title; button.addEventListener('click', () => { menu.hidden = true; navigate(child.id); }); menu.appendChild(button); } menu.hidden = !menu.hidden; });
  $('selection-tools').addEventListener('pointerdown', e => e.preventDefault()); $('selection-branch').addEventListener('click', () => setPending('branch')); $('selection-quote').addEventListener('click', () => setPending('quote')); $('selection-bookmark').addEventListener('click', () => openBookmark(currentSelection));
  $('context-menu').addEventListener('pointerdown', e => e.preventDefault()); $('context-branch').addEventListener('click', () => { $('context-menu').hidden = true; setPending('branch'); }); $('context-quote').addEventListener('click', () => setPending('quote')); $('context-bookmark').addEventListener('click', () => { $('context-menu').hidden = true; openBookmark(currentSelection); });
  reader.addEventListener('contextmenu', e => { const el = e.target.closest('[data-block-id]'); if (!el) return; const selection = selectionInfo(); currentSelection = selection?.blockId === el.dataset.blockId ? selection : blockContext(el); if (!currentSelection) return; e.preventDefault(); $('selection-tools').hidden = true; const menu = $('context-menu'); menu.hidden = false; requestAnimationFrame(() => { menu.style.left = Math.max(6, Math.min(innerWidth - menu.offsetWidth - 6, e.clientX)) + 'px'; menu.style.top = Math.max(55, Math.min(innerHeight - menu.offsetHeight - 6, e.clientY)) + 'px'; }); });
  $('bookmark-paragraph').addEventListener('click', () => openBookmark(focusedBlock));
  $('bookmarks-toggle').addEventListener('click', () => { if (mapOpen) closeMap(); setBookmarksOpen($('bookmarks-panel').hidden); }); $('bookmarks-close').addEventListener('click', () => setBookmarksOpen(false));
  $('bookmark-cancel').addEventListener('click', () => $('bookmark-dialog').close());
  $('bookmark-save').addEventListener('click', () => { const label = $('bookmark-name').value.trim(); if (!label) { $('bookmark-error').textContent = '请填写书签名称。'; $('bookmark-error').hidden = false; $('bookmark-name').focus(); return; } if (!bookmarkCandidate) return; const bookmark = { id: uid(), ...bookmarkCandidate, label }; state.bookmarks.push(bookmark); if (!save()) { state.bookmarks.pop(); $('bookmark-error').textContent = '无法保存书签，请重试。'; $('bookmark-error').hidden = false; return; } $('bookmark-dialog').close(); bookmarkCandidate = null; $('status').textContent = '已保存书签：' + label; if (!$('bookmarks-panel').hidden) renderBookmarks(); });
  $('bookmarks-list').addEventListener('click', e => { const openId = e.target.closest('[data-bookmark-open]')?.dataset.bookmarkOpen; if (openId) { const bookmark = state.bookmarks.find(item => item.id === openId); if (bookmark) goBookmark(bookmark); return; } const deleteId = e.target.closest('[data-bookmark-delete]')?.dataset.bookmarkDelete; if (!deleteId) return; const index = state.bookmarks.findIndex(item => item.id === deleteId); if (index < 0) return; const removed = state.bookmarks.splice(index, 1)[0]; if (!save()) { state.bookmarks.splice(index, 0, removed); return; } renderBookmarks(); $('status').textContent = '已删除书签：' + removed.label; });
  $('references-toggle').addEventListener('click', () => { renderReferenceList(); $('references-panel').showModal(); }); $('references-close').addEventListener('click', () => $('references-panel').close());
  $('attachments-toggle').addEventListener('click', () => { renderMainAttachments(); $('attachments-panel').showModal(); }); $('attachments-close').addEventListener('click', () => $('attachments-panel').close());
  $('references-list').addEventListener('click', e => { const edit = e.target.closest('[data-reference-edit]')?.dataset.referenceEdit; if (edit) { openReference(edit); return; } const remove = e.target.closest('[data-reference-remove]')?.dataset.referenceRemove; if (!remove) return; const refs = ensureSession(current()).references, index = refs.findIndex(r => r.id === remove); if (index < 0) return; const old = refs.splice(index, 1)[0]; if (!saveReferences()) { refs.splice(index, 0, old); return; } renderReferenceList(); renderReferencesButton(); });
  $('references-list').addEventListener('change', e => { const id = e.target.dataset.referenceSelected; const ref = ensureSession(current()).references.find(r => r.id === id); if (ref) { const old = ref.selected; ref.selected = e.target.checked; if (!saveReferences()) { ref.selected = old; e.target.checked = old !== false; } } });
  $('reference-comment').addEventListener('input', () => { const ref = ensureSession(current()).references.find(r => r.id === editingReference); if (ref) { ref.comment = $('reference-comment').value; saveReferences(); } });
  $('reference-comment').addEventListener('keydown', e => { if (e.key === 'Enter' && e.ctrlKey && !e.shiftKey && !e.altKey) { e.preventDefault(); $('reference-confirm').click(); } });
  $('reference-confirm').addEventListener('click', () => { if (!saveReferences()) return; $('reference-editor').close(); editingReference = null; $('status').textContent = '引用已加入待发区；尚未请求 AI。'; }); $('reference-close').addEventListener('click', () => { $('reference-editor').close(); editingReference = null; });
  $('reference-attach').addEventListener('click', () => $('reference-file-input').click()); $('reference-file-input').addEventListener('change', e => { const files = Array.from(e.target.files || []); e.target.value = ''; if (files.length) appendFiles(files, editingReference); });
  function removeAttachment(e) { const button = e.target.closest('[data-attachment-remove]'); if (!button) return; const ref = button.dataset.attachmentScope === 'reference' ? ensureSession(current()).references.find(r => r.id === button.dataset.referenceId) : null; const target = ref ? ref.attachments : mainAttachments(); const group = attachmentGroups(target).find(item => item.files[0].id === button.dataset.attachmentRemove); if (!group) return; const previous = target.slice(), removing = new Set(group.files); target.splice(0, target.length, ...target.filter(file => !removing.has(file))); if (!(ref ? saveReferences() : saveDraft())) { target.splice(0, target.length, ...previous); return; } if (ref) renderReferenceEditor(); else renderMainAttachments(); }
  $('attachment-list').addEventListener('click', removeAttachment); $('reference-attachments').addEventListener('click', removeAttachment);
  function pasteImages(e, referenceId) { const files = Array.from(e.clipboardData?.files || []).filter(file => file.type.startsWith('image/')); if (!files.length) return; if (!e.clipboardData.getData('text/plain')) e.preventDefault(); appendFiles(files, referenceId); }
  function pasteMain(e) { const data = e.clipboardData; if (!data) return; let entries = []; try { entries = Array.from(data.items || []).filter(item => item.kind === 'file').map(item => typeof item.webkitGetAsEntry === 'function' ? item.webkitGetAsEntry() : null).filter(Boolean); } catch { /* Some browsers do not expose file system entries. */ } const files = Array.from(data.files || []); if (entries.some(entry => entry.isDirectory)) { e.preventDefault(); addPathReferences(entries.filter(entry => entry.isDirectory).map(entry => entry.name), 'directory'); if (files.length) appendFiles(files.filter(file => !entries.some(entry => entry.isDirectory && entry.name === file.name))); return; } if (files.length) { if (!data.getData('text/plain')) e.preventDefault(); appendFiles(files); return; } if (Array.from(data.types || []).includes('Files')) { e.preventDefault(); $('status').textContent = '浏览器无法读取粘贴的文件或文件夹，请使用添加附件。'; } }
  composer.addEventListener('paste', pasteMain); $('reference-comment').addEventListener('paste', e => pasteImages(e, editingReference));
  composer.addEventListener('dragover', e => { if (Array.from(e.dataTransfer?.types || []).includes('Files')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
  composer.addEventListener('drop', e => { const data = e.dataTransfer; if (!data || !Array.from(data.types || []).includes('Files')) return; e.preventDefault(); const items = Array.from(data.items || []), directories = items.map(item => item.webkitGetAsEntry?.()).filter(entry => entry?.isDirectory); if (directories.length) addPathReferences(directories.map(entry => entry.name), 'directory'); const files = Array.from(data.files || []).filter(file => !directories.some(directory => directory.name === file.name)); if (files.length) { if (e.shiftKey) addPathReferences(files.map(file => file.name), 'file'); else appendFiles(files); } });
  $('attach-toggle').addEventListener('click', () => { const open = $('attach-menu').hidden; $('attach-menu').hidden = !open; $('attach-toggle').setAttribute('aria-expanded', String(open)); });
  $('attach-files').addEventListener('click', () => { $('attach-menu').hidden = true; $('file-input').click(); }); $('attach-path-file').addEventListener('click', () => { $('attach-menu').hidden = true; $('path-file-input').click(); }); $('attach-folder').addEventListener('click', () => { $('attach-menu').hidden = true; $('folder-input').click(); });
  $('file-input').addEventListener('change', e => { const files = Array.from(e.target.files || []); e.target.value = ''; if (files.length) appendFiles(files); });
  $('path-file-input').addEventListener('change', e => { const files = Array.from(e.target.files || []); e.target.value = ''; if (files.length) addPathReferences(files.map(file => file.name), 'file'); });
  $('folder-input').addEventListener('change', e => { const files = Array.from(e.target.files || []); e.target.value = ''; if (files.length) addPathReferences([...new Set(files.map(file => file.webkitRelativePath.split('/')[0]))], 'directory'); else $('status').textContent = '浏览器无法选择空文件夹，或选择已取消。'; });
  if (!('webkitdirectory' in $('folder-input'))) { $('attach-folder').disabled = true; $('attach-folder').textContent = '引用文件夹（浏览器不支持）'; }
  $('demo-assets').addEventListener('click', () => { const turn = { id: uid(), question: '图文交互示例', answer: demoAssetBlocks(), demo: true }; current().turns.push(turn); if (!save()) { current().turns.pop(); return; } transcript.appendChild(renderTurn(turn, current().turns.length - 1)); requestAnimationFrame(() => { reader.scrollTop = reader.scrollHeight; }); $('status').textContent = '已添加图文示例；右键公式或图片可操作。'; });
  reader.addEventListener('pointerup', showSelection); reader.addEventListener('keyup', showSelection);
  reader.addEventListener('scroll', () => { if (!mapOpen) { current().viewport = reader.scrollTop; try { localStorage.setItem(viewportKey(state.active), String(reader.scrollTop)); } catch { $('status').textContent = '当前浏览器无法保存视口位置。'; } } }, { passive: true });
  function toggleHeading(target) { const heading = target.closest('[data-heading-id]'); if (!heading || window.getSelection()?.toString().trim()) return; const section = heading.closest('.turn'), turnIndex = Array.from(transcript.children).indexOf(section); const turn = current().turns[turnIndex]; if (!turn) return; const collapsed = heading.getAttribute('aria-expanded') === 'true'; turn.collapsedSections ||= {}; turn.collapsedSections[heading.dataset.headingId] = collapsed; syncFoldVisibility(section, turn); save(); }
  transcript.addEventListener('click', e => { const id = e.target.closest('[data-open-session]')?.dataset.openSession; if (id) { navigate(id); return; } const source = e.target.closest('[data-source-session]')?.dataset.sourceSession; if (source) { parentSource(session(source)); return; } const reference = e.target.closest('[data-reference-session]'); if (reference) { const target = session(reference.dataset.referenceSession); if (!target) { $('status').textContent = '无法定位引用出处。'; return; } const blockId = reference.dataset.referenceBlock; unfoldFor(target, blockId); const offset = reference.dataset.referenceOffset; navigate(target.id, { blockId, textOffset: offset === undefined ? undefined : Number(offset), center: true }); return; } const el = e.target.closest('[data-block-id]'); if (el && !window.getSelection()?.toString().trim()) { transcript.querySelector('.focused-passage')?.classList.remove('focused-passage'); el.classList.add('focused-passage'); focusedBlock = blockContext(el); $('bookmark-paragraph').disabled = false; } toggleHeading(e.target); });
  transcript.addEventListener('keydown', e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-heading-id]')) { e.preventDefault(); toggleHeading(e.target); } });
  $('composer-context').addEventListener('click', e => { if (e.target.id !== 'cancel-context') return; if (state.pending?.kind === 'branch') state.pending = null; else state.pending = null; setComposer(); save(); });
  composer.addEventListener('beforeinput', e => { mentionHistoryInput = e.inputType === 'historyUndo' || e.inputType === 'historyRedo'; });
  composer.addEventListener('input', () => { if (state.pending?.kind === 'branch' && state.pending.parentId === state.active) state.pending.draft = composer.value; else current().draft = composer.value; const attachments = mainAttachments(), key = state.active + (state.pending?.kind === 'branch' ? ':branch' : ''), stash = mentionUndo.get(key) || []; const changed = reconcileMentions(attachments, stash); mentionUndo.set(key, stash); mentionHistoryInput = false; if (changed) renderMainAttachments(); saveDraft(); showMentions(); });
  composer.addEventListener('keydown', e => { const menu = $('mention-menu'); if (!menu.hidden && !e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey) { const buttons = [...menu.querySelectorAll('button')]; if (e.key === 'Escape') { e.preventDefault(); menu.hidden = true; return; } if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); mentionSelection = Math.max(0, Math.min(buttons.length - 1, mentionSelection + (e.key === 'ArrowDown' ? 1 : -1))); buttons.forEach((button, index) => button.setAttribute('aria-selected', String(index === mentionSelection))); return; } if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); buttons[mentionSelection]?.click(); return; } } if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); send(); } }); $('send').addEventListener('click', send);
  $('map-tree').addEventListener('click', e => { const id = e.target.closest('[data-node-id]')?.dataset.nodeId; if (!id) return; state.selected = id; $('map-tree').querySelectorAll('[data-node-id]').forEach(node => node.setAttribute('aria-current', String(node.dataset.nodeId === id))); mapDetails(); save(); });
  $('map-tree').addEventListener('dblclick', e => { const id = e.target.closest('[data-node-id]')?.dataset.nodeId; if (id) navigate(id); });
  $('map-tree').addEventListener('keydown', e => { const id = e.target.closest('[data-node-id]')?.dataset.nodeId; if (id && e.key === 'Enter') { e.preventDefault(); navigate(id); } });
  $('map-detail').addEventListener('click', e => { const id = e.target.closest('[data-map-open]')?.dataset.mapOpen; if (id) navigate(id); const sourceId = e.target.closest('[data-map-source]')?.dataset.mapSource; if (sourceId) parentSource(session(sourceId)); });
  window.addEventListener('beforeunload', persistCurrent);
  document.addEventListener('copy', e => { const selection = window.getSelection(); if (!selection?.rangeCount || selection.isCollapsed || !e.clipboardData) return; const range = selection.getRangeAt(0); const folder = node => (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement)?.closest('.folder-path'); const start = folder(range.startContainer), end = folder(range.endContainer); if (start && start === end) { e.clipboardData.setData('text/plain', selection.toString().replace(/[\r\n]/g, '')); e.preventDefault(); return; } if (!transcript.contains(range.commonAncestorContainer)) return; const fragment = range.cloneContents(), inLink = node => (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement)?.closest('.associated'); if (!fragment.querySelector?.('.associated, .unresolved-associated') && !inLink(range.startContainer) && !inLink(range.endContainer)) return; const copied = inLink(range.commonAncestorContainer) ? '' : textWithoutLinks(fragment); e.clipboardData.setData('text/plain', copied); e.preventDefault(); });
  document.addEventListener('pointerdown', e => { if (!e.target.closest('#selection-tools') && !reader.contains(e.target)) $('selection-tools').hidden = true; if (!e.target.closest('#context-menu')) $('context-menu').hidden = true; });
  window.branchDemo = {
    getState: () => JSON.parse(JSON.stringify(state)),
    setState: next => { for (const s of state.sessions) localStorage.removeItem(draftKey(s.id)); state = JSON.parse(JSON.stringify(next)); state.bookmarks ||= []; closeMap(); renderTranscript(); setComposer(); reader.scrollTop = 0; save(); },
    reset: () => { for (const s of state.sessions) localStorage.removeItem(draftKey(s.id)); state = initial(); closeMap(); renderTranscript(); setComposer(); reader.scrollTop = 0; save(); },
    makeFixture: (turnCount = 120, branchCount = 60) => { const fixture = initial(); const root = fixture.sessions[0]; root.turns = Array.from({ length: turnCount }, (_, i) => ({ id: 'fixture-turn-' + i, question: '测试问题 ' + i, answer: [ { id: 'fixture-block-' + i, kind: 'paragraph', text: '同一阅读场景的固定测试内容。'.repeat(8) } ], demo: true })); for (let i = 0; i < branchCount; i++) fixture.sessions.push({ id: 'fixture-branch-' + i, title: '追问 ' + i, parent: 'root', source: { blockId: 'fixture-block-' + (i % turnCount), quote: '同一阅读场景' }, draft: '', turns: [] }); return fixture; },
    seedPerf: turns => { const fixture = initial(); fixture.sessions[0].turns = turns.map((t, i) => ({ id: t.id || 'fixture-turn-' + i, question: t.question, answer: Array.isArray(t.answer) ? t.answer : [{ id: 'fixture-block-' + i, kind: 'paragraph', text: String(t.answer || '') }], demo: true })); window.branchDemo.setState(fixture); }
  };
  if (!session(state.active)) state.active = state.sessions[0].id;
  renderTranscript(); setComposer(); requestAnimationFrame(() => { reader.scrollTop = current().viewport || 0; });
})();
