/* Only active scrolling holds a short-lived timer; idle scrollbars stay visible. */
(() => {
  const timers = new WeakMap();
  document.addEventListener('scroll', event => {
    const element = event.target === document ? document.documentElement : event.target;
    if (!(element instanceof Element)) return;
    element.classList.add('scroll-active');
    clearTimeout(timers.get(element));
    timers.set(element, setTimeout(() => { element.classList.remove('scroll-active'); timers.delete(element); }, 550));
  }, { capture: true, passive: true });
})();
