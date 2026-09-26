// EdgeDock, вкладка Яндекс Музыки.
// Читает, что играет, из Media Session (её заполняет сам плеер Яндекс Музыки), позицию и длину — из того,
// что плеер сообщает браузеру (это подслушивает page.js), лайк — из кнопки «Нравится», и отдаёт это фоновому скрипту.
// Кнопки, перемотку и лайк из виджета выполняет на странице так же, как если бы нажали вы.
//
// Скрипт может оказаться во вкладке дважды: браузер встраивает его при загрузке страницы, а фоновый скрипт —
// во вкладки, открытые до установки или обновления расширения. Поэтому всё обёрнуто в функцию,
// а работает только последний встроенный экземпляр — предыдущие замолкают сами.
(() => {
  'use strict';

  const CHECK_INTERVAL_MS = 1000; // сверка трека, пока вкладка открыта
  const HEARTBEAT_MS = 5000;      // повтор без изменений: фоновый скрипт жив, виджет узнаёт трек и после своего запуска
  const JUMP_SECONDS = 2;         // позиция ушла дальше этого от ожидаемой — перемотали, сообщаем сразу

  // Подписи элементов плеера (aria-label). Если Яндекс их переименует — поправить здесь.
  const BUTTONS = {
    previous: ['Предыдущая песня', 'Предыдущий трек', 'Previous song'],
    playPause: ['Пауза', 'Воспроизведение', 'Pause', 'Play'],
    next: ['Следующая песня', 'Следующий трек', 'Next song'],
    like: ['Нравится', 'Like'],
  };
  const TIMELINE = ['Управление таймкодом', 'Timecode control'];

  const token = `${Date.now()}-${Math.random()}`;
  window.__edgedockContent = token;

  // Позиция и длина, которые плеер сообщил браузеру; присылает page.js. Пока не сообщил — null.
  let reported = null;
  window.addEventListener('edgedock-position', event => {
    try {
      reported = JSON.parse(event.detail);
    } catch (e) {
      reported = null;
    }
  });
  window.dispatchEvent(new Event('edgedock-position-request'));

  const artworkCache = new Map();
  let lastKey = '';
  let lastSentAt = 0;
  let lastPosition = 0;
  let lastPlaying = false;
  const timer = setInterval(() => report(false), CHECK_INTERVAL_MS);

  // Этот экземпляр ещё главный и расширение не перезагружали.
  function alive() {
    let connected = false;
    try {
      connected = !!chrome.runtime && !!chrome.runtime.id;
    } catch (e) {
      connected = false;
    }
    if (window.__edgedockContent === token && connected) return true;
    clearInterval(timer);
    return false;
  }

  function findIn(root, labels) {
    for (const label of labels) {
      const element = root.querySelector(`[aria-label="${label}"]`);
      if (element) return element;
    }
    return null;
  }

  // Панель плеера: поднимаемся от кнопки «вперёд», пока в контейнере не окажутся и «назад», и «Нравится».
  // Так не спутаем кнопки плеера с кнопками «Воспроизведение» и «Нравится» на обложках и в списках.
  function playerBar() {
    const next = findIn(document, BUTTONS.next);
    let bar = next && next.parentElement;
    for (let depth = 0; bar && depth < 8; depth++, bar = bar.parentElement) {
      if (findIn(bar, BUTTONS.previous) && findIn(bar, BUTTONS.like)) return bar;
    }
    // Кнопки лайка рядом нет — хотя бы общий контейнер «назад» и «вперёд».
    bar = next && next.parentElement;
    while (bar && !findIn(bar, BUTTONS.previous)) bar = bar.parentElement;
    return bar;
  }

  function control(kind) {
    const bar = playerBar();
    return bar ? findIn(bar, BUTTONS[kind]) : null;
  }

  function usable(element) {
    return !!element && !element.disabled && element.getAttribute('aria-disabled') !== 'true';
  }

  // «1:23» или «1:02:03» → секунды.
  function toSeconds(text) {
    const parts = String(text || '').trim().split(':').map(Number);
    if (parts.length === 0 || parts.some(p => Number.isNaN(p))) return NaN;
    return parts.reduce((total, part) => total * 60 + part, 0);
  }

  // Позиция и длина трека в секундах. Главное — то, что плеер сам сообщает браузеру (присылает page.js):
  // так не важно, какая панель плеера на экране. Пока плеер ничего не сообщил (вкладку подхватили на паузе) —
  // ползунок перемотки плеера.
  function timeline(playing) {
    if (reported) {
      const elapsed = playing ? (Date.now() - reported.at) / 1000 * reported.rate : 0;
      return {
        position: Math.min(reported.duration, reported.position + elapsed),
        duration: reported.duration,
        canSeek: reported.canSeek === true,
      };
    }
    const line = slider();
    return line && { position: line.position, duration: line.duration, canSeek: usable(line.input) };
  }

  // Ползунок перемотки плеера. Его шкала — секунды, max — длина трека. В подписи обычно только текущее время
  // («1:23»), а в «Моей волне» — «1:23 / 3:45»; если длина там есть, берём её, а позицию — по положению ползунка.
  function slider() {
    const bar = playerBar();
    const input = (bar && findIn(bar, TIMELINE)) || findIn(document, TIMELINE);
    if (!input) return null;

    const total = toSeconds(String(input.getAttribute('aria-valuetext') || '').split('/')[1]);
    const max = Number(input.max) || 0;
    const value = Number(input.value) || 0;
    const duration = total > 0 ? total : max;
    if (duration <= 0 || max <= 0) return null;
    return { input, position: value / max * duration, duration, scale: max / duration };
  }

  // Перемотка — так же, как медиакнопками браузера (через page.js). Если плеер о себе ещё не сообщил —
  // его же ползунком: ставим значение и сообщаем странице, как при движении мышью.
  function seek(seconds) {
    if (reported && reported.canSeek === true) {
      window.dispatchEvent(new CustomEvent('edgedock-seek', { detail: String(seconds) }));
      return;
    }
    const line = slider();
    if (!line || !usable(line.input)) return;
    const target = Math.max(0, Math.min(line.duration, seconds)) * line.scale;
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setValue.call(line.input, String(target));
    line.input.dispatchEvent(new Event('input', { bubbles: true }));
    line.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Обложка ≈100 px: хватает для 48 DIP в виджете при масштабе 200%.
  function pickArtwork(artwork) {
    if (!artwork || artwork.length === 0) return '';
    const sized = [...artwork].map(a => ({ src: a.src, size: parseInt(String(a.sizes || '').split('x')[0], 10) || 0 }));
    const fitting = sized.filter(a => a.size >= 96).sort((a, b) => a.size - b.size)[0];
    return (fitting || sized[sized.length - 1]).src || '';
  }

  // blob:-адрес виджет открыть не сможет — превращаем такую обложку в data:-адрес.
  async function portableArtwork(src) {
    if (!src.startsWith('blob:')) return src;
    if (artworkCache.has(src)) return artworkCache.get(src);
    let data = '';
    try {
      const blob = await (await fetch(src)).blob();
      if (blob.size < 200000) {
        data = await new Promise(resolve => {
          const reader = new FileReader();
          reader.onload = () => resolve(String(reader.result));
          reader.onerror = () => resolve('');
          reader.readAsDataURL(blob);
        });
      }
    } catch (e) {
      data = '';
    }
    artworkCache.clear();
    artworkCache.set(src, data);
    return data;
  }

  // Главное — трек и кнопки. Дополнения (время, лайк) читаются отдельно: если на странице что-то поменялось
  // и они сломались, трек всё равно показывается.
  async function snapshot() {
    const session = navigator.mediaSession;
    const meta = session && session.metadata;
    if (!meta || !meta.title) return null;

    let playing = session.playbackState === 'playing';
    if (session.playbackState === 'none') {
      const audio = document.querySelector('audio');
      playing = !!audio && !audio.paused;
    }

    let artwork = '';
    try {
      artwork = await portableArtwork(pickArtwork(meta.artwork));
    } catch (e) {
      artwork = '';
    }

    const state = {
      title: meta.title,
      artist: meta.artist || '',
      artwork,
      playing,
      canPrevious: false,
      canPlayPause: false,
      canNext: false,
      position: 0,
      duration: 0,
      canSeek: false,
      liked: false,
      canLike: false,
    };

    try {
      state.canPrevious = usable(control('previous'));
      state.canPlayPause = usable(control('playPause'));
      state.canNext = usable(control('next'));
    } catch (e) { /* кнопки не нашлись — просто неактивны */ }

    try {
      const line = timeline(playing);
      if (line) {
        state.position = line.position;
        state.duration = line.duration;
        state.canSeek = line.canSeek;
      }
    } catch (e) { /* без полоски времени */ }

    try {
      const like = control('like');
      state.liked = !!like && like.getAttribute('aria-pressed') === 'true';
      state.canLike = usable(like);
    } catch (e) { /* без лайка */ }

    return state;
  }

  // Сообщаем, когда что-то изменилось. Позиция сама по себе идёт каждую секунду — из-за неё не пишем,
  // виджет досчитывает её сам. Сообщаем только скачок (перемотка) и раз в HEARTBEAT_MS.
  async function report(force) {
    if (!alive()) return;
    let state = null;
    try {
      state = await snapshot();
    } catch (e) {
      state = null;
    }

    const now = Date.now();
    const { position = 0, ...rest } = state || {};
    const key = JSON.stringify(state ? rest : null);
    const expected = lastPosition + (lastPlaying ? (now - lastSentAt) / 1000 : 0);
    const jumped = !!state && Math.abs(position - expected) > JUMP_SECONDS;
    if (force !== true && key === lastKey && !jumped && now - lastSentAt < HEARTBEAT_MS) return;

    lastKey = key;
    lastSentAt = now;
    lastPosition = position;
    lastPlaying = !!(state && state.playing);
    send({ type: 'state', state: state && { ...state, positionAt: now }, moved: force === true || jumped });
  }

  function send(message) {
    if (!alive()) return;
    try {
      chrome.runtime.sendMessage(message).catch(() => {});
    } catch (e) {
      // Расширение перезагрузили — этот экземпляр скрипта больше не нужен.
      clearInterval(timer);
    }
  }

  // Старт, пауза, перемотка и смена трека — сразу, не дожидаясь очередной сверки.
  for (const type of ['play', 'pause', 'seeked', 'loadedmetadata']) {
    document.addEventListener(type, () => report(true), true);
  }

  // Вкладку закрыли или ушли со страницы — трека больше нет.
  window.addEventListener('pagehide', () => send({ type: 'state', state: null, moved: true }));

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (!message || !alive()) return;
    if (message.type === 'ping') {
      sendResponse(true); // фоновый скрипт проверяет, что во вкладке уже есть рабочий экземпляр
      return;
    }
    if (message.type === 'report') {
      report(true); // фоновый скрипт только что подключился к виджету — прислать состояние сразу
      return;
    }
    if (message.type !== 'command') return;
    try {
      if (message.command === 'seek') {
        seek(Number(message.position) || 0);
      } else if (BUTTONS[message.command]) {
        const target = control(message.command);
        if (usable(target)) target.click();
      }
    } catch (e) { /* элемент плеера не нашёлся — ничего не делаем */ }
    setTimeout(() => report(true), 300);
  });

  report(true);
})();
