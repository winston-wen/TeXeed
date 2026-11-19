'use strict';

module.exports = () => {
  const source = Array.from({ length: 6 }, (_, i) =>
    '#'.repeat(i + 1) + ' 参数 $512$ 和 $64$\n\n取值 $064$，步长 $2x$。'
  ).join('\n\n');
  current().turns = Array.from({ length: 50 }, (_, i) => ({
    id: 'numbers-' + i, question: '参数取值 ' + i, answer: source,
    answerHeadingVersion: 1, sentAt: '2026-10-28T15:00:00+08:00', answeredAt: '2026-10-28T15:00:01+08:00',
    segments: [], request: { annotations: i ? [{ block: 'numbers-' + (i - 1) + '-p0', endTextOffset: 2 }] : [] }
  }));
  current().draft = '保留测试草稿';
  $('composer').value = current().draft;
};
