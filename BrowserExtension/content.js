// EdgeDock, вкладка Яндекс Музыки.
// Читает, что играет, из Media Session (её заполняет сам плеер Яндекс Музыки) и отдаёт фоновому скрипту.
// Кнопки из виджета выполняет нажатием кнопок плеера на странице.
'use strict';

const CHECK_INTERVAL_MS = 1000; // сверка трека, пока вкладка открыта
const HEARTBEAT_MS = 5000;      // повтор без изменений: фоновый скрипт жив, виджет узнаёт трек и после своего запуска

// Подписи кнопок плеера (aria-label). Если Яндекс их переименует — поправить здесь.
const BUTTONS = {
  previous: ['Предыдущая песня', 'Предыдущий трек', 'Previous song'],
  playPause: ['Пауза', 'Воспроизведение', 'Pause', 'Play'],
  next: ['Следующая песня', 'Следующий трек', 'Next song'],
};

const artworkCache = new Map();
let lastJson = '';
let lastSentAt = 0;
let timer = setInterval(report, CHECK_INTERVAL_MS);

// Панель управления плеером: ближайший общий контейнер кнопок «назад» и «вперёд».
// Так не спутаем кнопку плеера с кнопками «Воспроизведение» на обложках альбомов.
function playerControls() {
  const next = findIn(document, BUTTONS.next);
  let group = next && next.parentElement;
  while (group && !findIn(group, BUTTONS.previous)) group = group.parentElement;
  return group;
}

function findIn(root, labels) {
  for (const label of labels) {
    const element = root.querySelector(`[aria-label="${label}"]`);
    if (element) return element;
  }
  return null;
}

function button(kind) {
  const controls = playerControls();
  return controls ? findIn(controls, BUTTONS[kind]) : null;
}

function usable(element) {
  return !!element && !element.disabled && element.getAttribute('aria-disabled') !== 'true';
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

  return {
    title: meta.title,
    artist: meta.artist || '',
    artwork: await portableArtwork(pickArtwork(meta.artwork)),
    playing,
    canPrevious: usable(button('previous')),
    canPlayPause: usable(button('playPause')),
    canNext: usable(button('next')),
  };
}

async function report(force) {
  let state = null;
  try {
    state = await snapshot();
  } catch (e) {
    state = null;
  }
  const json = JSON.stringify(state);
  const now = Date.now();
  if (force !== true && json === lastJson && now - lastSentAt < HEARTBEAT_MS) return;
  lastJson = json;
  lastSentAt = now;
  send({ type: 'state', state });
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

// Старт, пауза и смена трека — сразу, не дожидаясь очередной сверки.
for (const type of ['play', 'pause', 'loadedmetadata']) {
  document.addEventListener(type, () => report(true), true);
}

// Вкладку закрыли или ушли со страницы — трека больше нет.
window.addEventListener('pagehide', () => send({ type: 'state', state: null }));

chrome.runtime.onMessage.addListener(message => {
  if (!message || message.type !== 'command' || !BUTTONS[message.command]) return;
  const target = button(message.command);
  if (usable(target)) target.click();
  setTimeout(() => report(true), 300);
});

report(true);
