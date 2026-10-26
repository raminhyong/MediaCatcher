const ui = {
  pageHost: document.getElementById('pageHost'),
  mediaCount: document.getElementById('mediaCount'),
  mediaList: document.getElementById('mediaList'),
  filters: document.getElementById('filters'),
  combinePdf: document.getElementById('combinePdf'),
  downloadAll: document.getElementById('downloadAll'),
  rescan: document.getElementById('rescan'),
  clear: document.getElementById('clear'),
  manualUrl: document.getElementById('manualUrl'),
  manualDownload: document.getElementById('manualDownload'),
  status: document.getElementById('status'),
  hostBar: document.getElementById('hostBar'),
  hostDot: document.getElementById('hostDot'),
  hostLabel: document.getElementById('hostLabel')
};

const state = {
  tabId: null,
  tabUrl: '',
  tabSupported: false,
  items: [],
  filter: 'all',
  busy: false,
  autoScanned: false,
  hostReady: false
};

const CATEGORY_LABELS = {
  video: 'วิดีโอ',
  audio: 'เสียง',
  image: 'รูปภาพ',
  stream: 'สตรีม',
  file: 'ไฟล์',
  document: 'เอกสาร',
  media: 'มีเดีย'
};

const CATEGORY_GLYPHS = {
  video: '▶',
  audio: '♪',
  image: '▣',
  stream: '∞',
  file: '⇩',
  document: '▤',
  media: '⇩'
};

const BLOCKED_SCHEMES = /^(chrome|edge|about|brave|opera|vivaldi|chrome-extension|moz-extension):/i;

let statusTimer = null;

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function sendMessage(message, timeoutMs = 15000) {
  return new Promise((resolve) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      resolve({});
    }, timeoutMs);

    chrome.runtime.sendMessage(message, (response) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (chrome.runtime.lastError) {
        resolve({});
        return;
      }
      resolve(response || {});
    });
  });
}

function sendToTab(tabId, message) {
  return new Promise((resolve) => {
    chrome.tabs.sendMessage(tabId, message, () => {
      void chrome.runtime.lastError;
      resolve();
    });
  });
}

function setStatus(text, isError) {
  if (statusTimer) clearTimeout(statusTimer);
  if (!text) {
    ui.status.classList.remove('visible', 'error');
    ui.status.textContent = '';
    return;
  }
  ui.status.textContent = text;
  ui.status.classList.toggle('error', Boolean(isError));
  ui.status.classList.add('visible');
  statusTimer = setTimeout(() => {
    ui.status.classList.remove('visible', 'error');
  }, 5000);
}

function setHostStatus(ready, label) {
  state.hostReady = ready;
  ui.hostDot.classList.toggle('ok', ready);
  ui.hostDot.classList.toggle('down', !ready);
  ui.hostLabel.textContent = label;
}

function formatSize(bytes) {
  const value = Number(bytes) || 0;
  if (value <= 0) return 'ไม่ทราบขนาด';
  const units = ['B', 'KB', 'MB', 'GB'];
  let scaled = value;
  let unitIndex = 0;
  while (scaled >= 1024 && unitIndex < units.length - 1) {
    scaled /= 1024;
    unitIndex += 1;
  }
  const digits = unitIndex === 0 || scaled >= 100 ? 0 : 1;
  return `${scaled.toFixed(digits)} ${units[unitIndex]}`;
}

function currentFilteredItems() {
  if (state.filter === 'all') return state.items;
  return state.items.filter((item) => item.mediaCategory === state.filter);
}

function buildEmptyState() {
  const wrapper = document.createElement('div');
  wrapper.className = 'empty-state';

  const title = document.createElement('strong');
  const hint = document.createElement('span');

  if (!state.tabSupported) {
    title.textContent = 'สแกนหน้านี้ไม่ได้';
    hint.textContent = 'เปิดเว็บไซต์ปกติแล้วคลิกไอคอนอีกครั้ง';
  } else if (state.items.length === 0) {
    title.textContent = 'ยังไม่พบไฟล์มีเดีย';
    hint.textContent = 'เล่นวิดีโอหรือเลื่อนดูรูป แล้วกด "สแกนใหม่"';
  } else {
    title.textContent = 'ไม่มีไฟล์ในหมวดนี้';
    hint.textContent = 'ลองเลือกหมวด "ทั้งหมด"';
  }

  wrapper.append(title, hint);
  return wrapper;
}

