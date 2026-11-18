(function () {
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
    if (hiragino && plex) return;
    notice.textContent = !hiragino && !plex ? '未找到阅读字体，暂用系统字体。' :
      !hiragino ? '未找到冬青黑体 W3，中文暂用系统字体。' : '未找到 IBM Plex Sans，英文与数字暂用系统字体。';
    notice.hidden = false;
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', showReadingFontFallback, { once: true });
  else showReadingFontFallback();
}());
