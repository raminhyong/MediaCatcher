(() => {
  const discoveredUrls = new Set();

  const MEDIA_EXTENSION_PATTERN =
    /\.(mp4|m4v|webm|mkv|mov|avi|flv|wmv|ts|mp3|m4a|aac|ogg|oga|opus|wav|flac|jpg|jpeg|png|gif|webp|avif|bmp|svg|m3u8|mpd)(?:[?#]|$)/i;

  const DOCUMENT_HOSTS = [
    'anyflip.com', 'fliphtml5.com', 'pubhtml5.com', 'heyzine.com', 'publuu.com',
    'flowpaper.com', 'flippingbook.com', 'issuu.com', 'paperturn.com'
  ];

  function normalizeUrl(candidateUrl) {
    if (!candidateUrl) return '';
    try {
      const resolved = new URL(candidateUrl, window.location.href);
      if (resolved.protocol !== 'http:' && resolved.protocol !== 'https:') return '';
      return resolved.href;
    } catch (_error) {
      return '';
    }
  }

  function parseFilenameFromUrl(resourceLocation) {
    try {
      const finalSegment = new URL(resourceLocation).pathname.split('/').filter(Boolean).pop();
      if (finalSegment && finalSegment.includes('.')) {
        return decodeURIComponent(finalSegment);
      }
    } catch (_error) {
      // ignore
    }
    return `media_${Date.now()}`;
  }

  function createMediaRecord(mediaUrl, mediaCategory, detectedMime) {
    return {
      sourceUrl: mediaUrl,
      mediaCategory: mediaCategory,
      mimeType: detectedMime,
      fileSizeBytes: 0,
      fileName: parseFilenameFromUrl(mediaUrl),
      pageUrl: window.location.href,
      detectedVia: 'dom',
      discoveredAt: Date.now()
    };
  }

  function hostMatches(hostname, suffix) {
    const normalized = (hostname || '').toLowerCase();
    return normalized === suffix || normalized.endsWith('.' + suffix);
  }

  function isPageMediaUrl(pageUrl) {
    if (!pageUrl) return false;
    let parsed;
    try {
      parsed = new URL(pageUrl);
    } catch (_error) {
      return false;
    }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;

    const host = parsed.hostname.toLowerCase();
    const path = parsed.pathname;
    const segmentCount = path.split('/').filter(Boolean).length;

    if (hostMatches(host, 'youtu.be')) return segmentCount > 0;

    if (hostMatches(host, 'youtube.com') || hostMatches(host, 'youtube-nocookie.com')) {
      if (path === '/watch' && parsed.searchParams.has('v')) return true;
      return /^\/(shorts|live|embed)\//.test(path);
    }

    if (hostMatches(host, 'vimeo.com')) return /^\/\d+/.test(path);
    if (hostMatches(host, 'dailymotion.com')) return /^\/video\//.test(path);
    if (hostMatches(host, 'twitch.tv')) return /^\/videos\//.test(path);
    if (hostMatches(host, 'soundcloud.com')) return segmentCount >= 2;
    if (hostMatches(host, 'bandcamp.com')) return /^\/(track|album)\//.test(path);
    if (hostMatches(host, 'bilibili.com')) return /^\/video\//.test(path);
    if (hostMatches(host, 'nicovideo.jp')) return /^\/watch\//.test(path);
    if (hostMatches(host, 'rumble.com')) return segmentCount >= 2;
    if (hostMatches(host, 'streamable.com')) return segmentCount > 0;

    return false;
  }

  function pageDisplayTitle() {
    const raw = (document.title || '').trim();
    if (!raw) return '';
    return raw
      .replace(/\s*[-–|]\s*(YouTube|Vimeo|Dailymotion|Twitch|SoundCloud|Bandcamp|bilibili|Rumble|Streamable)\s*$/i, '')
      .trim();
  }

  function collectPageUrlRecord() {
    const pageUrl = normalizeUrl(window.location.href);
    if (!pageUrl || !isPageMediaUrl(pageUrl)) return null;
    if (discoveredUrls.has(pageUrl)) return null;

    discoveredUrls.add(pageUrl);
    const record = createMediaRecord(pageUrl, 'video', '');
    record.fileName = pageDisplayTitle() || `video_${Date.now()}`;
    record.detectedVia = 'page-url';
    return record;
  }

  function isDocumentPageUrl(pageUrl) {
    if (!pageUrl) return false;
    let parsed;
    try {
      parsed = new URL(pageUrl);
    } catch (_error) {
      return false;
    }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
    const host = parsed.hostname.toLowerCase();
    return DOCUMENT_HOSTS.some((suffix) => hostMatches(host, suffix));
  }

  function collectDocumentPageUrlRecord() {
    const pageUrl = normalizeUrl(window.location.href);
    if (!pageUrl || !isDocumentPageUrl(pageUrl)) return null;
    if (discoveredUrls.has(pageUrl)) return null;

    discoveredUrls.add(pageUrl);
    const record = createMediaRecord(pageUrl, 'document', '');
    record.fileName = (document.title || '').trim() || `document_${Date.now()}`;
    record.detectedVia = 'page-url';
    return record;
  }

  function collectVideoElements() {
    const collected = [];
    document.querySelectorAll('video').forEach((videoElement) => {
      const candidates = [];
      const primarySrc = videoElement.currentSrc || videoElement.src;
      if (primarySrc) candidates.push({ src: primarySrc, type: '' });
      videoElement.querySelectorAll('source').forEach((sourceElement) => {
        if (sourceElement.src) {
          candidates.push({ src: sourceElement.src, type: sourceElement.type || '' });
        }
      });

      candidates.forEach((candidate) => {
        const videoSrc = normalizeUrl(candidate.src);
        if (!videoSrc || discoveredUrls.has(videoSrc)) return;
        discoveredUrls.add(videoSrc);
        collected.push(createMediaRecord(videoSrc, 'video', candidate.type));
      });
    });
    return collected;
  }

  function collectAudioElements() {
    const collected = [];
    document.querySelectorAll('audio').forEach((audioElement) => {
      const audioSrc = normalizeUrl(audioElement.currentSrc || audioElement.src);
      if (!audioSrc || discoveredUrls.has(audioSrc)) return;
      discoveredUrls.add(audioSrc);
      collected.push(createMediaRecord(audioSrc, 'audio', ''));
    });
    return collected;
  }

  function collectImageElements() {
    const collected = [];
    document.querySelectorAll('img').forEach((imageElement) => {
      const imageSrc = normalizeUrl(imageElement.currentSrc || imageElement.src);
      if (!imageSrc || discoveredUrls.has(imageSrc)) return;
      if (imageElement.naturalWidth > 0 && imageElement.naturalWidth < 120) return;
      discoveredUrls.add(imageSrc);
      collected.push(createMediaRecord(imageSrc, 'image', ''));
    });
    return collected;
  }

  function collectLinkedFiles() {
    const collected = [];
    document.querySelectorAll('a[href]').forEach((anchorElement) => {
      const anchorHref = normalizeUrl(anchorElement.getAttribute('href'));
      if (!anchorHref || discoveredUrls.has(anchorHref)) return;
      if (!MEDIA_EXTENSION_PATTERN.test(anchorHref)) return;
      discoveredUrls.add(anchorHref);
      const extension = anchorHref.split(/[?#]/)[0].split('.').pop().toLowerCase();
      const category = /^(m3u8|mpd)$/.test(extension) ? 'stream' : 'file';
      collected.push(createMediaRecord(anchorHref, category, ''));
    });
    return collected;
  }

  function collectPerformanceResources() {
    const collected = [];
    let entries = [];
    try {
      entries = performance.getEntriesByType('resource');
    } catch (_error) {
      return collected;
    }

    for (const entry of entries) {
      if (collected.length >= 250) break;
      const resourceUrl = normalizeUrl(entry.name);
      if (!resourceUrl || discoveredUrls.has(resourceUrl)) continue;
      if (!MEDIA_EXTENSION_PATTERN.test(resourceUrl)) continue;

      const extension = resourceUrl.split(/[?#]/)[0].split('.').pop().toLowerCase();
      let category = 'file';
      if (/^(m3u8|mpd)$/.test(extension)) category = 'stream';
      else if (/^(mp4|m4v|webm|mkv|mov|avi|flv|wmv|ts)$/.test(extension)) category = 'video';
      else if (/^(mp3|m4a|aac|ogg|oga|opus|wav|flac)$/.test(extension)) category = 'audio';
      else if (/^(jpg|jpeg|png|gif|webp|avif|bmp|svg)$/.test(extension)) category = 'image';

      discoveredUrls.add(resourceUrl);
      const record = createMediaRecord(resourceUrl, category, '');
      record.fileSizeBytes = entry.encodedBodySize || entry.transferSize || 0;
      record.detectedVia = 'performance';
      collected.push(record);
    }

    return collected;
  }

  function scanDomMediaResources() {
    const pageRecord = collectPageUrlRecord();
    const documentRecord = collectDocumentPageUrlRecord();
    const isDocument = Boolean(documentRecord);

    const aggregated = [
      ...(pageRecord ? [pageRecord] : []),
      ...(documentRecord ? [documentRecord] : []),
      ...(isDocument ? [] : collectPerformanceResources()),
      ...collectVideoElements(),
      ...collectAudioElements(),
      ...(isDocument ? [] : collectImageElements()),
      ...collectLinkedFiles()
    ];

    if (aggregated.length === 0) return;

    try {
      chrome.runtime.sendMessage({
        actionType: 'INGEST_DOM_MEDIA',
        mediaList: aggregated
      });
    } catch (_error) {
      // extension context invalidated
    }
  }

  let throttleTimer = null;
  const domObserver = new MutationObserver(() => {
    if (throttleTimer) return;
    throttleTimer = setTimeout(() => {
      scanDomMediaResources();
      throttleTimer = null;
    }, 1500);
  });

  function observeMutations() {
    const observerTarget = document.body || document.documentElement;
    if (!observerTarget) return;
    domObserver.observe(observerTarget, { childList: true, subtree: true });
  }

  function startScanning() {
    observeMutations();
    scanDomMediaResources();
  }

  try {
    chrome.runtime.sendMessage({ actionType: 'PAGE_READY' }, () => {
      void chrome.runtime.lastError;
    });
  } catch (_error) {
    // extension context invalidated
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startScanning, { once: true });
  } else {
    startScanning();
  }

  window.addEventListener('load', () => scanDomMediaResources(), { once: true });

  chrome.runtime.onMessage.addListener((incomingMessage, _sender, sendResponseCallback) => {
    if (!incomingMessage || incomingMessage.actionType !== 'RESCAN_PAGE') return false;
    scanDomMediaResources();
    sendResponseCallback({ rescanStarted: true });
    return true;
  });
})();
