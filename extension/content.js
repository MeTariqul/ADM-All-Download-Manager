// Collects media URLs that are rendered in the page. Network responses are
// sniffed by the service worker; this script catches <video>/<audio>/<source>
// elements and direct media links that never trigger a sniffable request.
(() => {
  const reported = new Set();
  const MEDIA_LINK = /\.(mp4|webm|mkv|mov|m4v|mp3|m4a|flac|wav|ogg|opus)(\?|#|$)/i;
  // Links that point at a downloadable file rather than a web page.
  const FILE_LINK = /\.(zip|rar|7z|tar|gz|bz2|xz|exe|msi|apk|dmg|iso|img|pdf|epub|csv|docx?|xlsx?|pptx?|json|xml|yaml|yml|deb|rpm|jar|py|cpp|java|cs|go|rs|sql)(\?|#|$)/i;

  function add(url, found) {
    if (!url || !/^https?:/i.test(url) || reported.has(url)) return;
    reported.add(url);
    found.push(url);
  }

  function scan() {
    const found = [];
    const files = [];

    document.querySelectorAll('video, audio').forEach((element) => {
      add(element.currentSrc, found);
      add(element.src, found);
      element.querySelectorAll('source').forEach((source) => add(source.src, found));
    });

    document.querySelectorAll('a[href]').forEach((anchor) => {
      if (MEDIA_LINK.test(anchor.href)) {
        add(anchor.href, found);
      } else if (FILE_LINK.test(anchor.href)) {
        if (!reported.has(anchor.href)) {
          reported.add(anchor.href);
          files.push(anchor.href);
        }
      }
    });

    if (files.length) {
      try {
        chrome.runtime.sendMessage(
          { type: 'fileLinks', urls: files.slice(0, 100), pageUrl: location.href },
          () => void chrome.runtime.lastError
        );
      } catch (error) {
        // The service worker may be asleep.
      }
    }

    if (found.length === 0) return;

    try {
      chrome.runtime.sendMessage(
        {
          type: 'mediaCandidates',
          urls: found,
          pageUrl: location.href,
          title: document.title
        },
        () => void chrome.runtime.lastError
      );
    } catch (error) {
      // The service worker may be asleep or the extension may have been reloaded.
    }
  }

  scan();
  setInterval(scan, 5000);

  // ------------------------------------------------- in-page download button

  const HOST_ID = 'adm-video-download-host';
  const DIRECT_MEDIA = /\.(mp4|webm|mkv|mov|m4v)(\?|#|$)/i;
  const VIDEO_HOSTS = /(^|\.)(youtube\.com|youtu\.be|vimeo\.com|dailymotion\.com|dai\.ly|twitch\.tv|twitter\.com|x\.com|facebook\.com|fb\.watch|instagram\.com|tiktok\.com|soundcloud\.com|bilibili\.com|reddit\.com|redd\.it|streamable\.com|rumble\.com|odysee\.com)$/i;

  let host = null;
  let button = null;
  let currentVideo = null;
  let resetTimer = 0;
  let observer = null;

  function isSupportedPage() {
    return VIDEO_HOSTS.test(location.hostname);
  }

  function pickVideo() {
    const videos = Array.from(document.querySelectorAll('video')).filter((video) => {
      const rect = video.getBoundingClientRect();
      if (rect.width < 240 || rect.height < 120) return false;
      const style = getComputedStyle(video);
      return style.display !== 'none' && style.visibility !== 'hidden';
    });

    videos.sort((left, right) => {
      const leftPlaying = !left.paused && !left.ended ? 1 : 0;
      const rightPlaying = !right.paused && !right.ended ? 1 : 0;
      if (leftPlaying !== rightPlaying) return rightPlaying - leftPlaying;

      const leftArea = left.clientWidth * left.clientHeight;
      const rightArea = right.clientWidth * right.clientHeight;
      return rightArea - leftArea;
    });

    return videos[0] || null;
  }

  function shouldOffer(video) {
    if (isSupportedPage()) return true;

    const source = video.currentSrc || video.src || '';
    return DIRECT_MEDIA.test(source);
  }

  function overlayParent() {
    const fullscreen = document.fullscreenElement || document.webkitFullscreenElement;
    if (fullscreen && fullscreen.tagName !== 'VIDEO') return fullscreen;
    return document.body;
  }

  function ensureButton() {
    if (host && host.isConnected && button) return;

    host = document.createElement('div');
    host.id = HOST_ID;
    host.style.cssText =
      'position:fixed;z-index:2147483647;pointer-events:none;display:none;overflow:visible;';

    const shadow = host.attachShadow({ mode: 'open' });
    button = document.createElement('button');
    button.type = 'button';
    button.textContent = '↓ Download';
    button.title = 'Download with ADM';
    button.setAttribute('aria-label', 'Download this video with ADM');
    button.style.cssText = [
      'position:absolute',
      'right:14px',
      'bottom:58px',
      'display:flex',
      'align-items:center',
      'padding:8px 12px',
      'border:0',
      'border-radius:8px',
      'background:rgba(17,19,24,0.90)',
      'color:#ffffff',
      'font:600 12px/1 "Segoe UI",system-ui,-apple-system,sans-serif',
      'letter-spacing:0.1px',
      'cursor:pointer',
      'pointer-events:auto',
      'box-shadow:0 3px 14px rgba(0,0,0,0.35)',
      'opacity:0.92',
      'transition:opacity .15s, transform .15s',
      'white-space:nowrap'
    ].join(';');

    button.addEventListener('mouseenter', () => {
      button.style.opacity = '1';
      button.style.transform = 'translateY(-1px)';
    });
    button.addEventListener('mouseleave', () => {
      if (!button.disabled) {
        button.style.opacity = '0.92';
        button.style.transform = 'none';
      }
    });

    // Keep the page's player from treating the click as a play/pause tap.
    ['mousedown', 'pointerdown', 'click'].forEach((type) => {
      button.addEventListener(type, (event) => event.stopPropagation());
    });
    button.addEventListener('click', onDownloadClick);

    shadow.appendChild(button);
    overlayParent().appendChild(host);
  }

  function setButtonState(text, disabled, resetAfterMs) {
    if (!button) return;
    clearTimeout(resetTimer);
    button.textContent = text;
    button.disabled = Boolean(disabled);
    button.style.cursor = disabled ? 'default' : 'pointer';
    button.style.opacity = disabled ? '0.65' : '0.92';

    if (resetAfterMs) {
      resetTimer = setTimeout(() => {
        if (!button) return;
        button.textContent = '↓ Download';
        button.disabled = false;
        button.style.cursor = 'pointer';
        button.style.opacity = '0.92';
        button.style.transform = 'none';
      }, resetAfterMs);
    }
  }

  function onDownloadClick(event) {
    event.preventDefault();
    event.stopPropagation();

    if (!button || button.disabled) return;

    // On a supported site the page URL is what the extractor needs. Elsewhere
    // send the media URL when it is a real file, and the page URL otherwise so
    // yt-dlp's generic extractor still gets a chance.
    const mediaUrl = (currentVideo && (currentVideo.currentSrc || currentVideo.src)) || '';
    const directMedia = /^https?:/i.test(mediaUrl) && /\.(mp4|webm|mkv|mov|m4v)(\?|#|$)/i.test(mediaUrl);
    const url = isSupportedPage() || !directMedia ? location.href : mediaUrl;
    setButtonState('Opening ADM…', true, 0);

    try {
      chrome.runtime.sendMessage(
        {
          type: 'openVideo',
          url,
          title: document.title,
          referrer: location.href
        },
        (response) => {
          const error = chrome.runtime.lastError;
          if (error || !response || !response.ok) {
            setButtonState('ADM is not available', false, 3000);
            if (response && response.message) button.title = response.message;
            return;
          }

          setButtonState('Sent to ADM ✓', false, 2600);
        }
      );
    } catch (error) {
      setButtonState('ADM is not available', false, 3000);
    }
  }

  function updateOverlay() {
    if (!document.body) return;

    const video = pickVideo();
    if (!video || !shouldOffer(video)) {
      if (host) host.style.display = 'none';
      currentVideo = null;
      return;
    }

    if (currentVideo !== video) {
      currentVideo = video;
      setButtonState('↓ Download', false, 0);

      // Players resize themselves; keep the button glued to the video.
      if (observer) {
        observer.disconnect();
        observer = null;
      }

      if (typeof ResizeObserver === 'function') {
        observer = new ResizeObserver(() => updateOverlay());
        observer.observe(video);
      }
    }

    ensureButton();

    const parent = overlayParent();
    if (host.parentNode !== parent) parent.appendChild(host);

    const rect = video.getBoundingClientRect();
    const onScreen = rect.bottom > 0 && rect.right > 0 &&
      rect.top < window.innerHeight && rect.left < window.innerWidth;

    // When the video element itself is fullscreen it is painted in the top
    // layer, so an overlay on the page would be hidden behind it.
    const fullscreen = document.fullscreenElement || document.webkitFullscreenElement;
    const covered = fullscreen === video;

    if (covered || !onScreen || rect.width < 240 || rect.height < 120) {
      host.style.display = 'none';
      return;
    }

    host.style.display = 'block';
    host.style.left = Math.round(rect.left) + 'px';
    host.style.top = Math.round(rect.top) + 'px';
    host.style.width = Math.round(rect.width) + 'px';
    host.style.height = Math.round(rect.height) + 'px';
    button.style.bottom = Math.min(58, Math.max(10, Math.round(rect.height * 0.12))) + 'px';
  }

  document.addEventListener('scroll', updateOverlay, true);
  window.addEventListener('resize', updateOverlay);
  document.addEventListener('fullscreenchange', updateOverlay);
  document.addEventListener('webkitfullscreenchange', updateOverlay);
  setInterval(updateOverlay, 400);
  updateOverlay();
})();
