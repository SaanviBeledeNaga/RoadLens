'use strict';
const api = RoadLensApi;
const $ = id => document.getElementById(id);
const video = $('source-video'), canvas = $('line-canvas'), ctx = canvas.getContext('2d');
const reviewController = new FlowReview.Controller(document, {
  message,
  onSelect: trackId => activityTable.select(trackId),
  onClose: () => {
    activityTable.select(null);
    if (lastSnapshot?.processedFrames > 0) {
      $('processed-preview').hidden = false;
      video.hidden = true;
      canvas.hidden = true;
    } else if (file) { video.hidden = false; canvas.hidden = false; }
  }
});
const activityTable = new FlowActivity.Controller(document, (vehicle, snapshot) => reviewController.select(snapshot, vehicle));
let file = null, objectUrl = null, modelReady = false, busy = false, jobId = null, pollTimer = null;
let line = defaultLine(), dragging = false, lineStart = null, lastSnapshot = null, pollFailures = 0;
function defaultLine(){return {start:{x:.02,y:.6},end:{x:.98,y:.6}};}
function message(text,error=false){$('message').textContent=text;$('message').classList.toggle('error',error);}
function time(seconds){const s=Math.max(0,Math.floor(seconds||0));return `${String(Math.floor(s/60)).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`;}
function setBusy(value){busy=value;$('start-button').disabled=value||!file||!modelReady;$('video-input').disabled=value;$('confidence').disabled=value;$('stride').disabled=value;$('reset-line').disabled=value;$('stop-button').hidden=!value;canvas.style.pointerEvents=value?'none':'auto';}
async function health(){try{const response=await api.fetch('/api/health');if(!response.ok)throw Error();const data=await response.json();modelReady=data.modelReady;$('health-label').textContent=modelReady?'Detection engine online':'Detection engine unavailable';$('health-dot').className=`dot ${modelReady?'online':'offline'}`;if(!modelReady)message(data.modelError||'The detection model is loading.',true);$('start-button').disabled=busy||!file||!modelReady;}catch{modelReady=false;$('health-label').textContent='Backend disconnected';$('health-dot').className='dot offline';$('start-button').disabled=true;}}
function stageSize(){const widthSource=video.videoWidth||lastSnapshot?.video?.width;const heightSource=video.videoHeight||lastSnapshot?.video?.height;if(!widthSource||!heightSource)return;const ratio=widthSource/heightSource;const width=Math.min($('viewport').clientWidth,600*ratio);$('media-stage').style.width=`${width}px`;const dpr=window.devicePixelRatio||1;canvas.width=Math.round(width*dpr);canvas.height=Math.round(width/ratio*dpr);drawLine();}
function drawLine(){const w=canvas.width,h=canvas.height;ctx.clearRect(0,0,w,h);if(!file)return;const a={x:line.start.x*w,y:line.start.y*h},b={x:line.end.x*w,y:line.end.y*h};const dpr=window.devicePixelRatio||1;ctx.strokeStyle='#f5d670';ctx.fillStyle='#f5d670';ctx.lineWidth=3*dpr;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();for(const p of [a,b]){ctx.beginPath();ctx.arc(p.x,p.y,5*dpr,0,Math.PI*2);ctx.fill();}const dx=b.x-a.x,dy=b.y-a.y,length=Math.hypot(dx,dy)||1;ctx.font=`bold ${13*dpr}px Segoe UI`;ctx.textAlign='center';ctx.textBaseline='middle';for(const [label,sign] of [['A',-1],['B',1]]){const x=(a.x+b.x)/2+sign*(-dy/length)*22*dpr,y=(a.y+b.y)/2+sign*(dx/length)*22*dpr;ctx.fillStyle='#111b14';ctx.fillRect(x-10*dpr,y-10*dpr,20*dpr,20*dpr);ctx.fillStyle='#f5d670';ctx.fillText(label,x,y);}}
function point(event){const r=canvas.getBoundingClientRect();return{x:Math.max(0,Math.min(1,(event.clientX-r.left)/r.width)),y:Math.max(0,Math.min(1,(event.clientY-r.top)/r.height))};}
canvas.addEventListener('pointerdown',e=>{if(busy||!file)return;lineStart=point(e);dragging=true;canvas.setPointerCapture(e.pointerId);});
canvas.addEventListener('pointermove',e=>{if(!dragging)return;line={start:lineStart,end:point(e)};drawLine();});
canvas.addEventListener('pointerup',()=>{if(!dragging)return;dragging=false;if(Math.hypot(line.start.x-line.end.x,line.start.y-line.end.y)<.05){line=defaultLine();message('Line was too short. Drag a longer line across the road.');}else message('Counting line set. Ready to analyze.');drawLine();});
canvas.addEventListener('pointercancel',()=>{dragging=false;line=defaultLine();video.hidden=false;canvas.hidden=false;$('processed-preview').hidden=true;drawLine();});
$('reset-line').addEventListener('click',()=>{if(busy)return;reviewController.close(false);activityTable.select(null);line=defaultLine();video.hidden=false;canvas.hidden=false;$('processed-preview').hidden=true;drawLine();});
$('confidence').addEventListener('input',()=>{$('confidence-value').textContent=`${$('confidence').value}%`;});
async function chooseFile(selected){if(busy||!selected)return;if(selected.size>500*1024*1024){message('Choose a video smaller than 500 MB.',true);return;}if(!/\.(mp4|mov|avi|mkv|webm)$/i.test(selected.name)){message('Choose an MP4, MOV, AVI, MKV, or WEBM video.',true);return;}reviewController.close(false);clearTimeout(pollTimer);jobId=null;lastSnapshot=null;file=selected;line=defaultLine();if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=URL.createObjectURL(file);$('upload-title').textContent=file.name;$('upload-detail').textContent='Click to choose another video';$('file-info').hidden=false;$('file-info').textContent=`${(file.size/1024/1024).toFixed(1)} MB · Local video`;$('video-name').textContent=`/ ${file.name}`;$('empty-state').hidden=true;$('media-stage').hidden=false;$('processed-preview').hidden=true;video.hidden=false;canvas.hidden=false;video.src=objectUrl;video.load();resetResults();message('Loading video preview…');$('start-button').disabled=!modelReady;}
video.addEventListener('loadeddata',()=>{stageSize();$('time-label').textContent=`00:00 / ${time(video.duration)}`;message('Drag across the video to adjust the counting line.');});
video.addEventListener('error',()=>{message('Browser preview is unavailable for this codec. The server may still decode it; MP4 (H.264) works best.',true);});
new ResizeObserver(stageSize).observe($('viewport'));
$('video-input').addEventListener('change',e=>chooseFile(e.target.files[0]));
for(const event of ['dragenter','dragover'])$('drop-zone').addEventListener(event,e=>{e.preventDefault();if(!busy)$('drop-zone').classList.add('dragover');});
for(const event of ['dragleave','drop'])$('drop-zone').addEventListener(event,e=>{e.preventDefault();$('drop-zone').classList.remove('dragover');if(event==='drop')chooseFile(e.dataTransfer.files[0]);});
function resetResults(){reviewController.close(false);for(const id of ['total','count-car','count-motorcycle','count-heavy','direction-a','direction-b'])$(id).textContent='0';$('progress-bar').style.width='0';$('progress-label').textContent='Ready to analyze';$('fps-label').textContent='— frames / sec';$('job-badge').textContent='READY';activityTable.reset();$('export-button').removeAttribute('href');$('export-button').classList.add('disabled');$('export-button').setAttribute('aria-disabled','true');}
function update(snapshot){lastSnapshot=snapshot;const counts=snapshot.counts;$('total').textContent=snapshot.total;$('count-car').textContent=counts.car||0;$('count-motorcycle').textContent=counts.motorcycle||0;$('count-heavy').textContent=(counts.bus||0)+(counts.truck||0);$('direction-a').textContent=snapshot.directions['A to B']||0;$('direction-b').textContent=snapshot.directions['B to A']||0;$('progress-bar').style.width=`${snapshot.progress*100}%`;$('progress-label').textContent=`${Math.round(snapshot.progress*100)}% processed`;$('time-label').textContent=`${time(snapshot.videoSeconds)} / ${time(snapshot.video?.durationSeconds)}`;$('fps-label').textContent=`${snapshot.processingFps.toFixed(1)} frames / sec`;$('job-badge').textContent=snapshot.status.toUpperCase();if(snapshot.processedFrames>0&&!reviewController.active){const image=$('processed-preview');image.onload=()=>{if(reviewController.active)return;image.hidden=false;video.hidden=true;canvas.hidden=true;};const preview=snapshot.previewUrl||`/api/jobs/${snapshot.id}/preview`;image.src=api.url(preview)+(preview.includes('?')?'&':'?')+`v=${snapshot.processedFrames}`;}activityTable.update(snapshot);}
function upload(form){return new Promise((resolve,reject)=>{const xhr=new XMLHttpRequest();xhr.open('POST',api.url('/api/jobs'));xhr.setRequestHeader('X-RoadLens-Client',api.clientKey);xhr.upload.onprogress=e=>{if(e.lengthComputable)message(`Uploading video… ${Math.round(e.loaded/e.total*100)}%`);};xhr.onload=()=>{let data;try{data=JSON.parse(xhr.responseText);}catch{reject(Error(`Server returned ${xhr.status}. Please try a smaller video.`));return;}if(xhr.status>=200&&xhr.status<300)resolve(data);else reject(Error(data.error||data.detail||'Upload failed.'));};xhr.onerror=()=>reject(Error('Upload failed. Check that the backend is running.'));xhr.send(form);});}
$('analysis-form').addEventListener('submit',async e=>{e.preventDefault();if(!file||busy||!modelReady)return;setBusy(true);resetResults();video.pause();$('start-button').innerHTML='Processing video <span>◌</span>';try{const form=new FormData();form.append('video',file);form.append('settings',JSON.stringify({line,confidence:Number($('confidence').value)/100,frameStride:Number($('stride').value)}));const snapshot=await upload(form);jobId=snapshot.id;update(snapshot);pollFailures=0;message('Analyzing video. Counts update as vehicles cross the line.');poll();}catch(error){message(error.message,true);setBusy(false);$('start-button').innerHTML='Analyze video <span>↗</span>';}});
async function poll(){if(!jobId)return;try{const response=await api.fetch(`/api/jobs/${jobId}`);if(!response.ok)throw Error(response.status===404?'This job is no longer in memory. Select the video and start again.':'Unable to read processing status.');const snapshot=await response.json();pollFailures=0;update(snapshot);if(['completed','cancelled','failed'].includes(snapshot.status)){setBusy(false);$('start-button').innerHTML='Analyze again <span>↗</span>';message(snapshot.status==='failed'?snapshot.error:snapshot.status==='completed'?`Analysis complete. ${snapshot.total} vehicle crossings counted.`:'Processing stopped. Partial results are available.',snapshot.status==='failed');return;}pollTimer=setTimeout(poll,600);}catch(error){pollFailures++;message(`${error.message} Retrying…`,true);if(pollFailures>=10){message('Connection lost. Reload the page once the backend is running.',true);setBusy(false);return;}pollTimer=setTimeout(poll,1500);}}
$('stop-button').addEventListener('click',async()=>{if(!jobId)return;try{const response=await api.fetch(`/api/jobs/${jobId}/cancel`,{method:'POST'});if(!response.ok)throw Error('Could not stop this job.');message('Stopping processing…');}catch(error){message(error.message,true);}});
health();setInterval(health,15000);
$('sample-button').addEventListener('click',async()=>{if(busy)return;const button=$('sample-button');button.disabled=true;try{message('Loading sample footage…');const response=await fetch('/samples/traffic.mp4');if(!response.ok)throw Error('Sample video is unavailable. Choose your own video.');const blob=await response.blob();await chooseFile(new File([blob],'sample-traffic.mp4',{type:'video/mp4'}));}catch(error){message(error.message,true);}finally{button.disabled=false;}});




