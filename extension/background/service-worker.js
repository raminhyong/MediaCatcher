const HOST_NAME = 'com.mediacatcher.host';

const MEDIA_MIME_PATTERNS = [
  /^video\//i,
  /^audio\//i,
  /^image\/(png|jpe?g|webp|gif|svg\+xml|avif|bmp|tiff|x-icon|vnd\.microsoft\.icon|jxl)/i,
  /application\/(x-mpegurl|vnd\.apple\.mpegurl|dash\+xml)/i
];

const STREAM_MIME_PATTERN = /(mpegurl|dash\+xml)/i;

const EXTENSION_MIME_MAP = {
  mp4: 'video/mp4',
  webm: 'video/webm',
  mkv: 'video/x-matroska',
  mov: 'video/quicktime',
  m4v: 'video/mp4',
  ts: 'video/mp2t',
  mp3: 'audio/mpeg',
  m4a: 'audio/mp4',
  aac: 'audio/aac',
  ogg: 'audio/ogg',
  oga: 'audio/ogg',
  opus: 'audio/opus',
  wav: 'audio/wav',
  flac: 'audio/flac',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  gif: 'image/gif',
  webp: 'image/webp',
  avif: 'image/avif',
  bmp: 'image/bmp',
  svg: 'image/svg+xml',
  m3u8: 'application/x-mpegURL',
  mpd: 'application/dash+xml'
};

const EXCLUDED_HOSTS = new Set([
  'google-analytics.com',
  'www.google-analytics.com',
  'doubleclick.net',
  'googletagmanager.com',
  'stats.g.doubleclick.net'
]);

const catalogStore = new Map();
const loadedTabs = new Set();
const tabQueues = new Map();

function catalogKey(tabId) {
  return `catalog:${tabId}`;
}

function queueTabTask(tabId, task) {
  const previous = tabQueues.get(tabId) || Promise.resolve();
  const run = previous.then(() => task());
  tabQueues.set(tabId, run.then(() => undefined, () => undefined));
  return run;
}

function extractHeaderValue(headersList, headerTarget) {
  if (!Array.isArray(headersList)) return '';
  const matched = headersList.find(
    (record) => record.name.toLowerCase() === headerTarget.toLowerCase()
  );
  return matched ? matched.value : '';
}

function getUrlExtension(sourceUrl) {
  try {
    const pathname = new URL(sourceUrl).pathname;
    const lastSegment = pathname.split('/').pop() || '';
    if (!lastSegment.includes('.')) return '';
    return lastSegment.split('.').pop().toLowerCase();
  } catch (_error) {
    return '';
  }
}

function classifyMimeCategory(mimeTypeString) {
  const normalized = (mimeTypeString || '').toLowerCase();
  if (normalized.startsWith('video/')) return 'video';
  if (normalized.startsWith('audio/')) return 'audio';
  if (normalized.startsWith('image/')) return 'image';
  if (STREAM_MIME_PATTERN.test(normalized)) return 'stream';
  return 'media';
}

function deriveFilenameFromUrl(sourceUrl, mimeTypeString) {
  try {
    const segment = new URL(sourceUrl).pathname.split('/').filter(Boolean).pop();
    if (segment && segment.includes('.')) {
      return decodeURIComponent(segment);
    }
  } catch (_parseError) {
    // fall through
  }
  const extension = getUrlExtension(sourceUrl) || (mimeTypeString.split('/')[1] || 'bin').split(';')[0];
  return `media_${Date.now()}.${extension}`;
}

function shouldTrackMedia(sourceUrl, mimeTypeString, byteLength) {
  if (!sourceUrl || !/^https?:/i.test(sourceUrl)) return false;

  let parsed;
  try {
    parsed = new URL(sourceUrl);
  } catch (_err) {
    return false;
  }
  if (EXCLUDED_HOSTS.has(parsed.hostname)) return false;

  if (/^image\//i.test(mimeTypeString) && byteLength > 0 && byteLength < 8192) {
    return false;
  }
  return MEDIA_MIME_PATTERNS.some((matcher) => matcher.test(mimeTypeString));
}

function resolveMime(requestMime, sourceUrl, byteLength) {
  const normalized = (requestMime || '').split(';')[0].trim().toLowerCase();
  if (normalized && normalized !== 'application/octet-stream' && normalized !== 'binary/octet-stream') {
    return normalized;
  }
  const extension = getUrlExtension(sourceUrl);
  const guessed = EXTENSION_MIME_MAP[extension];
  if (guessed && byteLength >= 8192) return guessed;
  return normalized || guessed || '';
}

function updateBadgeDisplay(targetTabId, mediaCount) {
  if (targetTabId < 0) return;
  const text = mediaCount > 0 ? (mediaCount > 99 ? '99+' : String(mediaCount)) : '';
  chrome.action.setBadgeText({ tabId: targetTabId, text });
  chrome.action.setBadgeBackgroundColor({ tabId: targetTabId, color: '#2563eb' });
}

