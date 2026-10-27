'use strict';
(() => {
  function videoTime(seconds) {
    if (seconds === null || seconds === undefined) return '—';
    const milliseconds = Math.max(0, Math.round(seconds * 1000));
    const minutes = Math.floor(milliseconds / 60000);
    const remainder = milliseconds % 60000;
    return `${String(minutes).padStart(2, '0')}:${String(Math.floor(remainder / 1000)).padStart(2, '0')}.${String(remainder % 1000).padStart(3, '0')}`;
  }

  function selectActivities(snapshot, filter = 'all', requestedPage = 0, pageSize = 50) {
    const complete = Array.isArray(snapshot?.vehicles);
    const all = complete ? snapshot.vehicles : (snapshot?.events || []).map(event => ({
      trackId: event.trackId, vehicleClass: event.vehicleClass, crossed: true,
      direction: event.direction, firstSeenSeconds: null, crossedAtSeconds: event.videoSeconds
    }));
    const filtered = all.filter(vehicle => filter === 'all' || (filter === 'crossed' ? vehicle.crossed : !vehicle.crossed))
      .sort((a, b) => a.trackId - b.trackId);
    const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
    const page = Math.max(0, Math.min(requestedPage, pageCount - 1));
    return {
      complete, page, pageCount, total: all.length, crossed: all.filter(vehicle => vehicle.crossed).length,
      filteredTotal: filtered.length, from: filtered.length ? page * pageSize + 1 : 0,
      to: Math.min((page + 1) * pageSize, filtered.length),
      rows: filtered.slice(page * pageSize, (page + 1) * pageSize)
    };
  }

  class Controller {
    constructor(document, onSelect = () => {}) {
      this.document = document;
      this.onSelect = onSelect;
      this.selectedTrackId = null;
      this.snapshot = null;
      this.page = 0;
      this.filter = document.getElementById('activity-filter');
      this.filter.addEventListener('change', () => { this.page = 0; this.render(); });
      document.getElementById('activity-prev').addEventListener('click', () => { this.page--; this.render(); });
      document.getElementById('activity-next').addEventListener('click', () => { this.page++; this.render(); });
    }
    reset() {
      this.snapshot = null;
      this.selectedTrackId = null;
      this.page = 0;
      this.filter.value = 'all';
      this.render();
    }
    select(trackId) {
      this.selectedTrackId = trackId;
      this.render();
    }
    update(snapshot) {
      if (this.snapshot?.id !== snapshot.id) { this.page = 0; this.selectedTrackId = null; }
      this.snapshot = snapshot;
      this.render();
    }
    render() {
      const get = id => this.document.getElementById(id);
      const view = selectActivities(this.snapshot, this.filter.value, this.page);
      this.page = view.page;
      get('activity-summary').textContent = `${view.total} tracked · ${view.crossed} crossed`;
      get('activity-help').textContent = this.snapshot && !view.complete
        ? 'This older result only contains crossings. Reanalyze the video to include every detected vehicle.'
        : this.snapshot && !this.snapshot.playbackAvailable
          ? 'Track numbers are in ascending order. Reanalyze this older result to enable video navigation and highlighting.'
          : 'Track numbers are in ascending order. Click a number to find and highlight that vehicle in the video.';
      const body = get('events-body');
      body.replaceChildren();
      if (!view.rows.length) {
        const row = this.document.createElement('tr');
        const cell = this.document.createElement('td');
        cell.colSpan = 6;
        cell.className = 'table-empty';
        cell.textContent = this.snapshot ? 'No vehicles match this filter yet.' : 'Detected vehicles will appear here during analysis.';
        row.append(cell);
        body.append(row);
      }
      for (const vehicle of view.rows) {
        const row = this.document.createElement('tr');
        if (vehicle.trackId === this.selectedTrackId) row.className = 'selected-vehicle-row';
        const values = [`#${vehicle.trackId}`, vehicle.vehicleClass, vehicle.crossed ? 'Crossed' : 'Not crossed',
          vehicle.direction?.replace(' to ', ' → ') || '—', videoTime(vehicle.firstSeenSeconds), videoTime(vehicle.crossedAtSeconds)];
        values.forEach((value, index) => {
          const cell = this.document.createElement('td');
          if (index === 0) {
            const button = this.document.createElement('button');
            button.type = 'button';
            button.className = 'track-link';
            button.textContent = value;
            button.disabled = !this.snapshot.playbackAvailable;
            button.setAttribute('aria-label', `View vehicle ${value} in the video`);
            button.setAttribute('aria-pressed', String(vehicle.trackId === this.selectedTrackId));
            button.title = button.disabled ? 'Reanalyze this video to enable review' : 'Jump to this vehicle and highlight it';
            button.addEventListener('click', () => this.onSelect(vehicle, this.snapshot));
            cell.append(button);
          } else if (index === 2) {
            const badge = this.document.createElement('span');
            badge.className = `crossing-status ${vehicle.crossed ? 'crossed' : 'not-crossed'}`;
            badge.textContent = value;
            cell.append(badge);
          } else cell.textContent = value;
          row.append(cell);
        });
        body.append(row);
      }
      get('activity-prev').disabled = view.page === 0;
      get('activity-next').disabled = view.page + 1 >= view.pageCount;
      get('activity-page').textContent = `${view.from}–${view.to} of ${view.filteredTotal} vehicles`;
      const exportButton = get('export-button');
      const available = !!this.snapshot && view.complete;
      exportButton.classList.toggle('disabled', !available);
      exportButton.setAttribute('aria-disabled', String(!available));
      if (available) exportButton.href = globalThis.RoadLensApi?.url(this.snapshot.vehicleExportUrl || `/api/jobs/${this.snapshot.id}/vehicles/export`) || `/api/jobs/${this.snapshot.id}/vehicles/export`;
      else exportButton.removeAttribute('href');
    }
  }
  globalThis.FlowActivity = { selectActivities, videoTime, Controller };
  if (typeof module !== 'undefined') module.exports = globalThis.FlowActivity;
})();
