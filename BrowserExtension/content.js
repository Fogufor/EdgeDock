// EdgeDock, вкладка Яндекс Музыки.
// Читает, что играет, из Media Session (её заполняет сам плеер Яндекс Музыки), позицию — из полоски
// перемотки плеера, лайк — из кнопки «Нравится», и отдаёт это фоновому скрипту.
// Кнопки, перемотку и лайк из виджета выполняет на странице так же, как если бы нажали вы.
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

const artworkCache = new Map();
let lastKey = '';
let lastSentAt = 0;
let lastPosition = 0;
let lastPlaying = false;
let timer = setInterval(report, CHECK_INTERVAL_MS);

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

// Полоска перемотки плеера: позиция и длина трека в секундах. Длину берём из подписи «1:23 / 3:45»,
// а точную позицию — из положения ползунка (его шкала может быть и в секундах, и в миллисекундах).
function timeline() {
  const bar = playerBar();
  const input = (bar && findIn(bar, TIMELINE)) || findIn(document, TIMELINE);
  if (!input) return null;

  const [shown, total] = String(input.getAttribute('aria-valuetext') || '').split('/').map(toSeconds);
  const max = Number(input.max) || 0;
  const value = Number(input.value) || 0;
  const duration = total > 0 ? total : 0;
  if (duration <= 0) return null;

  const position = max > 0 ? value / max * duration : (shown >= 0 ? shown : 0);
  return { input, position, duration, scale: max > 0 ? max / duration : 1 };
}

// Перемотка тем же ползунком плеера: ставим значение и сообщаем странице, как при движении мышью.
function seek(seconds) {
  const line = timeline();
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

async function snapshot() {
  const session = navigator.mediaSession;
  const meta = session && session.metadata;
  if (!meta || !meta.title) return null;

  let playing = session.playbackState === 'playing';
  if (session.playbackState === 'none') {
    const audio = document.querySelector('audio');
    playing = !!audio && !audio.paused;
  }

  const line = timeline();
  const like = control('like');
  return {
    title: meta.title,
    artist: meta.artist || '',
    artwork: await portableArtwork(pickArtwork(meta.artwork)),
    playing,
    canPrevious: usable(control('previous')),
    canPlayPause: usable(control('playPause')),
    canNext: usable(control('next')),
    position: line ? line.position : 0,
    duration: line ? line.duration : 0,
    canSeek: !!line && usable(line.input),
    liked: !!like && like.getAttribute('aria-pressed') === 'true',
    canLike: usable(like),
  };
}

// Сообщаем, когда что-то изменилось. Позиция сама по себе идёт каждую секунду — из-за неё не пишем,
// виджет досчитывает её сам. Сообщаем только скачок (перемотка) и раз в HEARTBEAT_MS.
async function report(force) {
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
  try {
    chrome.runtime.sendMessage(message).catch(() => {});
  } catch (e) {
    // Расширение перезагрузили — этот экземпляр скрипта больше не нужен.
    clearInterval(timer);
    timer = null;
  }
}

// Старт, пауза, перемотка и смена трека — сразу, не дожидаясь очередной сверки.
for (const type of ['play', 'pause', 'seeked', 'loadedmetadata']) {
  document.addEventListener(type, () => report(true), true);
}

// Вкладку закрыли или ушли со страницы — трека больше нет.
window.addEventListener('pagehide', () => send({ type: 'state', state: null, moved: true }));

chrome.runtime.onMessage.addListener(message => {
  if (!message || message.type !== 'command') return;
  if (message.command === 'seek') {
    seek(Number(message.position) || 0);
  } else if (BUTTONS[message.command]) {
    const target = control(message.command);
    if (usable(target)) target.click();
  }
  setTimeout(() => report(true), 300);
});

report(true);