async function ensureCatalogLoaded(tabId) {
  if (loadedTabs.has(tabId)) return;
  let storedRecords = [];
  try {
    const storedBag = await chrome.storage.session.get(catalogKey(tabId));
    const storedValue = storedBag[catalogKey(tabId)];
    if (Array.isArray(storedValue)) storedRecords = storedValue;
  } catch (_err) {
    storedRecords = [];
  }
  const catalog = catalogStore.get(tabId) || new Map();
  storedRecords.forEach((record) => {
    if (record && record.sourceUrl && !catalog.has(record.sourceUrl)) {
      catalog.set(record.sourceUrl, record);
    }
  });
  catalogStore.set(tabId, catalog);
  loadedTabs.add(tabId);
}

async function persistCatalog(tabId) {
  const catalog = catalogStore.get(tabId);
  if (!catalog) return;
  try {
    await chrome.storage.session.set({ [catalogKey(tabId)]: Array.from(catalog.values()) });
  } catch (_err) {
    // in-memory catalog still works
  }
}

function buildRecord(requestUrl, mimeType, byteLength) {
  return {
    sourceUrl: requestUrl,
    mediaCategory: classifyMimeCategory(mimeType),
    mimeType,
    fileSizeBytes: byteLength,
    fileName: deriveFilenameFromUrl(requestUrl, mimeType),
    detectedVia: 'network',
    discoveredAt: Date.now()
  };
}

function mergeRecord(existing, candidate) {
  let changed = false;
  const candidateFromNetwork = candidate.detectedVia === 'network';
  const existingFromNetwork = existing.detectedVia === 'network';

  if (candidate.fileSizeBytes > 0 && candidate.fileSizeBytes !== existing.fileSizeBytes) {
    existing.fileSizeBytes = candidate.fileSizeBytes;
    changed = true;
  }

  if (candidateFromNetwork && (candidate.mimeType || candidate.mediaCategory)) {
    if (candidate.mimeType && candidate.mimeType !== existing.mimeType) {
      existing.mimeType = candidate.mimeType;
      changed = true;
    }
    if (candidate.mediaCategory && candidate.mediaCategory !== existing.mediaCategory) {
      existing.mediaCategory = candidate.mediaCategory;
      changed = true;
    }
    if (!existingFromNetwork) {
      existing.detectedVia = 'network';
      changed = true;
    }
  } else if (!existing.mimeType && candidate.mimeType) {
    existing.mimeType = candidate.mimeType;
    changed = true;
  }

  return changed;
}

function recordDiscoveredMedia(tabId, candidates) {
  if (tabId < 0 || candidates.length === 0) return Promise.resolve();
  return queueTabTask(tabId, async () => {
    await ensureCatalogLoaded(tabId);
    const catalog = catalogStore.get(tabId);
    let changed = false;

    candidates.forEach((candidate) => {
      if (!candidate || !candidate.sourceUrl) return;
      const existing = catalog.get(candidate.sourceUrl);
      if (existing) {
        if (mergeRecord(existing, candidate)) changed = true;
        return;
      }
      catalog.set(candidate.sourceUrl, candidate);
      changed = true;
    });

    if (!changed) return catalog.size;
    await persistCatalog(tabId);
    updateBadgeDisplay(tabId, catalog.size);
    return catalog.size;
  });
}

chrome.webRequest.onHeadersReceived.addListener(
  (requestDetails) => {
    const { tabId, url: resourceUrl, responseHeaders, statusCode } = requestDetails;
    if (tabId < 0 || !resourceUrl) return;
    if (statusCode && statusCode >= 400) return;

    const rawMime = extractHeaderValue(responseHeaders, 'content-type');
    const contentLengthRaw = extractHeaderValue(responseHeaders, 'content-length');
    const totalBytes = contentLengthRaw ? parseInt(contentLengthRaw, 10) || 0 : 0;
    const resolvedMime = resolveMime(rawMime, resourceUrl, totalBytes);

    if (!resolvedMime || !shouldTrackMedia(resourceUrl, resolvedMime, totalBytes)) return;
    recordDiscoveredMedia(tabId, [buildRecord(resourceUrl, resolvedMime, totalBytes)]);
  },
  { urls: ['<all_urls>'] },
  ['responseHeaders']
);

function sendNativeMessage(message) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(HOST_NAME, message, (response) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: chrome.runtime.lastError.message });
          return;
        }
        resolve(response || { ok: false, error: 'no response' });
      });
    } catch (error) {
      resolve({ ok: false, error: String(error) });
    }
  });
}

function categoryFor(item) {
  return item.mediaCategory === 'stream' ? 'Video' : undefined;
}

async function startDownload(url, fileName, item) {
  const message = {
    action: 'add-download',
    source: 'extension',
    url,
    fileName,
    kind: item && item.mediaCategory === 'stream' ? 'stream' : undefined,
    category: categoryFor(item),
    referer: item ? item.pageUrl : undefined
  };
  return sendNativeMessage(message);
}

async function startDownloadBatch(items) {
  const payload = items.map((item) => ({
    url: item.url,
    fileName: item.fileName,
    kind: item.mediaCategory === 'stream' ? 'stream' : undefined,
    category: categoryFor(item)
  }));
  return sendNativeMessage({ action: 'add-batch', source: 'extension', items: payload });
}

