'use strict';
(() => {
  // Boxes are interpolated only across adjacent observations. Gaps never invent a vehicle position.
  function boxAtTime(observations, seconds, fps = 30, stride = 2) {
    if (!observations?.length || !Number.isFinite(seconds)) return null;
    const frameSeconds = 1 / Math.max(1, fps);
    if (seconds < observations[0].videoSeconds - frameSeconds / 2 ||
        seconds > observations[observations.length - 1].videoSeconds + frameSeconds / 2) return null;
    let low = 0, high = observations.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if (observations[middle].videoSeconds < seconds) low = middle + 1;
      else high = middle;
    }
    const after = observations[Math.min(low, observations.length - 1)];
    const before = observations[Math.max(0, low - 1)];
    if (Math.abs(after.videoSeconds - seconds) < 1e-6) return after.box;
    if (before === after) return before.box;
    const gap = after.videoSeconds - before.videoSeconds;
    const maxGap = Math.max(.12, 3 * Math.max(1, stride) / Math.max(1, fps));
    if (gap > maxGap) {
      const nearest = Math.abs(seconds - before.videoSeconds) < Math.abs(after.videoSeconds - seconds) ? before : after;
      return Math.abs(nearest.videoSeconds - seconds) <= frameSeconds / 2 ? nearest.box : null;
    }
    const t = Math.max(0, Math.min(1, (seconds - before.videoSeconds) / Math.max(1e-9, gap)));
    return Object.fromEntries(['x', 'y', 'width', 'height'].map(key => [key, before.box[key] + (after.box[key] - before.box[key]) * t]));
  }

  function scaleBox(box, sourceWidth, sourceHeight, canvasWidth, canvasHeight) {
    return {x:box.x / sourceWidth * canvasWidth, y:box.y / sourceHeight * canvasHeight,
      width:box.width / sourceWidth * canvasWidth, height:box.height / sourceHeight * canvasHeight};
  }

  class Controller {
    constructor(document, callbacks = {}) {
      this.document = document;
      this.callbacks = callbacks;
      this.video = document.getElementById('review-video');
      this.canvas = document.getElementById('review-canvas');
      this.context = this.canvas.getContext('2d');
      this.active = false;
      this.data = null;
      this.token = 0;
      this.frameHandle = null;
      this.abort = null;
      this.lastRefresh = 0;
      document.getElementById('review-close').addEventListener('click', () => this.close());
      document.getElementById('review-first').addEventListener('click', () => this.seek(this.data?.vehicle.firstSeenSeconds));
      document.getElementById('review-crossing').addEventListener('click', () => this.seek(this.data?.vehicle.crossedAtSeconds));
      this.video.addEventListener('seeked', () => this.draw(this.video.currentTime));
      this.video.addEventListener('timeupdate', () => this.draw(this.video.currentTime));
      this.video.addEventListener('error', () => {
        if (this.active) this.setStatus('This video codec cannot be played in the browser. Use an H.264 MP4 for review.');
      });
      new ResizeObserver(() => this.resize()).observe(this.video);
    }
    get(id) { return this.document.getElementById(id); }
    setStatus(text) { this.get('review-status').textContent = text; }
    async select(snapshot, vehicle) {
      const token = ++this.token;
      this.abort?.abort();
      this.abort = new AbortController();
      this.video.pause();
      try {
        const response = await (globalThis.RoadLensApi?.fetch || fetch)(`/api/jobs/${snapshot.id}/vehicles/${vehicle.trackId}/review`, {signal:this.abort.signal});
        const data = await response.json();
        if (!response.ok) throw Error(data.detail || data.error || 'Unable to review this vehicle.');
        if (token !== this.token) return;
        this.data = data;
        this.stride = snapshot.settings.frameStride;
        this.active = true;
        this.get('source-video').hidden = true;
        this.get('line-canvas').hidden = true;
        this.get('processed-preview').hidden = true;
        this.get('review-stage').hidden = false;
        this.get('review-toolbar').hidden = false;
        this.get('media-stage').hidden = false;
        this.get('empty-state').hidden = true;
        this.get('review-title').textContent = `Vehicle #${vehicle.trackId} · ${vehicle.vehicleClass}`;
        this.get('review-crossing').disabled = vehicle.crossedAtSeconds === null;
        this.setStatus('Loading the recorded vehicle position…');
        this.callbacks.onSelect?.(vehicle.trackId);
        const ratio = data.video.width / data.video.height;
        this.get('media-stage').style.width = `${Math.min(this.get('viewport').clientWidth, 600 * ratio)}px`;
        if (this.video.dataset.jobId !== snapshot.id) {
          this.video.dataset.jobId = snapshot.id;
          this.video.src = globalThis.RoadLensApi?.url(data.videoUrl) || data.videoUrl;
          this.video.load();
        }
        if (this.video.readyState < 1) await this.waitForMetadata(this.abort.signal);
        if (token !== this.token) return;
        this.resize();
        this.seek(vehicle.crossedAtSeconds ?? vehicle.firstSeenSeconds);
        this.startFrames();
        this.get('viewport').scrollIntoView({behavior:'smooth', block:'center'});
      } catch (error) {
        if (error.name !== 'AbortError' && token === this.token) {
          this.setStatus(error.message);
          this.callbacks.message?.(error.message, true);
        }
      }
    }
    waitForMetadata(signal) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => finish(Error('Video metadata did not load. Please select the vehicle again.')), 15000);
        const success = () => finish();
        const failure = () => finish(Error('The video cannot be played in this browser. An H.264 MP4 is recommended.'));
        const aborted = () => finish(new DOMException('Review selection changed.', 'AbortError'));
        const finish = error => {
          clearTimeout(timer);
          this.video.removeEventListener('loadedmetadata', success);
          this.video.removeEventListener('error', failure);
          signal.removeEventListener('abort', aborted);
          error ? reject(error) : resolve();
        };
        this.video.addEventListener('loadedmetadata', success, {once:true});
        this.video.addEventListener('error', failure, {once:true});
        signal.addEventListener('abort', aborted, {once:true});
        if (this.video.readyState >= 1) finish();
        else if (signal.aborted) aborted();
      });
    }
    seek(seconds) {
      if (!this.active || !Number.isFinite(seconds)) return;
      this.video.pause();
      const duration = Number.isFinite(this.video.duration) ? this.video.duration : seconds + 1;
      this.video.currentTime = Math.max(0, Math.min(seconds, Math.max(0, duration - .001)));
      this.context.clearRect(0, 0, this.canvas.width, this.canvas.height);
      this.setStatus(`Seeking to ${FlowActivity.videoTime(seconds)}…`);
      if (!this.video.seeking) this.draw(this.video.currentTime);
    }
    resize() {
      if (!this.active || !this.data) return;
      const width = this.video.clientWidth;
      if (!width) return;
      const ratio = this.data.video.width / this.data.video.height;
      const dpr = globalThis.devicePixelRatio || 1;
      this.canvas.width = Math.round(width * dpr);
      this.canvas.height = Math.round(width / ratio * dpr);
      this.draw(this.video.currentTime);
    }
    draw(seconds) {
      if (!this.active || !this.data) return;
      const ctx = this.context, width = this.canvas.width, height = this.canvas.height;
      ctx.clearRect(0, 0, width, height);
      if (this.video.seeking) return;
      const box = boxAtTime(this.data.observations, seconds, this.data.video.fps, this.stride);
      const stamp = FlowActivity.videoTime(seconds);
      if (!box) { this.setStatus(`${stamp} · Vehicle #${this.data.trackId} is not detected at this moment.`); return; }
      const scaled = scaleBox(box, this.data.video.width, this.data.video.height, width, height);
      const dpr = globalThis.devicePixelRatio || 1;
      ctx.fillStyle = 'rgba(0, 0, 0, 0.30)';
      ctx.beginPath();ctx.rect(0, 0, width, height);ctx.rect(scaled.x, scaled.y, scaled.width, scaled.height);ctx.fill('evenodd');
      ctx.strokeStyle = '#b1ff70';ctx.lineWidth = 4 * dpr;
      ctx.shadowColor = '#111';ctx.shadowBlur = 5 * dpr;
      ctx.strokeRect(scaled.x, scaled.y, scaled.width, scaled.height);
      ctx.shadowBlur = 0;
      const label = `#${this.data.trackId} ${this.data.vehicle.vehicleClass}`;
      ctx.font = `bold ${13*dpr}px Segoe UI`;
      const labelWidth = ctx.measureText(label).width + 16*dpr;
      const labelY = Math.max(0, scaled.y - 26*dpr);
      const labelX = Math.max(0, Math.min(scaled.x, width - labelWidth));
      ctx.fillStyle = '#b1ff70';ctx.fillRect(labelX, labelY, labelWidth, 24*dpr);
      ctx.fillStyle = '#11210c';ctx.fillText(label, labelX+8*dpr, labelY+17*dpr);
      this.setStatus(`${stamp} · Vehicle #${this.data.trackId} highlighted. Use the video controls to play.`);
    }
    startFrames() {
      this.stopFrames();
      const tick = (_now, metadata) => {
        if (!this.active) return;
        this.draw(metadata?.mediaTime ?? this.video.currentTime);
        this.frameHandle = this.video.requestVideoFrameCallback ? this.video.requestVideoFrameCallback(tick) : requestAnimationFrame(tick);
      };
      this.frameHandle = this.video.requestVideoFrameCallback ? this.video.requestVideoFrameCallback(tick) : requestAnimationFrame(tick);
    }
    stopFrames() {
      if (this.frameHandle === null) return;
      if (this.video.cancelVideoFrameCallback) this.video.cancelVideoFrameCallback(this.frameHandle);
      else cancelAnimationFrame(this.frameHandle);
      this.frameHandle = null;
    }
    close(notify = true) {
      ++this.token;
      this.abort?.abort();
      this.active = false;
      this.video.pause();
      this.stopFrames();
      this.context.clearRect(0, 0, this.canvas.width, this.canvas.height);
      this.get('review-stage').hidden = true;
      this.get('review-toolbar').hidden = true;
      if (notify) this.callbacks.onClose?.();
    }
  }
  globalThis.FlowReview = {boxAtTime, scaleBox, Controller};
  if (typeof module !== 'undefined') module.exports = globalThis.FlowReview;
})();
