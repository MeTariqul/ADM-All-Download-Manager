const HOST_NAME = 'com.adm.downloadmanager';
const MAX_MEDIA_PER_TAB = 30;
const MEDIA_TTL_MS = 2 * 60 * 60 * 1000;

// chrome.storage.session survives service-worker restarts; fall back to local
// storage on browsers that do not expose it.
const sessionArea = chrome.storage.session || chrome.storage.local;

// ---------------------------------------------------------------- utilities

function callApi(fn, ...args) {
  return new Promise((resolve) => {
    try {
      fn(...args, (result) => {
        const error = chrome.runtime.lastError;
        resolve(error ? undefined : result);
      });
    } catch (error) {
      resolve(undefined);
    }
  });
}

async function getStored(keys) {
  return (await callApi(chrome.storage.local.get, keys)) || {};
}

async function setStored(items) {
  await callApi(chrome.storage.local.set, items);
}

async function getSession(key) {
  return (await callApi(sessionArea.get, key)) || {};
}

async function setSession(items) {
  await callApi(sessionArea.set, items);
}

function sendNative(message, timeoutMs = 180000) {
  return new Promise((resolve) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (!settled) {
        settled = true;
        resolve(null);
      }
    }, timeoutMs);

    try {
      chrome.runtime.sendNativeMessage(HOST_NAME, message, (response) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        const error = chrome.runtime.lastError;
        resolve(error ? null : response);
      });
    } catch (error) {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve(null);
      }
    }
  });
}

function setBadge(text, color) {
  try {
    chrome.action.setBadgeBackgroundColor({ color: color || '#087EA4' });
    chrome.action.setBadgeText({ text: text || '' });
    if (text) {
      setTimeout(() => chrome.action.setBadgeText({ text: '' }), 4000);
    }
  } catch (error) {
    // Badge feedback is best-effort.
  }
}

function baseName(path) {
  if (!path) return '';
  const parts = String(path).split(/[\\/]/);
  return parts[parts.length - 1] || '';
}

// The browser's file name without the folders, so ADM can rebuild the same
// name in its own download folder.
function fileNameOnly(path) {
  return baseName(path);
}

// Reads the cookies of a URL so the desktop app can send them with the
// request. Sites behind a login or a CDN token need them.
async function cookiesForUrl(url) {
  try {
    if (!chrome.cookies || !url) return '';
    const cookies = await callApi(chrome.cookies.getAll, { url });
    if (!cookies || !cookies.length) return '';

    return cookies
      .map((cookie) => `${cookie.name}=${cookie.value}`)
      .join('; ');
  } catch (error) {
    // The cookies permission may be missing or the URL may not match.
    return '';
  }
}

function guessTitle(url) {
  try {
    const parsed = new URL(url);
    const last = decodeURIComponent(parsed.pathname.split('/').filter(Boolean).pop() || '');
    return last || parsed.hostname;
  } catch (error) {
    return url;
  }
}

function guessContentType(url) {
  const path = String(url).split('?')[0].toLowerCase();
  if (path.endsWith('.mp4') || path.endsWith('.m4v')) return 'video/mp4';
  if (path.endsWith('.webm')) return 'video/webm';
  if (path.endsWith('.mkv')) return 'video/x-matroska';
  if (path.endsWith('.mp3')) return 'audio/mpeg';
  if (path.endsWith('.m4a')) return 'audio/mp4';
  if (path.endsWith('.flac')) return 'audio/flac';
  if (path.endsWith('.wav')) return 'audio/wav';
  if (path.endsWith('.ogg') || path.endsWith('.opus')) return 'audio/ogg';
  return '';
}

function guessFileName(url, contentType) {
  const name = guessTitle(url);
  if (name && name.includes('.')) return name;

  const type = (contentType || guessContentType(url)).toLowerCase();
  if (type.includes('mp4')) return 'video.mp4';
  if (type.includes('webm')) return 'video.webm';
  if (type.includes('matroska')) return 'video.mkv';
  if (type.includes('mpeg') && type.startsWith('audio')) return 'audio.mp3';
  if (type.includes('audio')) return 'audio.m4a';
  return 'download.bin';
}

function headerValue(headers, name) {
  if (!headers) return '';
  const match = headers.find((header) => header.name.toLowerCase() === name.toLowerCase());
  return match ? match.value || '' : '';
}

