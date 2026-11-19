(function () {
  const skip = '.math, .katex, .katex-display, .heading-marker, .heading-number, .same-turn-link, .associated';
  const measurements = new Map();
  let context;

  function textNear(element) {
    for (const direction of ['previousSibling', 'nextSibling']) {
      for (let sibling = element[direction]; sibling; sibling = sibling[direction]) {
        if (sibling.nodeType === Node.ELEMENT_NODE && sibling.matches(skip)) continue;
        const text = sibling.textContent;
        const sample = (direction === 'previousSibling' ? text.slice(-12) : text.slice(0, 12)).replace(/[^\p{L}\p{N}]/gu, '');
        if (sample) return sample;
      }
    }
    return '';
  }

  window.alignReadingMedia = function (root) {
    const nodes = root.querySelectorAll('.reading-bubble');
    if (!nodes.length) return;
    if (!context) {
      const style = getComputedStyle(document.documentElement);
      context = document.createElement('canvas').getContext('2d');
      context.font = `${style.getPropertyValue('--reading-font')} ${style.getPropertyValue('--reading-face')}`;
    }
    for (const element of nodes) {
      const text = textNear(element.parentElement) || '中文';
      let axis = measurements.get(text);
      if (axis === undefined) {
        const ink = context.measureText(text);
        axis = (ink.actualBoundingBoxAscent - ink.actualBoundingBoxDescent) / 2;
        if (measurements.size >= 512) measurements.clear();
        measurements.set(text, axis);
      }
      element.style.setProperty('--reading-ink-axis', `${axis}px`);
    }
  };

  async function showReadingFontFallback() {
    const shell = document.querySelector('.reading-shell, .reader-view');
    const reader = document.querySelector('#reader');
    if (!shell || !reader) return;
    const notice = document.createElement('div');
    notice.className = 'reading-font-notice';
    notice.setAttribute('role', 'status');
    notice.hidden = true;
    shell.insertBefore(notice, reader);
    const available = async (family, text) => {
      try { return (await document.fonts.load(`16px "${family}"`, text)).length > 0; }
      catch { return false; }
    };
    const [hiragino, plex] = await Promise.all([
      available('TeXeed Hiragino W3', '中文'), available('TeXeed Plex Sans', 'Il1 O0')
    ]);
    measurements.clear();
    window.alignReadingMedia(reader);
    if (hiragino && plex) return;
    notice.textContent = !hiragino && !plex ? '未找到阅读字体，暂用系统字体。' :
      !hiragino ? '未找到冬青黑体 W3，中文暂用系统字体。' : '未找到 IBM Plex Sans，英文与数字暂用系统字体。';
    notice.hidden = false;
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', showReadingFontFallback, { once: true });
  else showReadingFontFallback();
}());
