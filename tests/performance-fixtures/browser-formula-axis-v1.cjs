'use strict';
module.exports = () => {
  const source = Array.from({ length: 6 }, (_, i) =>
    '#'.repeat(i + 1) + ' 恢复答案 $x=2$ 保留上下文\n\n正文说明 $\\frac{1}{2}$ 仍在同一行。'
  ).join('\n\n');
  current().turns = Array.from({ length: 50 }, (_, i) => ({
    id: 'math-' + i,
    question: '公式与追问排版 ' + i,
    answer: source,
    answerHeadingVersion: 1,
    sentAt: '2026-10-28T15:00:00+08:00',
    answeredAt: '2026-10-28T15:00:01+08:00',
    segments: [],
    request: { annotations: i ? [{ block: 'math-' + (i - 1) + '-p0', endTextOffset: 4 }] : [] }
  }));
  current().draft = '保留测试草稿';
  $('composer').value = current().draft;
};