function isMediaSegment(url, contentType) {
  const path = String(url).split('?')[0].toLowerCase();
  if (path.endsWith('.ts') || path.endsWith('.m4s') || path.endsWith('.init')) return true;
  if (contentType.includes('mp2t') || contentType.includes('iso.segment')) return true;
  return false;
}

// ------------------------------------------------------------- media store

async function listMedia(tabId) {
  if (tabId === undefined || tabId === null || tabId < 0) return [];
  const key = 'media_' + tabId;
  const stored = await getSession(key);
  const list = Array.isArray(stored[key]) ? stored[key] : [];
  const cutoff = Date.now() - MEDIA_TTL_MS;
  return list.filter((entry) => entry && entry.url && (!entry.foundAt || entry.foundAt >= cutoff));
}

async function addMedia(tabId, entry) {
  if (tabId === undefined || tabId === null || tabId < 0) return;
  if (!entry || !entry.url || !/^https?:/i.test(entry.url)) return;

  const key = 'media_' + tabId;
  const stored = await getSession(key);
  let list = Array.isArray(stored[key]) ? stored[key] : [];

  if (list.some((existing) => existing.url === entry.url)) return;

  list.unshift(entry);
  if (list.length > MAX_MEDIA_PER_TAB) list = list.slice(0, MAX_MEDIA_PER_TAB);
  await setSession({ [key]: list });
}

async function clearMedia(tabId) {
  if (tabId === undefined || tabId === null || tabId < 0) return;
  await callApi(sessionArea.remove, 'media_' + tabId);
}

// -------------------------------------------------------------- file links

async function listFileLinks(tabId) {
  if (tabId === undefined || tabId === null || tabId < 0) return [];
  const key = 'files_' + tabId;
  const stored = await getSession(key);
  const list = Array.isArray(stored[key]) ? stored[key] : [];
  const cutoff = Date.now() - MEDIA_TTL_MS;
  return list.filter((entry) => entry && entry.url && (!entry.foundAt || entry.foundAt >= cutoff));
}

async function addFileLink(tabId, entry) {
  if (tabId === undefined || tabId === null || tabId < 0) return;
  if (!entry || !entry.url || !/^https?:/i.test(entry.url)) return;

  const key = 'files_' + tabId;
  const stored = await getSession(key);
  let list = Array.isArray(stored[key]) ? stored[key] : [];

  if (list.some((existing) => existing.url === entry.url)) return;

  list.unshift(entry);
  if (list.length > MAX_MEDIA_PER_TAB * 4) list = list.slice(0, MAX_MEDIA_PER_TAB * 4);
  await setSession({ [key]: list });
}

async function clearFileLinks(tabId) {
  if (tabId === undefined || tabId === null || tabId < 0) return;
  await callApi(sessionArea.remove, 'files_' + tabId);
}

// --------------------------------------------------------------- downloads

async function shouldIntercept(item) {
  const settings = await getStored({ intercept: true });
  if (settings.intercept === false) return false;
  if (!item || item.byExtensionId) return false;
  if (!/^https?:/i.test(item.url || '')) return false;
  if (item.state && item.state !== 'in_progress') return false;
  return true;
}

// The desktop app owns the setting. The extension keeps a local copy so it
// still works when ADM is not running, and syncs when it is.
async function syncInterceptFromApp() {
  // Only ask the app when it is already running; a status query must never
  // start ADM as a side effect of opening the popup.
  const status = await sendNative({ action: 'PING' }, 3000);
  if (!status || !status.ok) return;

  const response = await sendNative({ action: 'GET_INTERCEPT' }, 3000);
  if (!response || !response.ok) return;

  const enabled = response.message !== '0';
  const settings = await getStored({ intercept: true });
  if (settings.intercept !== enabled) {
    await setStored({ intercept: enabled });
  }
}

async function interceptDownload(item) {
  if (!(await shouldIntercept(item))) return;

  try {
    await callApi(chrome.downloads.pause, item.id);
  } catch (error) {
    // The download may already be finished.
  }

  const cookie = await cookiesForUrl(item.url);

  const response = await sendNative({
    action: 'ADD_DOWNLOAD',
    url: item.url,
    filename: fileNameOnly(item.filename),
    referrer: item.referrer || '',
    userAgent: navigator.userAgent,
    cookie
  });

  if (response && response.ok) {
    await callApi(chrome.downloads.cancel, item.id);
    setBadge('✓', '#3DD68C');
    return;
  }

  // Never lose the user's download if ADM cannot accept it.
  await callApi(chrome.downloads.resume, item.id);
  setBadge('!', '#FF6B6B');
  console.warn('[ADM] Native host did not accept the download:', response);
}

