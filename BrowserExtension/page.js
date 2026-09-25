// EdgeDock, вкладка Яндекс Музыки: этот скрипт работает в мире самой страницы, рядом с плеером.
// Плеер сам сообщает браузеру позицию и длину трека (navigator.mediaSession.setPositionState) и принимает
// от него перемотку (обработчик "seekto") — на этом работают медиакнопки браузера. Скрипт подслушивает
// оба вызова и через события окна передаёт их content.js: так время и перемотка не зависят от вёрстки плеера.
//   edgedock-position         → content.js: JSON { duration, position, rate, at, canSeek } или null;
//   edgedock-seek             ← content.js: секунды от начала трека;
//   edgedock-position-request ← content.js: повторить последнее состояние (content.js встроился позже).
(() => {
  'use strict';

  // Уже встроен — например, до перезагрузки расширения: тот экземпляр и работает.
  if (window.__edgedockPage || !('mediaSession' in navigator)) return;
  window.__edgedockPage = 1;

  const proto = MediaSession.prototype;
  const setPositionState = proto.setPositionState;
  const setActionHandler = proto.setActionHandler;
  let seekHandler = null;
  let last = null;

  function publish() {
    try {
      window.dispatchEvent(new CustomEvent('edgedock-position', { detail: JSON.stringify(last) }));
    } catch (e) { /* плееру наши ошибки не нужны */ }
  }

  proto.setPositionState = function (state) {
    const result = setPositionState.apply(this, arguments);
    const duration = state && Number(state.duration);
    last = duration > 0 && Number.isFinite(duration)
      ? { duration, position: Number(state.position) || 0, rate: Number(state.playbackRate) || 1, at: Date.now(), canSeek: !!seekHandler }
      : null;
    publish();
    return result;
  };

  proto.setActionHandler = function (action, handler) {
    const result = setActionHandler.apply(this, arguments);
    if (action === 'seekto') {
      seekHandler = typeof handler === 'function' ? handler : null;
      if (last && last.canSeek !== !!seekHandler) {
        last.canSeek = !!seekHandler;
        publish();
      }
    }
    return result;
  };

  window.addEventListener('edgedock-seek', event => {
    const seconds = Number(event.detail);
    if (!seekHandler || !Number.isFinite(seconds)) return;
    // Как медиакнопки браузера. Ровно на 0 плеер не перематывает — просим чуть дальше.
    seekHandler({ action: 'seekto', seekTime: Math.max(seconds, 0.01) });
  });

  window.addEventListener('edgedock-position-request', () => {
    if (last) publish();
  });
})();
