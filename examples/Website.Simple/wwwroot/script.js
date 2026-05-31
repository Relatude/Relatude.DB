const STATUS_LABELS = ['Queued', 'Running', 'Completed', 'Failed', 'Canceled'];
const POLL_INTERVAL = 1000;

function formatTime(iso) {
    const d = new Date(iso);
    return isNaN(d) ? '—' : d.toLocaleTimeString();
}

function buildProgressCell(conv) {
    if (conv.status !== 1) return `<span class="progress-text">—</span>`;
    const pct = Math.max(0, Math.min(100, conv.progressPercentage || 0));
    return `<span class="progress-bar-wrap"><span class="progress-bar-fill" style="width:${pct}%"></span></span><span class="progress-text">${pct}%</span>`;
}

async function cancelConversion(id, btn) {
    btn.disabled = true;
    btn.textContent = 'Canceling…';
    try {
        await fetch(`/cancel?id=${encodeURIComponent(id)}`, { method: 'POST' });
    } catch (e) {
        btn.disabled = false;
        btn.textContent = 'Cancel';
    }
}

function renderTable(conversions) {
    const tbody = document.getElementById('conversions-body');
    if (!conversions.length) {
        tbody.innerHTML = '<tr><td colspan="8" class="empty-row">No conversions in progress or recent history.</td></tr>';
        return;
    }
    tbody.innerHTML = conversions.map(c => {
        const canCancel = c.status === 0 || c.status === 1;
        const cancelBtn = canCancel
            ? `<button class="btn-cancel" onclick="cancelConversion('${c.id}', this)">Cancel</button>`
            : '';
        return `<tr>
            <td class="file-name" title="${c.fileName}">${c.fileName || '—'}</td>
            <td><span class="format-badge">${c.fromFormat ?? '—'}</span></td>
            <td><span class="format-badge">${c.toFormat ?? '—'}</span></td>
            <td><span class="status-pill status-${c.status}">${STATUS_LABELS[c.status] ?? c.status}</span></td>
            <td>${buildProgressCell(c)}</td>
            <td class="desc-cell" title="${c.description}">${c.description || '—'}</td>
            <td class="time-cell">${c.processedMs != null ? Math.round(c.processedMs).toLocaleString() + ' ms' : '—'}</td>
            <td>${cancelBtn}</td>
        </tr>`;
    }).join('');
}

function renderStats(data) {
    document.getElementById('stats').innerHTML = `
        <div class="stat-card stat-running"><div class="value">${data.running}</div><div class="label">Running</div></div>
        <div class="stat-card stat-queued"><div class="value">${data.queued}</div><div class="label">Queued</div></div>
        <div class="stat-card stat-completed"><div class="value">${data.completed}</div><div class="label">Completed</div></div>
        <div class="stat-card stat-failed"><div class="value">${data.failed + data.canceled}</div><div class="label">Failed / Canceled</div></div>
    `;
}

async function poll() {
    try {
        const res = await fetch('/getstatus');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        renderStats(data);
        renderTable(data.current);
        document.getElementById('last-updated').textContent = `Updated: ${new Date().toLocaleTimeString()}`;
    } catch (e) {
        document.getElementById('last-updated').textContent = `Error: ${e.message}`;
    }
}

poll();
setInterval(poll, POLL_INTERVAL);