chrome.downloads.onCreated.addListener((item) => {
  void interceptDownload(item);
});

// ---------------------------------------------------------- media sniffing

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0) return;

    const contentType = headerValue(details.responseHeaders, 'content-type').toLowerCase();
    if (!contentType.startsWith('video/') && !contentType.startsWith('audio/')) return;
    if (isMediaSegment(details.url, contentType)) return;

    const size = Number(headerValue(details.responseHeaders, 'content-length')) || 0;
    if (size > 0 && size < 200 * 1024) return;

    void addMedia(details.tabId, {
      url: details.url,
      contentType: contentType.split(';')[0],
      size,
      title: guessTitle(details.url),
      referrer: details.initiator || '',
      foundAt: Date.now()
    });
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other'] },
  ['responseHeaders']
);

chrome.tabs.onRemoved.addListener((tabId) => {
  void clearMedia(tabId);
  void clearFileLinks(tabId);
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === 'loading' && changeInfo.url) {
    void clearMedia(tabId);
    void clearFileLinks(tabId);
  }
});

// ------------------------------------------------------------ context menu

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: 'adm-download-link',
      title: 'Download with ADM',
      contexts: ['link', 'video', 'audio', 'image']
    });
    chrome.contextMenus.create({
      id: 'adm-download-video',
      title: 'Download video with ADM',
      contexts: ['video']
    });
    chrome.contextMenus.create({
      id: 'adm-send-page',
      title: 'Send this page link to ADM',
      contexts: ['page']
    });
    chrome.contextMenus.create({
      id: 'adm-download-all',
      title: 'Download all file links on this page with ADM',
      contexts: ['page']
    });
  });
}

chrome.runtime.onInstalled.addListener(() => {
  createMenus();
  void (async () => {
    const settings = await getStored({ intercept: undefined });
    if (settings.intercept === undefined) {
      await setStored({ intercept: true });
    }
  })();
});

chrome.runtime.onStartup.addListener(createMenus);

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === 'adm-download-all') {
    const tabId = tab && tab.id;
    void (async () => {
      const links = await listFileLinks(tabId);
      if (!links.length) {
        setBadge('!', '#FF6B6B');
        console.warn('[ADM] No file links were found on this page.');
        return;
      }

      const response = await sendNative({
        action: 'ADD_DOWNLOADS',
        urls: links.map((link) => link.url).join('\n'),
        referrer: info.pageUrl || (tab && tab.url) || '',
        userAgent: navigator.userAgent,
        cookie: await cookiesForUrl(links[0].url)
      });

      setBadge(response && response.ok ? '✓' : '!', response && response.ok ? '#3DD68C' : '#FF6B6B');
    })();
    return;
  }

  if (info.menuItemId === 'adm-download-video') {
    const videoPage = info.pageUrl || (tab && tab.url) || '';
    if (!/^https?:/i.test(videoPage)) return;

    void (async () => {
      const response = await sendNative({
        action: 'OPEN_VIDEO',
        url: videoPage,
        referrer: videoPage,
        userAgent: navigator.userAgent
      });

      setBadge(response && response.ok ? '✓' : '!', response && response.ok ? '#3DD68C' : '#FF6B6B');
    })();
    return;
  }

  const url = info.linkUrl || info.srcUrl || info.pageUrl || (tab && tab.url) || '';
  if (!/^https?:/i.test(url)) return;

  void (async () => {
    const response = await sendNative({
      action: 'ADD_DOWNLOAD',
      url,
      filename: baseName(url.split('?')[0]),
      referrer: info.pageUrl || (tab && tab.url) || '',
      userAgent: navigator.userAgent,
      cookie: await cookiesForUrl(url)
    });

    setBadge(response && response.ok ? '✓' : '!', response && response.ok ? '#3DD68C' : '#FF6B6B');
  })();
});

// ---------------------------------------------------------------- messages

async function downloadMedia(message) {
  if (!message || !message.url) return { ok: false, message: 'No media URL was supplied.' };

  const cookie = await cookiesForUrl(message.url);

  const response = await sendNative({
    action: 'ADD_DOWNLOAD',
    url: message.url,
    filename: message.filename || guessFileName(message.url, message.contentType),
    referrer: message.referrer || '',
    userAgent: navigator.userAgent,
    cookie
  });

  if (!response) return { ok: false, message: 'ADM did not respond. Is the native host installed?' };
  return response.ok
    ? { ok: true, message: 'Queued in ADM.' }
    : { ok: false, message: response.message || 'ADM rejected the download.' };
}

