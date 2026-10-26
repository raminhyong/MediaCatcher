(() => {
  const discoveredUrls = new Set();

  const MEDIA_EXTENSION_PATTERN =
    /\.(mp4|m4v|webm|mkv|mov|avi|flv|wmv|ts|mp3|m4a|aac|ogg|oga|opus|wav|flac|jpg|jpeg|png|gif|webp|avif|bmp|svg|m3u8|mpd)(?:[?#]|$)/i;

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
    const aggregated = [
      ...collectPerformanceResources(),
      ...collectVideoElements(),
      ...collectAudioElements(),
      ...collectImageElements(),
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
