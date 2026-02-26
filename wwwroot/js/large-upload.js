import { BlockBlobClient } from 'https://cdn.jsdelivr.net/npm/@azure/storage-blob@12.17.0/+esm';
import SparkMD5 from 'https://cdn.jsdelivr.net/npm/spark-md5@3.0.2/+esm';

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

  function getRequestVerificationToken() {
    const tokenField = document.querySelector('input[name="__RequestVerificationToken"]');
    return tokenField ? tokenField.value : null;
  }
  function formatBytes(bytes) { if (bytes === 0) return '0 B'; const u=['B','KB','MB','GB','TB']; const i=Math.floor(Math.log(bytes)/Math.log(1024)); const v=bytes/Math.pow(1024,i); return v.toFixed(v>=10||i===0?0:2)+' '+u[i]; }
  function formatSpeed(bps) { if (bps <= 0) return '0 MB/s'; return (bps/1024/1024).toFixed(2)+' MB/s'; }
  async function postForm(handler, data) { const form=new FormData(); for(const [k,v] of Object.entries(data)) form.append(k,v); const t=getRequestVerificationToken(); if(t) form.append('__RequestVerificationToken',t); const resp=await fetch(`?handler=${handler}`,{method:'POST',body:form}); if(!resp.ok) throw new Error(await resp.text()); return await resp.json(); }

  function hexToBytes(hex){
    const arr = new Uint8Array(hex.length/2);
    for(let i=0;i<hex.length;i+=2) arr[i/2] = parseInt(hex.substr(i,2),16);
    return arr;
  }

  async function computeMd5(file){
    status.textContent = 'Hashing (MD5)...';
    const chunkSize = 4 * 1024 * 1024;
    const totalChunks = Math.max(1, Math.ceil(file.size / chunkSize));
    const spark = new SparkMD5.ArrayBuffer();
    let offset = 0;
    for (let i=0;i<totalChunks;i++){
      const slice = file.slice(offset, offset + chunkSize);
      const buf = await slice.arrayBuffer();
      spark.append(buf);
      offset += chunkSize;
      if (i % 4 === 0 || i === totalChunks - 1){
        const pct = Math.round(((i+1)/totalChunks)*100);
        status.textContent = `Hashing (MD5) ${pct}%`;
      }
    }
    const hex = spark.end();
    const md5Bytes = hexToBytes(hex);
    let binary = '';
    for (let i=0;i<md5Bytes.length;i++) binary += String.fromCharCode(md5Bytes[i]);
    const b64 = btoa(binary);
    return { hex, base64: b64, bytes: md5Bytes };
  }

  let abortController = null;
  cancelBtn.addEventListener('click',()=>{ if(abortController){ abortController.abort(); status.textContent='Cancellation requested...'; cancelBtn.disabled=true; } });

  const lgControls = document.getElementById('largeUploadControls');
  const uploadBtn = document.getElementById('uploadBtn');

  btn.addEventListener('click', async ()=>{
    const file=fileInput.files[0]; if(!file){ status.textContent='Select a file first.'; return; }
    const concurrency=parseInt(concSel.value,10);
    const blockSizeMB=parseInt(blockSel.value,10);
    const initialMB=parseInt(initialSel.value,10);
    const blockSize=blockSizeMB*1024*1024;
    const initialTransferSize=initialMB>0?initialMB*1024*1024:blockSize;
    if(blockSize>100*1024*1024){ status.textContent='Block size must be <=100MB'; return; }

    btn.disabled=true; cancelBtn.disabled=false; progWrap.style.display='block'; progBar.style.width='0%'; progBar.textContent='0%'; metricsContainer.style.display='none'; metricsText.textContent='';
    if(lgControls) lgControls.style.display='block';
    if(uploadBtn) { uploadBtn.disabled=true; uploadBtn.innerHTML='<span class="spinner-border spinner-border-sm" role="status"></span> Uploading…'; }

    let md5;
    try{
      md5 = await computeMd5(file);
    } catch(err){
      status.textContent='MD5 hashing failed: '+err.message;
      btn.disabled=false; cancelBtn.disabled=true;
      return;
    }

    // Duplicate check before requesting SAS
    try {
      status.textContent='Checking for duplicate...';
      const dupResp = await postForm('CheckDuplicate', { md5Base64: md5.base64 });
      if (dupResp.duplicate){
        status.innerHTML = `Duplicate detected (MD5 match). Existing blob: <code>${dupResp.blobName}</code>${dupResp.fileName ? ' ('+dupResp.fileName+')' : ''}. Upload skipped.`;
        btn.disabled=false; cancelBtn.disabled=true;
        return;
      }
    } catch { /* ignore duplicate check failure */ }

    const roomId = btn.getAttribute('data-room-id') || '';
    const folderPath = btn.getAttribute('data-folder-path') || '';

    status.textContent='Requesting SAS...';
    let init;
    try { init = await postForm('InitLarge', { roomId, fileName: file.name, folderPath }); }
    catch(err){ status.textContent='Failed to init: '+err.message; btn.disabled=false; cancelBtn.disabled=true; return; }

    const client = new BlockBlobClient(init.sas);
    abortController = new AbortController();
    const start=performance.now(); let lastLoaded=0; let lastTime=start;

    try {
      await client.uploadBrowserData(file, {
        blockSize,
        maxSingleShotSize: initialTransferSize,
        concurrency,
        abortSignal: abortController.signal,
        blobHTTPHeaders: {
          blobContentType: file.type || 'application/octet-stream',
          blobContentMD5: md5.bytes
        },
        // NEW: set md5 tag at creation
      //  tags: {
        //  md5: md5.base64
       // },
        onProgress: ev=>{
          const now=performance.now();
          const loaded=ev.loadedBytes;
          const pct=Math.round((loaded/file.size)*100);
          progBar.style.width=pct+'%'; progBar.textContent=pct+'%';
          const totalElapsedSec=(now-start)/1000;
          const avg=loaded/totalElapsedSec;
          const deltaBytes=loaded-lastLoaded;
          const deltaTimeSec=(now-lastTime)/1000;
          if(deltaTimeSec>=0.5){
            const inst=deltaBytes/(deltaTimeSec||1e-6);
            status.textContent=`Uploading ${pct}% (${formatBytes(loaded)} / ${formatBytes(file.size)}) Avg: ${formatSpeed(avg)} Cur: ${formatSpeed(inst)}`;
            lastLoaded=loaded; lastTime=now;
          }
        }
      });

      const totalMs=performance.now()-start;
      const avgSpeed=file.size/(totalMs/1000);
      status.textContent=`Upload complete in ${(totalMs/1000).toFixed(2)}s Avg: ${formatSpeed(avgSpeed)}`;

      await postForm('FinalizeLarge', {
        roomId,
        blobName:init.blobName,
        fileName:file.name,
        size:file.size,
        contentType:file.type,
        folderPath,
        uploadDurationMs: Math.round(totalMs),
        averageBytesPerSecond: Math.round(avgSpeed),
        blockSizeMB: blockSizeMB,
        concurrency: concurrency,
        md5Base64: md5.base64
      });

      metricsContainer.style.display='block';
      metricsText.textContent=JSON.stringify({
        fileName:file.name,
        size:file.size,
        durationMs:Math.round(totalMs),
        averageBytesPerSecond:Math.round(avgSpeed),
        blockSize,
        concurrency,
        initialTransferSize,
        md5Base64: md5.base64
      }, null, 2);
      setTimeout(()=>window.location.reload(),1800);
    } catch(err){
      if(err.name==='AbortError'){
        status.textContent='Upload canceled.';
      } else {
        status.textContent='Error: '+err.message;
        console.error(err);
      }
    } finally {
      btn.disabled=false; cancelBtn.disabled=true; abortController=null;
      if(uploadBtn) { uploadBtn.disabled=false; uploadBtn.textContent='Upload'; }
    }
  });
})();