// Queues several links in one go, for example every file link on a page.
async function downloadMany(message) {
  const urls = Array.isArray(message.urls) ? message.urls.filter((url) => /^https?:/i.test(url || '')) : [];
  if (!urls.length) return { ok: false, message: 'No links were supplied.' };

  const cookie = await cookiesForUrl(urls[0]);

  const response = await sendNative({
    action: 'ADD_DOWNLOADS',
    urls: urls.join('\n'),
    referrer: message.referrer || '',
    userAgent: navigator.userAgent,
    cookie
  });

  if (!response) return { ok: false, message: 'ADM did not respond. Is the native host installed?' };
  return response.ok
    ? { ok: true, message: response.message || `${urls.length} links sent to ADM.` }
    : { ok: false, message: response.message || 'ADM rejected the links.' };
}

async function openVideo(message) {
  if (!message || !message.url) return { ok: false, message: 'No video URL was supplied.' };

  const response = await sendNative({
    action: 'OPEN_VIDEO',
    url: message.url,
    referrer: message.referrer || message.url,
    userAgent: navigator.userAgent
  });

  if (!response) return { ok: false, message: 'ADM did not respond. Is the native host installed?' };
  return response.ok
    ? { ok: true, message: 'Opening the ADM format picker.' }
    : { ok: false, message: response.message || 'ADM could not open this video.' };
}

async function downloadPage(message) {
  if (!message || !message.url) return { ok: false, message: 'No page URL was supplied.' };

  const response = await sendNative({
    action: 'ADD_DOWNLOAD',
    url: message.url,
    filename: '',
    referrer: message.referrer || message.url,
    userAgent: navigator.userAgent
  });

  if (!response) return { ok: false, message: 'ADM did not respond. Is the native host installed?' };
  return response.ok
    ? { ok: true, message: 'Sent to ADM.' }
    : { ok: false, message: response.message || 'ADM rejected the link.' };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== 'object') return undefined;

  switch (message.type) {
    case 'mediaCandidates':
      void (async () => {
        const tabId = sender.tab && sender.tab.id;
        const urls = Array.isArray(message.urls) ? message.urls.slice(0, 10) : [];
        for (const url of urls) {
          await addMedia(tabId, {
            url,
            contentType: guessContentType(url),
            size: 0,
            title: message.title || guessTitle(url),
            referrer: message.pageUrl || '',
            foundAt: Date.now()
          });
        }
      })();
      return false;

    case 'fileLinks':
      void (async () => {
        const tabId = sender.tab && sender.tab.id;
        const urls = Array.isArray(message.urls) ? message.urls.slice(0, 100) : [];
        for (const url of urls) {
          await addFileLink(tabId, {
            url,
            title: guessTitle(url),
            size: 0,
            referrer: message.pageUrl || '',
            foundAt: Date.now()
          });
        }
      })();
      return false;

    case 'getFileLinks':
      void listFileLinks(message.tabId).then((items) => sendResponse({ items }));
      return true;

    case 'downloadMany':
      void downloadMany(message).then(sendResponse);
      return true;

    case 'getMedia':
      void listMedia(message.tabId).then((items) => sendResponse({ items }));
      return true;

    case 'downloadMedia':
      void downloadMedia(message).then(sendResponse);
      return true;

    case 'downloadPage':
      void downloadPage(message).then(sendResponse);
      return true;

    case 'openVideo':
      void openVideo(message).then(sendResponse);
      return true;

    case 'getIntercept':
      void syncInterceptFromApp().then(() =>
        getStored({ intercept: true }).then((settings) => sendResponse({ intercept: settings.intercept !== false })));
      return true;

    case 'setIntercept':
      void (async () => {
        await setStored({ intercept: message.value !== false });
        // The desktop app keeps the master setting, so tell it as well.
        await sendNative({ action: 'SET_INTERCEPT', intercept: message.value !== false }, 3000);
        sendResponse({ ok: true });
      })();
      return true;

    case 'getStatus':
      void sendNative({ action: 'PING' }, 3000).then((reply) => {
        sendResponse({ connected: !!(reply && reply.ok), message: reply ? reply.message : 'Native host not reachable' });
      });
      return true;

    case 'showApp':
      void sendNative({ action: 'SHOW' }).then((reply) => sendResponse(reply || { ok: false }));
      return true;

    default:
      return undefined;
  }
});
