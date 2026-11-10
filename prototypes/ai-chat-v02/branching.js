(() => {
  'use strict';
  const KEY = 'texeed-branch-reading-prototype-v1';
  const readingKey = id => KEY + '/reading/' + id, draftKey = id => KEY + '/draft/' + id;
  const $ = id => document.getElementById(id);
  const reader = $('reader'), transcript = $('transcript'), composer = $('composer');
  const uid = () => (globalThis.crypto && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2));
  const block = (kind, text) => ({ id: uid(), kind, text });
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
    block('paragraph', '完成一个问题的讨论后，你可以返回原先的阅读位置；如果想重新核对所指的句子，可以选择查看提问出处。这两个位置可能不同。'),
    block('paragraph', '继续阅读之后，如果在另一处遇到疑问，可以再创建一条追问会话。会话关系图会显示它们来自同一篇回答还是另一条追问，并允许你重新打开已经讨论过的问题。')
  ];
  const initial = () => ({ version: 1, active: 'root', selected: 'root', pending: null, returnPoint: null, sessions: [{ id: 'root', title: 'Saber OT 的代数结构', parent: null, source: null, reading: null, draft: '', turns: [{ id: 'root-turn', question: '请给我讲讲 Saber OT 底层的代数结构。', answer: rootBlocks(), demo: true }] }] });
  const readState = () => { try { const value = JSON.parse(localStorage.getItem(KEY) || 'null'); const loaded = value && value.version === 1 && Array.isArray(value.sessions) && value.sessions.length ? value : initial(); loaded.pendingBySession ||= {}; for (const s of loaded.sessions) { const reading = JSON.parse(localStorage.getItem(readingKey(s.id)) || 'null'); if (reading) s.reading = reading; const draft = JSON.parse(localStorage.getItem(draftKey(s.id)) || 'null'); if (draft) { s.draft = draft.draft; if (draft.pending) loaded.pendingBySession[s.id] = draft.pending; else delete loaded.pendingBySession[s.id]; } } loaded.pending = loaded.pendingBySession[loaded.active] || null; return loaded; } catch { return initial(); } };
  let state = readState(), currentSelection = null, restoring = false, mapOpen = false;
  const session = id => state.sessions.find(s => s.id === id);
  const current = () => session(state.active) || state.sessions[0];
  function save() { try { state.pendingBySession ||= {}; if (state.pending?.parentId === state.active) state.pendingBySession[state.active] = state.pending; else delete state.pendingBySession[state.active]; localStorage.setItem(KEY, JSON.stringify(state)); localStorage.removeItem(readingKey(state.active)); localStorage.removeItem(draftKey(state.active)); return true; } catch { $('status').textContent = '当前浏览器无法保存演示进度。'; return false; } }
  function saveReading() { try { localStorage.setItem(readingKey(state.active), JSON.stringify(current().reading)); } catch { $('status').textContent = '当前浏览器无法保存阅读位置。'; } }
  function saveDraft() { try { localStorage.setItem(draftKey(state.active), JSON.stringify({ draft: current().draft, pending: state.pending?.parentId === state.active ? state.pending : null })); } catch { $('status').textContent = '当前浏览器无法保存示例草稿。'; } }
  function capturePosition(withText = false) {
    const bounds = reader.getBoundingClientRect();
    const candidates = Array.from(transcript.querySelectorAll('[data-block-id]'));
    const first = candidates.find(el => el.getBoundingClientRect().bottom > bounds.top + 4) || candidates.at(-1);
    if (!first) return { fallback: reader.scrollTop };
    const rect = first.getBoundingClientRect(), result = { blockId: first.dataset.blockId, offset: bounds.top - rect.top, fallback: reader.scrollTop };
    if (withText && !first.classList.contains('formula') && document.caretRangeFromPoint) {
      const point = document.caretRangeFromPoint(Math.min(rect.right - 2, rect.left + 8), Math.max(bounds.top + 2, rect.top + 2));
      if (point && first.contains(point.startContainer)) { const prefix = document.createRange(); prefix.selectNodeContents(first); prefix.setEnd(point.startContainer, point.startOffset); result.textOffset = prefix.toString().length; result.lineY = point.getBoundingClientRect().top - bounds.top; }
    }
    return result;
  }
  function restorePosition(pos) {
    restoring = true;
    requestAnimationFrame(() => {
      const el = pos && pos.blockId ? transcript.querySelector(`[data-block-id="${CSS.escape(pos.blockId)}"]`) : null;
      if (el && Number.isInteger(pos.textOffset)) {
        const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT); let node, left = pos.textOffset, target = null;
        while ((node = walker.nextNode())) { if (left <= node.length) { target = node; break; } left -= node.length; }
        if (target) { const range = document.createRange(); range.setStart(target, Math.min(left, target.length)); range.collapse(true); const rect = range.getBoundingClientRect(); reader.scrollTop += rect.top - reader.getBoundingClientRect().top - (pos.lineY || 0); }
        else reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().top + (pos.offset || 0);
      }
      else if (el) reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().top + (pos.offset || 0);
      else reader.scrollTop = pos && Number.isFinite(pos.fallback) ? pos.fallback : 0;
      requestAnimationFrame(() => { restoring = false; });
    });
  }
  function persistCurrent() {
    if (!mapOpen && !restoring) current().reading = capturePosition();
    if (state.pending && state.pending.kind === 'branch' && state.pending.parentId === state.active) state.pending.draft = composer.value;
    else current().draft = composer.value;
    save();
  }
  function appendText(tag, cls, text, parent) { const el = document.createElement(tag); if (cls) el.className = cls; el.textContent = text; parent.appendChild(el); return el; }
  function quoteElement(parent, label, text) { const quote = document.createElement('div'); quote.className = 'quoted'; const prefix = document.createElement('span'); prefix.textContent = label + '：'; quote.append(prefix, document.createTextNode(text)); parent.appendChild(quote); }
  function renderBlock(parent, item, number, heading) {
    let el;
    if (item.kind === 'heading') { el = document.createElement('div'); el.className = 'passage passage-heading'; el.setAttribute('role', 'button'); el.setAttribute('tabindex', '0'); el.dataset.headingId = item.id; const mark = document.createElement('span'); mark.className = 'heading-number'; mark.textContent = `[${number}:${heading}]`; el.append(mark, document.createTextNode(item.text)); }
    else if (item.kind === 'math') { el = document.createElement('div'); el.className = 'passage formula'; try { katex.render(item.text, el, { displayMode: true, throwOnError: false, output: 'html' }); } catch { el.textContent = item.text; } }
    else { el = document.createElement('p'); el.className = 'passage'; el.textContent = item.text; }
    el.dataset.blockId = item.id; parent.appendChild(el);
    const related = state.sessions.filter(s => s.parent === current().id && s.source && s.source.blockId === item.id);
    if (related.length) { const links = document.createElement('div'); for (const child of related) { const link = document.createElement('button'); link.className = 'associated'; link.textContent = '追问：' + child.title; link.dataset.openSession = child.id; links.appendChild(link); } parent.appendChild(links); }
  }
  function renderTurn(turn, i) {
      const wrap = document.createElement('section'); wrap.className = 'turn';
      const q = appendText('div', 'turn-question', turn.question, wrap); const num = document.createElement('span'); num.className = 'turn-number'; num.textContent = `[${i + 1}]`; q.prepend(num);
      appendText('div', 'time', '示例时间　发送', wrap);
      if (turn.quote) quoteElement(wrap, '引用', turn.quote);
      const rule = document.createElement('hr'); rule.className = 'answer-rule'; wrap.appendChild(rule);
      if (turn.demo) appendText('div', 'example-label', '示例答复 · 未连接 AI', wrap);
      let heading = 0, folded = false; for (const item of turn.answer) { const before = wrap.children.length; if (item.kind === 'heading') { heading++; folded = Boolean(turn.collapsedSections?.[item.id]); } renderBlock(wrap, item, i + 1, heading); const added = Array.from(wrap.children).slice(before); if (item.kind === 'heading') added[0].setAttribute('aria-expanded', String(!folded)); else if (folded) added.forEach(el => { el.hidden = true; }); }
      const end = document.createElement('hr'); end.className = 'answer-rule'; wrap.appendChild(end);
      appendText('div', 'time', '示例时间　答复', wrap); return wrap;
  }
  function renderTranscript() {
    transcript.replaceChildren();
    const s = current();
    s.turns.forEach((turn, i) => transcript.appendChild(renderTurn(turn, i)));
    $('session-title').textContent = s.title;
    $('mark-reading').textContent = s.markedReading ? '更新阅读处' : '记住阅读处';
    $('back-source').hidden = !s.parent;
    const hasReturn = Boolean((state.returnPoint && state.returnPoint.sessionId === s.id) || s.parent);
    $('back-reading').hidden = !hasReturn;
    $('back-reading').textContent = state.returnPoint && state.returnPoint.sessionId === s.id ? '← 返回阅读断点' : '← 返回上层阅读断点';
    renderComposerContext();
  }
  function renderComposerContext() {
    const box = $('composer-context'); box.replaceChildren();
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    box.hidden = !pending;
    if (pending) { const text = document.createElement('div'); text.className = 'context-text'; const strong = document.createElement('strong'); strong.textContent = pending.kind === 'branch' ? '新开追问型会话' : '引用到当前会话'; text.append(strong, document.createTextNode('　' + pending.quote)); box.appendChild(text); const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = '取消'; cancel.id = 'cancel-context'; box.appendChild(cancel); }
    composer.placeholder = pending && pending.kind === 'branch' ? '写下追问；发送后创建追问会话……' : '继续当前会话……';
  }
  function setComposer() {
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    composer.value = pending && pending.kind === 'branch' ? (pending.draft || '') : (current().draft || '');
    renderComposerContext();
  }
  function navigate(id, position) {
    if (!session(id)) return;
    persistCurrent(); state.active = id; state.selected = id; state.pending = state.pendingBySession?.[id] || null; currentSelection = null;
    $('selection-tools').hidden = true; closeMap(); renderTranscript(); setComposer();
    restorePosition(position || current().reading); save();
  }
  function parentSource(target) {
    if (!target || !target.parent) return;
    const source = target.source || {};
    const parent = session(target.parent);
    if (parent) for (const turn of parent.turns) { let headingId = null; for (const item of turn.answer) { if (item.kind === 'heading') headingId = item.id; if (item.id === source.blockId && headingId && turn.collapsedSections) turn.collapsedSections[headingId] = false; } }
    state.returnPoint = { sessionId: target.parent, reading: source.parentReading || session(target.parent).reading };
    navigate(target.parent, { blockId: source.blockId, textOffset: source.textOffset, lineY: 18, offset: -18 });
    requestAnimationFrame(() => { const el = transcript.querySelector(`[data-block-id="${CSS.escape(source.blockId || '')}"]`); if (el) { el.classList.add('source-flash'); setTimeout(() => el.classList.remove('source-flash'), 3000); } });
    $('status').textContent = '已定位提问出处；可返回原来的阅读断点。';
  }
  function returnReading() {
    const s = current();
    if (state.returnPoint && state.returnPoint.sessionId === s.id) { const pos = state.returnPoint.reading; state.returnPoint = null; restorePosition(pos); $('back-reading').hidden = !s.parent; $('status').textContent = '已返回原来的阅读断点。'; save(); return; }
    if (s.parent) { const pos = s.source && s.source.parentReading; state.returnPoint = null; navigate(s.parent, pos); $('status').textContent = '已返回上层的阅读断点。'; }
  }
  function selectionInfo() {
    const sel = window.getSelection(); if (!sel || sel.isCollapsed || !sel.toString().trim() || !sel.rangeCount) return null;
    const range = sel.getRangeAt(0); if (!transcript.contains(range.commonAncestorContainer)) return null;
    const element = range.startContainer.nodeType === Node.ELEMENT_NODE ? range.startContainer : range.startContainer.parentElement;
    const source = element && element.closest('[data-block-id]'); if (!source) return null;
    let textOffset;
    if (!source.classList.contains('formula') && source.contains(range.startContainer)) { const prefix = document.createRange(); prefix.selectNodeContents(source); prefix.setEnd(range.startContainer, range.startOffset); textOffset = prefix.toString().length; }
    return { quote: sel.toString().trim().slice(0, 2000), blockId: source.dataset.blockId, textOffset, reading: capturePosition(true), rect: range.getBoundingClientRect() };
  }
  function showSelection() {
    const info = selectionInfo(); if (!info) { $('selection-tools').hidden = true; return; }
    currentSelection = info; const tools = $('selection-tools'); tools.hidden = false;
    requestAnimationFrame(() => { const width = tools.offsetWidth, height = tools.offsetHeight; tools.style.left = Math.max(6, Math.min(innerWidth - width - 6, info.rect.left)) + 'px'; tools.style.top = Math.max(55, Math.min(innerHeight - height - 6, info.rect.top - height - 6)) + 'px'; });
  }
  function setPending(kind) {
    if (!currentSelection) return;
    persistCurrent(); state.pending = { kind, parentId: state.active, quote: currentSelection.quote, blockId: currentSelection.blockId, sourceTextOffset: currentSelection.textOffset, parentReading: current().markedReading || currentSelection.reading, draft: '' };
    $('selection-tools').hidden = true; window.getSelection()?.removeAllRanges(); setComposer(); composer.focus(); save();
  }
  function demoAnswer(question, quote) {
    const subject = (question + ' ' + (quote || '')).toLowerCase();
    if (/例|比如|举/.test(subject)) return [block('paragraph', '示例：若先用整数模 5 作为简单模型，可以把 7 与 2 看作同一个余数类。这里仅演示追问的呈现方式，不能替代对原问题的实际推导。'), block('math', '7 \\equiv 2 \\pmod{5}')];
    if (/公式|多项式|环|结构|条件|为什么/.test(subject)) return [block('paragraph', '这里可以先区分符号代表的对象，以及它在这个具体推导中起到的作用。需要核实的细节应结合原文和完整上下文回答。'), block('paragraph', '这是预设示例答复，并没有调用模型。你可以继续输入问题，或再选中这段答复创建下一层追问。')];
    return [block('paragraph', '这是用于体验交互的预设示例答复，没有连接 AI，也不会对你的问题进行真实推理。'), block('paragraph', '你可以继续当前会话、选中此处创建更深一层的追问，或者返回上层继续阅读。')];
  }
  function send() {
    const question = composer.value.trim(); if (!question) { composer.focus(); return; }
    const pending = state.pending && state.pending.parentId === state.active ? state.pending : null;
    const previousState = JSON.stringify(state);
    const previousSessionId = state.active;
    const branching = pending && pending.kind === 'branch';
    if (branching) {
      const parent = current(); parent.reading = pending.parentReading;
      const child = { id: uid(), title: question.slice(0, 36), parent: parent.id, source: { blockId: pending.blockId, textOffset: pending.sourceTextOffset, quote: pending.quote, parentReading: pending.parentReading }, reading: null, draft: '', turns: [] };
      if (state.pendingBySession) delete state.pendingBySession[parent.id]; delete parent.markedReading; state.sessions.push(child); state.active = child.id; state.selected = child.id; state.returnPoint = null; state.pending = null;
      child.turns.push({ id: uid(), question, answer: demoAnswer(question, pending.quote), demo: true });
    } else {
      current().turns.push({ id: uid(), question, quote: pending && pending.kind === 'quote' ? pending.quote : '', answer: demoAnswer(question, pending && pending.quote), demo: true });
      current().draft = ''; state.pending = null;
    }
    if (!save()) { state = JSON.parse(previousState); $('status').textContent = '无法保存本次问题，输入内容已保留。'; composer.focus(); return; }
    if (branching) { try { localStorage.removeItem(draftKey(previousSessionId)); } catch { /* The sent snapshot has already been saved. */ } }
    composer.value = ''; if (branching) renderTranscript(); else transcript.appendChild(renderTurn(current().turns.at(-1), current().turns.length - 1)); setComposer();
    requestAnimationFrame(() => { reader.scrollTop = reader.scrollHeight; current().reading = capturePosition(); save(); });
    $('status').textContent = '已发送示例问题；下方显示预设答复。';
  }
  function makeNode(s) {
    const branch = document.createElement('div'); branch.className = 'tree-node';
    const node = document.createElement('button'); node.type = 'button'; node.className = 'node-card'; node.dataset.nodeId = s.id; node.setAttribute('aria-current', String(s.id === state.selected));
    appendText('span', 'node-title', s.title, node); appendText('span', 'node-meta', s.parent ? `${s.turns.length} 轮讨论` : '主会话', node); branch.appendChild(node);
    const children = state.sessions.filter(other => other.parent === s.id);
    if (children.length) { const wrap = document.createElement('div'); wrap.className = 'tree-children'; for (const child of children) { const container = document.createElement('div'); container.className = 'tree-child'; container.appendChild(makeNode(child)); wrap.appendChild(container); } branch.appendChild(wrap); }
    return branch;
  }
  function mapDetails() {
    const s = session(state.selected) || current(), box = $('map-detail'); box.replaceChildren();
    const parent = s.parent ? session(s.parent) : null; const turn = parent?.turns.findIndex(t => t.answer.some(b => b.id === s.source?.blockId));
    appendText('span', '', s.parent ? '来自「' + (parent?.title || '上层会话') + '」' + (turn >= 0 ? ` · 第 ${turn + 1} 轮回答` : '') + '：' + (s.source?.quote || '') : '主会话', box);
    const open = document.createElement('button'); open.type = 'button'; open.dataset.mapOpen = s.id; open.textContent = '打开会话'; box.appendChild(open);
    if (s.parent) { const source = document.createElement('button'); source.type = 'button'; source.dataset.mapSource = s.id; source.textContent = '查看提问出处'; box.appendChild(source); }
  }
  function renderMap() { $('map-tree').replaceChildren(); const roots = state.sessions.filter(s => !s.parent || !session(s.parent)); for (const root of roots) $('map-tree').appendChild(makeNode(root)); mapDetails(); }
  function openMap() { persistCurrent(); mapOpen = true; state.selected = state.active; $('reader-view').hidden = true; $('composer-pane').hidden = true; $('relation-map').hidden = false; $('map-toggle').setAttribute('aria-expanded', 'true'); $('selection-tools').hidden = true; renderMap(); save(); }
  function closeMap() { mapOpen = false; $('reader-view').hidden = false; $('composer-pane').hidden = false; $('relation-map').hidden = true; $('map-toggle').setAttribute('aria-expanded', 'false'); }
  $('map-toggle').addEventListener('click', () => mapOpen ? closeMap() : openMap()); $('map-close').addEventListener('click', closeMap);
  $('back-reading').addEventListener('click', returnReading); $('back-source').addEventListener('click', () => parentSource(current()));
  $('mark-reading').addEventListener('click', () => { current().markedReading = capturePosition(true); $('mark-reading').textContent = '更新阅读处'; $('status').textContent = '已记住当前位置；回看其他位置并追问后可返回这里。'; save(); });
  $('selection-tools').addEventListener('pointerdown', e => e.preventDefault()); $('selection-branch').addEventListener('click', () => setPending('branch')); $('selection-quote').addEventListener('click', () => setPending('quote'));
  reader.addEventListener('pointerup', showSelection); reader.addEventListener('keyup', showSelection);
  reader.addEventListener('scroll', () => { if (!restoring && !mapOpen) { current().reading = capturePosition(); saveReading(); } }, { passive: true });
  function toggleHeading(target) { const heading = target.closest('[data-heading-id]'); if (!heading || window.getSelection()?.toString().trim()) return; const turnIndex = Array.from(transcript.children).indexOf(heading.closest('.turn')); const turn = current().turns[turnIndex]; if (!turn) return; const collapsed = heading.getAttribute('aria-expanded') === 'true'; turn.collapsedSections ||= {}; turn.collapsedSections[heading.dataset.headingId] = collapsed; heading.setAttribute('aria-expanded', String(!collapsed)); let sibling = heading.nextElementSibling; while (sibling && !sibling.matches('[data-heading-id], .answer-rule, .time')) { sibling.hidden = collapsed; sibling = sibling.nextElementSibling; } save(); }
  transcript.addEventListener('click', e => { const id = e.target.closest('[data-open-session]')?.dataset.openSession; if (id) { navigate(id); return; } toggleHeading(e.target); });
  transcript.addEventListener('keydown', e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-heading-id]')) { e.preventDefault(); toggleHeading(e.target); } });
  $('composer-context').addEventListener('click', e => { if (e.target.id !== 'cancel-context') return; if (state.pending?.kind === 'branch') state.pending = null; else state.pending = null; setComposer(); save(); });
  composer.addEventListener('input', () => { if (state.pending?.kind === 'branch' && state.pending.parentId === state.active) state.pending.draft = composer.value; else current().draft = composer.value; saveDraft(); });
  composer.addEventListener('keydown', e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); send(); } }); $('send').addEventListener('click', send);
  $('map-tree').addEventListener('click', e => { const id = e.target.closest('[data-node-id]')?.dataset.nodeId; if (!id) return; state.selected = id; renderMap(); save(); });
  $('map-detail').addEventListener('click', e => { const id = e.target.closest('[data-map-open]')?.dataset.mapOpen; if (id) navigate(id); const sourceId = e.target.closest('[data-map-source]')?.dataset.mapSource; if (sourceId) parentSource(session(sourceId)); });
  window.addEventListener('beforeunload', persistCurrent);
  document.addEventListener('pointerdown', e => { if (!e.target.closest('#selection-tools') && !reader.contains(e.target)) $('selection-tools').hidden = true; });
  window.branchDemo = {
    getState: () => JSON.parse(JSON.stringify(state)),
    setState: next => { for (const s of state.sessions) { localStorage.removeItem(readingKey(s.id)); localStorage.removeItem(draftKey(s.id)); } state = JSON.parse(JSON.stringify(next)); closeMap(); renderTranscript(); setComposer(); restorePosition(current().reading); save(); },
    reset: () => { for (const s of state.sessions) { localStorage.removeItem(readingKey(s.id)); localStorage.removeItem(draftKey(s.id)); } state = initial(); closeMap(); renderTranscript(); setComposer(); restorePosition(null); save(); },
    makeFixture: (turnCount = 120, branchCount = 60) => { const fixture = initial(); const root = fixture.sessions[0]; root.turns = Array.from({ length: turnCount }, (_, i) => ({ id: 'fixture-turn-' + i, question: '测试问题 ' + i, answer: [ { id: 'fixture-block-' + i, kind: 'paragraph', text: '同一阅读场景的固定测试内容。'.repeat(8) } ], demo: true })); for (let i = 0; i < branchCount; i++) fixture.sessions.push({ id: 'fixture-branch-' + i, title: '追问 ' + i, parent: 'root', source: { blockId: 'fixture-block-' + (i % turnCount), quote: '同一阅读场景', parentReading: null }, reading: null, draft: '', turns: [] }); return fixture; },
    seedPerf: turns => { const fixture = initial(); fixture.sessions[0].turns = turns.map((t, i) => ({ id: t.id || 'fixture-turn-' + i, question: t.question, answer: Array.isArray(t.answer) ? t.answer : [{ id: 'fixture-block-' + i, kind: 'paragraph', text: String(t.answer || '') }], demo: true })); window.branchDemo.setState(fixture); }
  };
  if (!session(state.active)) state.active = state.sessions[0].id;
  renderTranscript(); setComposer(); restorePosition(current().reading);
})();