function buildThumbnail(item) {
  const thumb = document.createElement('div');
  thumb.className = 'thumb';

  const canPreview =
    item.mediaCategory === 'image' &&
    typeof item.sourceUrl === 'string' &&
    /^https?:/i.test(item.sourceUrl) &&
    !/\.svg(?:[?#]|$)/i.test(item.sourceUrl);

  if (canPreview) {
    const preview = document.createElement('img');
    preview.src = item.sourceUrl;
    preview.alt = '';
    preview.loading = 'lazy';
    preview.referrerPolicy = 'no-referrer';
    preview.addEventListener('error', () => {
      preview.remove();
      thumb.textContent = CATEGORY_GLYPHS.image;
    });
    thumb.appendChild(preview);
  } else {
    thumb.textContent = CATEGORY_GLYPHS[item.mediaCategory] || CATEGORY_GLYPHS.media;
  }

  return thumb;
}

function buildMediaRow(item) {
  const row = document.createElement('div');
  row.className = 'media-item';

  const meta = document.createElement('div');
  meta.className = 'meta';

  const name = document.createElement('div');
  name.className = 'name';
  name.textContent = item.fileName || 'ไฟล์ไม่มีชื่อ';
  name.title = item.sourceUrl || '';

  const sub = document.createElement('div');
  sub.className = 'sub';

  const category = CATEGORY_LABELS[item.mediaCategory] ? item.mediaCategory : 'media';
  const badge = document.createElement('span');
  badge.className = `badge badge-${category}`;
  badge.textContent = CATEGORY_LABELS[category];

  const size = document.createElement('span');
  size.textContent =
    item.mediaCategory === 'stream' ? 'เพลย์ลิสต์สตรีม' :
    item.mediaCategory === 'document' ? 'แปลงเป็น PDF' :
    formatSize(item.fileSizeBytes);

  sub.append(badge, size);
  meta.append(name, sub);

  const downloadButton = document.createElement('button');
  downloadButton.type = 'button';
  downloadButton.className = 'dl-btn';
  downloadButton.textContent = '⇩';
  downloadButton.title = 'ดาวน์โหลด';
  downloadButton.disabled = state.busy;
  downloadButton.addEventListener('click', async () => {
    downloadButton.disabled = true;
    const started = await startDownload(item);
    if (!started) {
      downloadButton.disabled = false;
      return;
    }
    setTimeout(() => {
      downloadButton.disabled = state.busy;
    }, 1500);
  });

  row.append(buildThumbnail(item), meta, downloadButton);
  return row;
}

function render() {
  const visibleItems = currentFilteredItems();
  ui.mediaCount.textContent = state.items.length > 99 ? '99+' : String(state.items.length);

  ui.mediaList.replaceChildren();
  if (visibleItems.length === 0) {
    ui.mediaList.appendChild(buildEmptyState());
  } else {
    visibleItems.forEach((item) => ui.mediaList.appendChild(buildMediaRow(item)));
  }

  ui.downloadAll.disabled = state.busy || visibleItems.length === 0;
  ui.combinePdf.disabled =
    state.busy || state.items.filter((item) => item.mediaCategory === 'image').length === 0;
  ui.clear.disabled = state.busy || state.items.length === 0;
  ui.manualDownload.disabled = state.busy;
}

async function startDownload(item) {
  const response = await sendMessage({
    actionType: 'START_DOWNLOAD',
    url: item.sourceUrl,
    fileName: item.fileName,
    item
  });
  if (response && response.ok) {
    if (item.mediaCategory === 'stream') {
      setStatus('ส่งสตรีมให้ Media Catcher แล้ว (แอปจะแปลงเป็นวิดีโอ)');
    } else if (item.mediaCategory === 'document') {
      setStatus('ส่งหนังสือให้ Media Catcher แล้ว (แอปจะแปลงเป็น PDF)');
    } else {
      setStatus(`ส่งให้ Media Catcher: ${item.fileName}`);
    }
    return true;
  }
  setStatus(`ส่งไม่สำเร็จ: ${(response && response.error) || 'ไม่ทราบสาเหตุ'}`, true);
  return false;
}

async function refresh() {
  const response = await sendMessage({ actionType: 'QUERY_MEDIA_CATALOG', tabId: state.tabId });
  state.items = Array.isArray(response.mediaCatalog) ? response.mediaCatalog : [];
  render();
}

async function downloadAll() {
  const targets = currentFilteredItems();
  if (targets.length === 0) return;

  state.busy = true;
  render();
  setStatus(`กำลังส่งดาวน์โหลด ${targets.length} ไฟล์...`);

  const response = await sendMessage({
    actionType: 'START_DOWNLOAD_BATCH',
    items: targets.map((item) => ({ url: item.sourceUrl, fileName: item.fileName, mediaCategory: item.mediaCategory }))
  });

  state.busy = false;
  render();

  const accepted = Number(response.accepted) || 0;
  const skipped = Number(response.skipped) || 0;
  setStatus(
    `ส่งแล้ว ${accepted} ไฟล์${skipped > 0 ? ` (ข้าม ${skipped})` : ''}`,
    !response.ok && response.error ? true : false
  );
}

async function combinePdf() {
  const images = state.items.filter((item) => item.mediaCategory === 'image');
  if (images.length === 0) {
    setStatus('ไม่พบรูปภาพในหน้านี้ให้รวมเป็น PDF', true);
    return;
  }

  state.busy = true;
  render();
  setStatus(`กำลังรวม ${images.length} รูปเป็น PDF...`);

  let baseName = 'document';
  try {
    baseName = new URL(state.tabUrl).hostname || 'document';
  } catch (_error) {
    baseName = 'document';
  }
  const response = await sendMessage({
    actionType: 'COMBINE_PDF',
    urls: images.map((item) => item.sourceUrl),
    fileName: `${baseName}_pages`,
    referer: state.tabUrl
  }, 600000);

  state.busy = false;
  render();

  if (response && response.ok) {
    setStatus(`บันทึก PDF แล้ว (${(response.pageCount) || images.length} หน้า)`);
  } else {
    setStatus(`รวม PDF ไม่สำเร็จ: ${(response && response.error) || 'ไม่ทราบสาเหตุ'}`, true);
  }
}

async function rescan() {
  if (state.tabId == null) return;
  setStatus('กำลังสแกนหน้าเว็บ...');
  await sendToTab(state.tabId, { actionType: 'RESCAN_PAGE' });
  await delay(900);
  await refresh();
  setStatus(`พบ ${state.items.length} ไฟล์มีเดีย`);
}

async function clearCatalog() {
  await sendMessage({ actionType: 'CLEAR_TAB_CATALOG', tabId: state.tabId });
  state.items = [];
  render();
  setStatus('ล้างรายการแล้ว');
}

function normalizeManualUrl(rawInput) {
  const trimmed = String(rawInput || '').trim();
  if (!trimmed) return '';
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  try {
    const parsed = new URL(withScheme);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return '';
    return parsed.href;
  } catch (_error) {
    return '';
  }
}

function filenameFromUrl(sourceUrl) {
  try {
    const segment = new URL(sourceUrl).pathname.split('/').filter(Boolean).pop();
    if (segment && segment.includes('.')) return decodeURIComponent(segment);
  } catch (_error) {
    // ignore
  }
  return `media_${Date.now()}.bin`;
}

async function handleManualDownload() {
  const normalized = normalizeManualUrl(ui.manualUrl.value);
  if (!normalized) {
    setStatus('ลิงก์ไม่ถูกต้อง ต้องขึ้นต้นด้วย http:// หรือ https://', true);
    return;
  }
  const fileName = filenameFromUrl(normalized);
  const response = await sendMessage({
    actionType: 'START_DOWNLOAD',
    url: normalized,
    fileName
  });
  if (response && response.ok) {
    ui.manualUrl.value = '';
    setStatus(`ส่งให้ Media Catcher: ${fileName}`);
  } else {
    setStatus(`ส่งไม่สำเร็จ: ${(response && response.error) || 'ไม่ทราบสาเหตุ'}`, true);
  }
}

async function checkHost() {
  const response = await sendMessage({ actionType: 'PING_HOST' });
  if (response && response.ok) {
    setHostStatus(true, 'แอป Media Catcher พร้อมใช้งาน');
  } else {
    setHostStatus(false, 'ไม่พบแอป — เปิด Media Catcher แล้วลงทะเบียน Native Host ก่อน');
  }
}

async function initialize() {
  ui.filters.addEventListener('click', (event) => {
    const button = event.target.closest('.filter-btn');
    if (!button) return;
    state.filter = button.dataset.filter;
    ui.filters.querySelectorAll('.filter-btn').forEach((candidate) => {
      candidate.classList.toggle('active', candidate === button);
    });
    render();
  });

  if (ui.combinePdf) ui.combinePdf.addEventListener('click', combinePdf);
  ui.downloadAll.addEventListener('click', downloadAll);
  ui.rescan.addEventListener('click', rescan);
  ui.clear.addEventListener('click', clearCatalog);
  ui.manualDownload.addEventListener('click', handleManualDownload);
  ui.manualUrl.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') handleManualDownload();
  });

  checkHost();

  const activeTabs = await chrome.tabs.query({ active: true, currentWindow: true });
  const activeTab = activeTabs[0];

  if (!activeTab || typeof activeTab.id !== 'number') {
    state.tabSupported = false;
    render();
    return;
  }

  state.tabId = activeTab.id;
  const tabUrl = activeTab.url || '';
  state.tabUrl = tabUrl;
  state.tabSupported = !BLOCKED_SCHEMES.test(tabUrl);

  if (tabUrl) {
    try {
      ui.pageHost.textContent = new URL(tabUrl).hostname || tabUrl;
    } catch (_error) {
      ui.pageHost.textContent = tabUrl;
    }
  }

  if (!state.tabSupported) {
    render();
    return;
  }

  await refresh();

  if (state.items.length === 0 && !state.autoScanned) {
    state.autoScanned = true;
    await sendToTab(state.tabId, { actionType: 'RESCAN_PAGE' });
    await delay(800);
    await refresh();
  }
}

initialize();
