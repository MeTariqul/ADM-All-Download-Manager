let activeTab = null;

document.addEventListener('DOMContentLoaded', () => {
  void initialize();
});

async function initialize() {
  activeTab = await getActiveTab();

  const intercept = await sendMessage({ type: 'getIntercept' });
  const toggled = document.getElementById('intercept');
  toggled.checked = !intercept || intercept.intercept !== false;
  toggled.addEventListener('change', onToggleIntercept);

  document.getElementById('refresh').addEventListener('click', () => void loadMedia(true));
  document.getElementById('refreshFiles').addEventListener('click', () => void loadFiles(true));
  document.getElementById('downloadAll').addEventListener('click', () => void downloadAllFiles());
  document.getElementById('downloadVideo').addEventListener('click', () => void downloadVideo());
  document.getElementById('sendPage').addEventListener('click', () => void sendPage());
  document.getElementById('openApp').addEventListener('click', () => void openApp());

  await Promise.all([loadMedia(false), loadFiles(false), checkStatus()]);
}

function sendMessage(message) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(message, (response) => {
        resolve(chrome.runtime.lastError ? null : response);
      });
    } catch (error) {
      resolve(null);
    }
  });
}

function getActiveTab() {
  return new Promise((resolve) => {
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => resolve(tabs && tabs[0]));
  });
}

async function loadMedia(showStatus) {
  if (!activeTab) return;

  if (showStatus) setStatus('Scanning page media…');

  const response = await sendMessage({ type: 'getMedia', tabId: activeTab.id });
  renderMedia(response && response.items ? response.items : []);

  if (showStatus) setStatus('');
}

function renderMedia(items) {
  const container = document.getElementById('media');
  container.textContent = '';

  if (!items.length) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = 'No media detected yet. Start playback, then refresh.';
    container.appendChild(empty);
    return;
  }

  for (const item of items) {
    const row = document.createElement('div');
    row.className = 'media';

    const icon = document.createElement('div');
    icon.className = 'media-icon';
    icon.textContent = (item.contentType || '').startsWith('audio') ? '♪' : '▶';

    const info = document.createElement('div');
    info.className = 'media-info';

    const title = document.createElement('div');
    title.className = 'media-title';
    title.textContent = item.title || 'Media file';
    title.title = item.url;

    const meta = document.createElement('div');
    meta.className = 'media-meta';
    meta.textContent = [typeLabel(item.contentType), formatSize(item.size)].filter(Boolean).join(' · ');

    info.append(title, meta);

    const button = document.createElement('button');
    button.className = 'mini-btn';
    button.textContent = '↓';
    button.title = 'Download with ADM';
    button.addEventListener('click', () => void downloadMedia(item, button));

    row.append(icon, info, button);
    container.appendChild(row);
  }
}

async function downloadMedia(item, button) {
  button.disabled = true;
  setStatus('Sending to ADM…');

  const hasExtension = /\.[a-z0-9]{2,5}$/i.test(item.title || '');
  const response = await sendMessage({
    type: 'downloadMedia',
    url: item.url,
    filename: hasExtension ? item.title : '',
    contentType: item.contentType,
    referrer: item.referrer
  });

  setStatus(response ? response.message : 'Could not reach ADM.', !!(response && response.ok));
  button.disabled = false;
}

// ------------------------------------------------------- file links on page

async function loadFiles(showStatus) {
  if (!activeTab) return;

  if (showStatus) setStatus('Scanning page for file links…');

  const response = await sendMessage({ type: 'getFileLinks', tabId: activeTab.id });
  renderFiles(response && response.items ? response.items : []);

  if (showStatus) setStatus('');
}

function renderFiles(items) {
  const container = document.getElementById('files');
  container.textContent = '';

  if (!items.length) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = 'No file links detected yet.';
    container.appendChild(empty);
    return;
  }

  for (const item of items) {
    const row = document.createElement('div');
    row.className = 'media';

    const icon = document.createElement('div');
    icon.className = 'media-icon';
    icon.textContent = '⬇';

    const info = document.createElement('div');
    info.className = 'media-info';

    const title = document.createElement('div');
    title.className = 'media-title';
    title.textContent = item.title || 'File';
    title.title = item.url;

    const meta = document.createElement('div');
    meta.className = 'media-meta';
    meta.textContent = formatSize(item.size);

    info.append(title, meta);
    row.append(icon, info);
    container.appendChild(row);
  }
}

