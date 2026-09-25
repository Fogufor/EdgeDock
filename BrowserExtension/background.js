// EdgeDock, фоновый скрипт.
// Собирает состояние со всех вкладок Яндекс Музыки, выбирает ту, что играет (или играла последней),
// и передаёт её виджету по WebSocket на 127.0.0.1. Обратно получает кнопки, перемотку и лайк.
//
// Чтобы ничего не приходилось делать руками:
// * при каждом запуске встраивается во вкладки Яндекс Музыки, открытые до установки, включения или обновления
//   расширения (браузер сам этого не делает — раньше помогало только F5);
// * называет виджету свою версию; если у виджета расширение новее, по его просьбе перезагружается
//   и перечитывает свои файлы с диска.
'use strict';

const WIDGET_URL = 'ws://127.0.0.1:48620/'; // порт должен совпадать с BrowserMusicBridge.Port в виджете
const RETRY_MS = 10000;                     // виджет не запущен — пробуем подключиться не чаще раза в 10 с
const MUSIC_TABS = [                        // те же адреса, что в content_scripts манифеста
  'https://music.yandex.ru/*',
  'https://music.yandex.com/*',
  'https://music.yandex.by/*',
  'https://music.yandex.kz/*',
  'https://music.yandex.uz/*',
];

const tabs = new Map(); // id вкладки → { state, playedAt }
let socket = null;
let lastKey = null;     // что последним ушло виджету (без позиции — её виджет досчитывает сам)
let lastAttempt = 0;

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message || message.type !== 'state' || !sender.tab) return;
  const entry = tabs.get(sender.tab.id) || { playedAt: 0 };
  entry.state = message.state;
  if (message.state && message.state.playing) entry.playedAt = Date.now();
  tabs.set(sender.tab.id, entry);
  sync(message.moved === true);
});

chrome.tabs.onRemoved.addListener(tabId => {
  if (tabs.delete(tabId)) sync(true);
});

// Вкладки, открытые раньше, чем заработало расширение: если в них нет рабочего скрипта — встраиваем.
async function ensureContentScripts() {
  let open = [];
  try {
    open = await chrome.tabs.query({ url: MUSIC_TABS });
  } catch (e) {
    return;
  }
  for (const tab of open) {
    try {
      await chrome.tabs.sendMessage(tab.id, { type: 'ping' });
    } catch (e) {
      // page.js — в мир самой страницы (позиция и перемотка от плеера), content.js — в мир расширения.
      try {
        await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['page.js'], world: 'MAIN' });
      } catch (e2) { /* без page.js content.js возьмёт время с ползунка плеера */ }
      chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] }).catch(() => {});
    }
  }
}

ensureContentScripts();

// Вкладка, которая играет; если ни одна не играет — та, что играла последней.
function current() {
  let best = null;
  for (const [id, entry] of tabs) {
    if (!entry.state) continue;
    const rank = (entry.state.playing ? 1e15 : 0) + entry.playedAt;
    if (!best || rank > best.rank) best = { id, entry, rank };
  }
  return best;
}

// moved — позиция скачком поменялась (перемотка, пауза, новый трек): отправить, даже если остальное то же.
function sync(moved) {
  const best = current();
  if (!best && !socket) return; // нечего сообщать, а виджет и так ничего не знает

  connect();
  if (!socket || socket.readyState !== WebSocket.OPEN) return;

  const state = best ? best.entry.state : null;
  const { position, positionAt, ...rest } = state || {};
  const key = JSON.stringify({ tab: best && best.id, ...rest });
  if (key === lastKey && !moved) return; // виджету отправляем только изменения
  socket.send(JSON.stringify(state ? { type: 'state', ...state } : { type: 'none' }));
  lastKey = key;
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  if (Date.now() - lastAttempt < RETRY_MS) return;
  lastAttempt = Date.now();
  const ws = new WebSocket(WIDGET_URL);
  socket = ws;
  ws.onopen = () => {
    ws.send(JSON.stringify({ type: 'hello', version: chrome.runtime.getManifest().version }));
    lastKey = null;
    sync(true);
  };
  // Виджет не запущен или закрылся — попробуем снова при следующем сообщении от вкладки.
  ws.onerror = () => {};
  ws.onclose = () => {
    if (socket === ws) socket = null;
  };
  ws.onmessage = event => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch (e) {
      return;
    }
    if (message.type === 'reload') {
      // Виджет обновился и привёз новую версию расширения — перечитываем файлы с диска.
      chrome.runtime.reload();
      return;
    }
    const best = current();
    if (message.type === 'command' && best) {
      chrome.tabs.sendMessage(best.id, { type: 'command', command: message.command, position: message.position }).catch(() => {});
    }
  };
}
