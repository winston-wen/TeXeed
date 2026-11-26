'use strict';

// Identify the turn from the user's own request. Example wording from a previous
// reply can supply the replacement only after the user has asked to write it back.
function prototypeRevisionRequest(prompt, turns) {
  const previous = turns.at(-1);
  if (!previous || /^(?:.*[？?]\s*$|(?:请)?(?:暂不|不要|别|只讨论|只解释))/.test(prompt)) return null;
  const ordinal = /第\s*(\d+)\s*轮/;
  const command = /^(?:请(?:你)?(?:帮我)?|帮我|麻烦你)?\s*(?:(?:直接|现在)?(?:修改|改写|重写|整理|润色|精简)\s*(?:第\s*\d+\s*轮|上一轮|上轮)|(?:把|将|在)\s*第\s*\d+\s*轮[^。！？\n]{0,160}?(?:改为|改成|删掉|去掉|写回|改写))/;
  const writeBack = /^(?:你)?(?:倒是|请(?:你)?|帮我)?(?:直接|现在|就)?(?:写过去|写回(?:去|原文|原回答)?|改到原文里|保存到原文)(?:啊|吧|呀|了)?[。！!\s]*$/;
  let number, instruction = prompt;
  if (command.test(prompt)) {
    if (/^(?:是什么意思|的方法|会不会|吗)|只(?:讨论|解释)|(?:不要|先别|暂不)(?:改|写)/.test(prompt.replace(command,'').trim())) return null;
    number = Number(ordinal.exec(prompt)?.[1] || turns.length);
  } else if (writeBack.test(prompt) && !previous.incomplete && /可(?:以)?(?:改|写|替换)|替换稿|改为|改成|写回/.test(previous.answer)) {
    const targets = new Set(Array.from((previous.prompt ?? previous.question ?? '').matchAll(/第\s*(\d+)\s*轮/g), m => Number(m[1])));
    if (targets.size !== 1) return null;
    [number] = targets;
    if (number >= turns.length) return null;
    const proposal = previous.answer.match(/把[「“][^」”]+[」”]改[成为][「“][^」”]*[」”]/u);
    if (proposal) instruction = proposal[0];
  } else return null;
  const target = turns[number-1];
  return target && !target.incomplete ? {target,number,instruction} : null;
}
