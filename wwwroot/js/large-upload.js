import { BlockBlobClient } from 'https://cdn.jsdelivr.net/npm/@azure/storage-blob@12.17.0/+esm';
import SparkMD5 from 'https://cdn.jsdelivr.net/npm/spark-md5@3.0.2/+esm';

// ----- helpers -----
function getRequestVerificationToken() {
  const tokenField = document.querySelector('input[name="__RequestVerificationToken"]');
  return tokenField ? tokenField.value : null;
}
function formatBytes(bytes) {
  if (bytes === 0) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  const v = bytes / Math.pow(1024, i);
  return v.toFixed(v >= 10 || i === 0 ? 0 : 2) + ' ' + u[i];
}
function formatSpeed(bps) {
  if (bps <= 0) return '0 MB/s';
  return (bps / 1024 / 1024).toFixed(2) + ' MB/s';
}
async function postForm(handler, data) {
  const form = new FormData();
  for (const [k, v] of Object.entries(data)) form.append(k, v);
  const t = getRequestVerificationToken();
  if (t) form.append('__RequestVerificationToken', t);
  const resp = await fetch(`?handler=${handler}`, { method: 'POST', body: form });
  if (!resp.ok) throw new Error(await resp.text());
  const ct = resp.headers.get('content-type') || '';
  return ct.includes('application/json') ? await resp.json() : await resp.text();
}
function hexToBytes(hex) {
  const arr = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) arr[i / 2] = parseInt(hex.substr(i, 2), 16);
  return arr;
}
async function computeMd5(file, onStatus) {
  onStatus?.('Hashing (MD5)...');
  const chunkSize = 4 * 1024 * 1024;
  const totalChunks = Math.max(1, Math.ceil(file.size / chunkSize));
  const spark = new SparkMD5.ArrayBuffer();
  let offset = 0;
  for (let i = 0; i < totalChunks; i++) {
    const slice = file.slice(offset, offset + chunkSize);
    const buf = await slice.arrayBuffer();
    spark.append(buf);
    offset += chunkSize;
    if (i % 4 === 0 || i === totalChunks - 1) {
      const pct = Math.round(((i + 1) / totalChunks) * 100);
      onStatus?.(`Hashing (MD5) ${pct}%`);
    }
  }
  const hex = spark.end();
  const md5Bytes = hexToBytes(hex);
  let binary = '';
  for (let i = 0; i < md5Bytes.length; i++) binary += String.fromCharCode(md5Bytes[i]);
  const b64 = btoa(binary);
  return { hex, base64: b64, bytes: md5Bytes };
}

// ----- exported API -----
// StartLargeExport options:
// {
//   file: File (required),
//   initHandler?: 'InitLarge' by default,
//   finalizeHandler?: 'FinalizeLarge' by default,
//   duplicateHandler?: 'CheckDuplicate' to enable duplicate check,
//   initExtra?: {roomId, folderPath, ...} extra form fields for init,
//   finalizeExtra?: {...} extra form fields for finalize,
//   concurrency?: number (default 4),
//   blockSizeMB?: number (default 8, <= 100),
//   initialMB?: number (default 0=auto as single-shot threshold),
//   signal?: AbortSignal (optional),
//   onStatus?: (text)=>void,
//   onProgress?: ({loadedBytes,totalBytes,pct,avgBps,instBps})=>void,
//   onAfterComplete?: (finalizeResponse)=>void,
// }
// Returns: { controller: AbortController, promise: Promise<void> }
export function startLargeUpload(opts) {
  const {
    file,
    initHandler = 'InitLarge',
    finalizeHandler = 'FinalizeLarge',
    duplicateHandler = 'CheckDuplicate',
    initExtra = {},
    finalizeExtra = {},
    concurrency = 4,
    blockSizeMB = 8,
    initialMB = 0,
    signal,
    onStatus,
    onProgress,
    onAfterComplete
  } = opts || {};

  if (!file) throw new Error('file is required');
  if (blockSizeMB > 100) throw new Error('Block size must be <=100MB');

  // Create our own controller and tie into optional external signal
  const controller = new AbortController();
  if (signal) {
    if (signal.aborted) controller.abort();
    else signal.addEventListener('abort', () => controller.abort(), { once: true });
  }

  const promise = (async () => {
    try {
      const blockSize = blockSizeMB * 1024 * 1024;
      const initialTransferSize = initialMB > 0 ? initialMB * 1024 * 1024 : blockSize;

      // MD5
      let md5;
      try {
        md5 = await computeMd5(file, onStatus);
      } catch (err) {
        onStatus?.('MD5 hashing failed: ' + (err?.message || err));
        throw err;
      }

      // Duplicate (optional)
      if (duplicateHandler) {
        try {
          onStatus?.('Checking for duplicate...');
          const dupResp = await postForm(duplicateHandler, { md5Base64: md5.base64, fileName: file.name, ...initExtra });
          if (dupResp && typeof dupResp === 'object' && dupResp.duplicate) {
            onStatus?.(`Duplicate detected (MD5 match). Existing blob: ${dupResp.blobName}${dupResp.fileName ? ' (' + dupResp.fileName + ')' : ''}. Upload skipped.`);
            return;
          }
        } catch {
          // ignore duplicate check failures
        }
      }

      // Init SAS
      onStatus?.('Requesting SAS...');
      const initPayload = { fileName: file.name, ...initExtra };
      const init = await postForm(initHandler, initPayload);
      if (!init?.sas || !init?.blobName) throw new Error('Init response missing SAS or blobName');

      // Upload
      const client = new BlockBlobClient(init.sas);
      const start = performance.now();
      let lastLoaded = 0;
      let lastTime = start;

      await client.uploadBrowserData(file, {
        blockSize,
        maxSingleShotSize: initialTransferSize,
        concurrency,
        abortSignal: controller.signal,
        blobHTTPHeaders: {
          blobContentType: file.type || 'application/octet-stream',
          blobContentMD5: md5.bytes
        },
        onProgress: ev => {
          const now = performance.now();
          const loaded = ev.loadedBytes;
          const pct = Math.round((loaded / file.size) * 100);
          const totalElapsedSec = (now - start) / 1000;
          const avg = loaded / totalElapsedSec;
          const deltaBytes = loaded - lastLoaded;
          const deltaTimeSec = (now - lastTime) / 1000;
          const inst = deltaTimeSec >= 0.25 ? (deltaBytes / (deltaTimeSec || 1e-6)) : 0;
          if (deltaTimeSec >= 0.5) {
            lastLoaded = loaded;
            lastTime = now;
          }
          onProgress?.({ loadedBytes: loaded, totalBytes: file.size, pct, avgBps: avg, instBps: inst });
        }
      });

      const totalMs = performance.now() - start;
      const avgSpeed = file.size / (totalMs / 1000);
      onStatus?.(`Upload complete in ${(totalMs / 1000).toFixed(2)}s Avg: ${formatSpeed(avgSpeed)}`);

      // Finalize
      const finPayload = {
        blobName: init.blobName,
        fileName: file.name,
        size: file.size,
        contentType: file.type || 'application/octet-stream',
        uploadDurationMs: Math.round(totalMs),
        averageBytesPerSecond: Math.round(avgSpeed),
        blockSizeMB,
        concurrency,
        md5Base64: md5.base64,
        ...finalizeExtra
      };
      const fin = await postForm(finalizeHandler, finPayload);
      onAfterComplete?.(fin);
    } catch (err) {
      if (controller.signal.aborted) {
        onStatus?.('Upload canceled.');
        return;
      }
      onStatus?.('Error: ' + (err?.message || err));
      throw err;
    }
  })();

  return { controller, promise };
}