async function downloadAllFiles() {
  if (!activeTab) return;

  const response = await sendMessage({ type: 'getFileLinks', tabId: activeTab.id });
  const items = response && response.items ? response.items : [];

  if (!items.length) {
    setStatus('No file links were found on this page.', false);
    return;
  }

  setStatus(`Sending ${items.length} links to ADM…`);

  const reply = await sendMessage({
    type: 'downloadMany',
    urls: items.map((item) => item.url),
    referrer: activeTab.url
  });

  setStatus(reply ? reply.message : 'Could not reach ADM.', !!(reply && reply.ok));
}

const VIDEO_SITES = [
  'youtube.com', 'youtu.be', 'vimeo.com', 'dailymotion.com', 'dai.ly',
  'twitch.tv', 'twitter.com', 'x.com', 'facebook.com', 'fb.watch',
  'instagram.com', 'tiktok.com', 'soundcloud.com', 'bilibili.com',
  'reddit.com', 'redd.it', 'streamable.com', 'rumble.com', 'odysee.com'
];

function isVideoSite(url) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    return VIDEO_SITES.some((site) => host === site || host.endsWith('.' + site));
  } catch (error) {
    return false;
  }
}

async function downloadVideo() {
  if (!activeTab || !/^https?:/i.test(activeTab.url || '')) {
    setStatus('This page cannot be sent to ADM.', false);
    return;
  }

  if (!isVideoSite(activeTab.url)) {
    setStatus('This site is not supported for video downloads.', false);
    return;
  }

  setStatus('Opening the format picker…');
  const response = await sendMessage({
    type: 'openVideo',
    url: activeTab.url,
    referrer: activeTab.url
  });

  setStatus(response ? response.message : 'Could not reach ADM.', !!(response && response.ok));
}

async function sendPage() {
  if (!activeTab || !/^https?:/i.test(activeTab.url || '')) {
    setStatus('This page cannot be sent to ADM.', false);
    return;
  }

  setStatus('Sending page link…');
  const response = await sendMessage({
    type: 'downloadPage',
    url: activeTab.url,
    referrer: activeTab.url
  });

  setStatus(response ? response.message : 'Could not reach ADM.', !!(response && response.ok));
}

async function openApp() {
  setStatus('Opening ADM…');
  const response = await sendMessage({ type: 'showApp' });

  if (response && response.ok) {
    setStatus('ADM is opening.');
    return;
  }

  setStatus('ADM is not installed or could not be started.', false);
}

async function onToggleIntercept(event) {
  const enabled = event.target.checked;
  await sendMessage({ type: 'setIntercept', value: enabled });
  setStatus(enabled ? 'Browser downloads now go to ADM.' : 'Browser downloads are left untouched.', true);
}

async function checkStatus() {
  const pill = document.getElementById('connection');
  const response = await sendMessage({ type: 'getStatus' });

  if (response && response.connected) {
    pill.textContent = 'ADM ready';
    pill.className = 'pill ok';
    return;
  }

  // The native host can be reached while ADM itself is not running. The two
  // cases need a different hint, because only one of them is a problem.
  const message = (response && response.message) || '';
  if (/not running/i.test(message)) {
    pill.textContent = 'ADM not running';
    pill.className = 'pill err';
    setStatus('ADM is not running. Use "Open ADM" to start it.', false);
  } else {
    pill.textContent = 'Extension not connected';
    pill.className = 'pill err';
    setStatus('The ADM browser helper is not installed. Reinstall ADM, then reload the extension.', false);
  }
}

function setStatus(text, ok) {
  const element = document.getElementById('status');
  element.textContent = text || '';
  element.className = 'status' + (ok === true ? ' ok' : ok === false ? ' err' : '');
}

function formatSize(bytes) {
  if (!bytes || bytes < 1) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 10 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

function typeLabel(contentType) {
  if (!contentType) return '';
  return contentType.startsWith('audio/') ? 'Audio' : contentType.startsWith('video/') ? 'Video' : contentType;
}