// Restore the latest job that includes complete vehicle history after a refresh.
async function restoreRecentJob() {
    try {
        const response = await api.fetch('/api/jobs');
        if (!response.ok || file || busy) return;
        const jobs = await response.json();
        const saved = jobs.find(job => Array.isArray(job.vehicles) && job.playbackAvailable) || jobs.find(job => Array.isArray(job.vehicles));
        if (!saved || file || busy) return;
        jobId = saved.id;
        line = saved.settings.line;
        $('confidence').value = String(Math.round(saved.settings.confidence * 100));
        $('confidence-value').textContent = `${$('confidence').value}%`;
        $('stride').value = String(saved.settings.frameStride);
        $('video-name').textContent = `/ ${saved.fileName}`;
        $('empty-state').hidden = true;
        $('media-stage').hidden = false;
        video.hidden = true;
        canvas.hidden = true;
        if (saved.video) {
            const ratio = saved.video.width / saved.video.height;
            $('media-stage').style.width = `${Math.min($('viewport').clientWidth, 600 * ratio)}px`;
        }
        update(saved);
        if (['queued', 'processing'].includes(saved.status)) {
            setBusy(true);
            poll();
        } else {
            message(`Restored ${saved.vehicles.length} tracked vehicles and ${saved.total} crossings. Choose a video to analyze again.`);
        }
    } catch { /* The normal upload flow remains available when history cannot be read. */ }
}
restoreRecentJob();
