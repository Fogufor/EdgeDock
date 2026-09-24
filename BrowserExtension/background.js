// EdgeDock, фоновый скрипт.
// Собирает состояние со всех вкладок Яндекс Музыки, выбирает ту, что играет (или играла последней),
// и передаёт её виджету по WebSocket на 127.0.0.1. Обратно получает кнопки плеера.
'use strict';

const WIDGET_URL = 'ws://127.0.0.1:48620/'; // порт должен совпадать с BrowserMusicBridge.Port в виджете
const RETRY_MS = 10000;                     // виджет не запущен — пробуем подключиться не чаще раза в 10 с

const tabs = new Map(); // id вкладки → { state, playedAt }
let socket = null;
let lastForwarded = null;
let lastAttempt = 0;

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message || message.type !== 'state' || !sender.tab) return;
  const entry = tabs.get(sender.tab.id) || { playedAt: 0 };
  entry.state = message.state;
  if (message.state && message.state.playing) entry.playedAt = Date.now();
  tabs.set(sender.tab.id, entry);
  sync();
});

chrome.tabs.onRemoved.addListener(tabId => {
  if (tabs.delete(tabId)) sync();
});

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

function sync() {
  const best = current();
  if (!best && !socket) return; // нечего сообщать, а виджет и так ничего не знает

  connect();
  if (!socket || socket.readyState !== WebSocket.OPEN) return;

  const payload = JSON.stringify(best ? { type: 'state', ...best.entry.state } : { type: 'none' });
  if (payload === lastForwarded) return; // виджету отправляем только изменения
  socket.send(payload);
  lastForwarded = payload;
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  if (Date.now() - lastAttempt < RETRY_MS) return;
  lastAttempt = Date.now();
  const ws = new WebSocket(WIDGET_URL);
  socket = ws;
  ws.onopen = () => {
    lastForwarded = null;
    sync();
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
    const best = current();
    if (message.type === 'command' && best) {
      chrome.tabs.sendMessage(best.id, { type: 'command', command: message.command }).catch(() => {});
    }
  };
}