chrome.runtime.onMessage.addListener((incomingMessage, messageSender, sendResponseCallback) => {
  const message = incomingMessage || {};
  const tabId = typeof message.tabId === 'number' ? message.tabId : messageSender.tab && messageSender.tab.id;

  if (message.actionType === 'PAGE_READY') {
    if (typeof tabId === 'number' && tabId >= 0) {
      queueTabTask(tabId, () => ensureCatalogLoaded(tabId)).catch(() => undefined);
    }
    sendResponseCallback({ ready: true });
    return true;
  }

  if (message.actionType === 'QUERY_MEDIA_CATALOG') {
    if (typeof tabId !== 'number' || tabId < 0) {
      sendResponseCallback({ mediaCatalog: [] });
      return true;
    }
    queueTabTask(tabId, async () => {
      await ensureCatalogLoaded(tabId);
      const catalog = catalogStore.get(tabId) || new Map();
      return Array.from(catalog.values()).sort((left, right) => right.discoveredAt - left.discoveredAt);
    })
      .then((mediaCatalog) => sendResponseCallback({ mediaCatalog }))
      .catch(() => sendResponseCallback({ mediaCatalog: [] }));
    return true;
  }

  if (message.actionType === 'INGEST_DOM_MEDIA') {
    if (typeof tabId !== 'number' || tabId < 0 || !Array.isArray(message.mediaList)) {
      sendResponseCallback({ acknowledged: false });
      return true;
    }
    const candidates = message.mediaList
      .filter((entry) => entry && typeof entry.sourceUrl === 'string' && /^https?:/i.test(entry.sourceUrl))
      .map((entry) => ({
        sourceUrl: entry.sourceUrl,
        mediaCategory: entry.mediaCategory || classifyMimeCategory(entry.mimeType || ''),
        mimeType: entry.mimeType || '',
        fileSizeBytes: Number(entry.fileSizeBytes) || 0,
        fileName: entry.fileName || deriveFilenameFromUrl(entry.sourceUrl, entry.mimeType || ''),
        detectedVia: entry.detectedVia || 'dom',
        discoveredAt: entry.discoveredAt || Date.now()
      }));
    recordDiscoveredMedia(tabId, candidates)
      .then(() => sendResponseCallback({ acknowledged: true }))
      .catch(() => sendResponseCallback({ acknowledged: false }));
    return true;
  }

  if (message.actionType === 'CLEAR_TAB_CATALOG') {
    if (typeof tabId !== 'number' || tabId < 0) {
      sendResponseCallback({ cleared: false });
      return true;
    }
    queueTabTask(tabId, async () => {
      catalogStore.delete(tabId);
      loadedTabs.add(tabId);
      catalogStore.set(tabId, new Map());
      try {
        await chrome.storage.session.remove(catalogKey(tabId));
      } catch (_err) {
        // ignore
      }
      updateBadgeDisplay(tabId, 0);
    })
      .then(() => sendResponseCallback({ cleared: true }))
      .catch(() => sendResponseCallback({ cleared: false }));
    return true;
  }

  if (message.actionType === 'START_DOWNLOAD') {
    startDownload(message.url, message.fileName, message.item)
      .then((response) => sendResponseCallback({ ok: response && response.ok, error: response && response.error }))
      .catch((error) => sendResponseCallback({ ok: false, error: String(error) }));
    return true;
  }

  if (message.actionType === 'START_DOWNLOAD_BATCH') {
    const items = Array.isArray(message.items) ? message.items : [];
    if (items.length === 0) {
      sendResponseCallback({ accepted: 0, skipped: 0 });
      return true;
    }
    startDownloadBatch(items)
      .then((response) => sendResponseCallback({
        ok: response && response.ok,
        accepted: response && response.accepted,
        skipped: response && response.skipped,
        error: response && response.error
      }))
      .catch((error) => sendResponseCallback({ ok: false, error: String(error) }));
    return true;
  }

  if (message.actionType === 'PING_HOST') {
    sendNativeMessage({ action: 'ping', source: 'extension' })
      .then((response) => sendResponseCallback({ ok: response && response.ok }))
      .catch((error) => sendResponseCallback({ ok: false, error: String(error) }));
    return true;
  }

  return false;
});

chrome.tabs.onRemoved.addListener((closedTabId) => {
  catalogStore.delete(closedTabId);
  loadedTabs.delete(closedTabId);
  try {
    chrome.storage.session.remove(catalogKey(closedTabId));
  } catch (_err) {
    // ignore
  }
});

chrome.tabs.onUpdated.addListener((updatedTabId, changeInfo) => {
  if (changeInfo.status !== 'loading' || !changeInfo.url) return;
  catalogStore.delete(updatedTabId);
  loadedTabs.add(updatedTabId);
  catalogStore.set(updatedTabId, new Map());
  try {
    chrome.storage.session.remove(catalogKey(updatedTabId));
  } catch (_err) {
    // ignore
  }
  updateBadgeDisplay(updatedTabId, 0);
});