// ----- legacy DOM wiring (kept for current page) -----
(function () {
  const btn = document.getElementById('startLargeUpload');
  if (!btn) return;

  const cancelBtn = document.getElementById('cancelUpload');
  const fileInput = document.getElementById('largeFile');
  const progWrap = document.getElementById('progWrap');
  const progBar = document.getElementById('progBar');
  const status = document.getElementById('uploadStatus');
  const concSel = document.getElementById('concurrency');
  const initialSel = document.getElementById('initialSize');
  const blockSel = document.getElementById('blockSize');
  const metricsContainer = document.getElementById('metricsContainer');
  const metricsText = document.getElementById('metricsText');

  let currentController = null;

  cancelBtn?.addEventListener('click', () => {
    if (currentController) {
      currentController.abort();
      cancelBtn.disabled = true;
    }
  });

  btn.addEventListener('click', async () => {
    const file = fileInput?.files?.[0];
    if (!file) {
      status.textContent = 'Select a file first.';
      return;
    }

    const concurrency = parseInt(concSel.value, 10) || 4;
    const blockSizeMB = parseInt(blockSel.value, 10) || 8;
    const initialMB = parseInt(initialSel.value, 10) || 0;
    if (blockSizeMB > 100) {
      status.textContent = 'Block size must be <=100MB';
      return;
    }

    // UI reset
    btn.disabled = true;
    if (cancelBtn) cancelBtn.disabled = false;
    if (progWrap) {
      progWrap.style.display = 'block';
      progBar.style.width = '0%';
      progBar.textContent = '0%';
    }
    metricsContainer.style.display = 'none';
    metricsText.textContent = '';

    // Optional: pass roomId/folderPath via data attributes if present
    const initExtra = {};
    const finalizeExtra = {};
    const roomId = btn.getAttribute('data-room-id');
    const folderPath = btn.getAttribute('data-folder-path');
    if (roomId) { initExtra.roomId = roomId; finalizeExtra.roomId = roomId; }
    if (folderPath != null) { initExtra.folderPath = folderPath; finalizeExtra.folderPath = folderPath; }

    const started = startLargeUpload({
      file,
      concurrency,
      blockSizeMB,
      initialMB,
      initExtra,
      finalizeExtra,
      onStatus: (text) => { status.textContent = text; },
      onProgress: ({ pct, loadedBytes, totalBytes, avgBps, instBps }) => {
        if (progWrap) {
          progBar.style.width = pct + '%';
          progBar.textContent = pct + '%';
        }
        status.textContent = `Uploading ${pct}% (${formatBytes(loadedBytes)} / ${formatBytes(totalBytes)}) Avg: ${formatSpeed(avgBps)} Cur: ${formatSpeed(instBps)}`;
      },
      onAfterComplete: (fin) => {
        metricsContainer.style.display = 'block';
        metricsText.textContent = JSON.stringify({ fileName: file.name, size: file.size, fin }, null, 2);
        setTimeout(() => window.location.reload(), 1800);
      }
    });

    currentController = started.controller;

    try {
      await started.promise;
    } finally {
      btn.disabled = false;
      if (cancelBtn) cancelBtn.disabled = true;
      currentController = null;
    }
  });
})();
