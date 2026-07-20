(function () {
    var stream = document.getElementById('logstream');
    var appSelect = document.getElementById('app-select');
    var pauseBtn = document.getElementById('pause-btn');
    var clearBtn = document.getElementById('clear-btn');
    var keywordFilter = document.getElementById('keyword-filter');
    var autoscroll = document.getElementById('autoscroll');
    var liveIndicator = document.getElementById('live-indicator');
    var levelToggles = Array.prototype.slice.call(document.querySelectorAll('.level-toggle'));

    if (!stream || !appSelect) return;

    var paused = false;
    var currentApp = appSelect.value;
    var connection = null;
    var MAX_LINES = 500;

    function pad(n, len) { return n.toString().padStart(len || 2, '0'); }

    function formatTimestamp(ts) {
        var d = ts ? new Date(ts) : new Date();
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(d.getMilliseconds(), 3);
    }

    function levelClass(level) {
        return (level || 'unknown').toString().toLowerCase();
    }

    function activeLevels() {
        return levelToggles.filter(function (cb) { return cb.checked; }).map(function (cb) { return cb.value; });
    }

    function appendLine(entry) {
        if (paused) return;

        var cls = levelClass(entry.level);
        if (activeLevels().indexOf(cls) === -1) return;

        var keyword = keywordFilter.value.trim().toLowerCase();
        if (keyword && entry.message.toLowerCase().indexOf(keyword) === -1) return;

        var el = document.createElement('div');
        el.className = 'logline';
        el.innerHTML =
            '<span class="ts">' + formatTimestamp(entry.timestamp) + '</span>' +
            '<span class="lvl ' + cls + '">' + cls.toUpperCase().padEnd(7) + '</span>' +
            '<span class="src">' + escapeHtml(entry.sourceFile || '') + '</span>' +
            '<span class="msg">' + escapeHtml(entry.message) + '</span>';
        stream.appendChild(el);

        while (stream.children.length > MAX_LINES) {
            stream.removeChild(stream.firstChild);
        }

        if (autoscroll.checked) {
            stream.scrollTop = stream.scrollHeight;
        }
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function setLiveState(isLive) {
        liveIndicator.style.color = isLive ? 'var(--ok)' : 'var(--err)';
        liveIndicator.querySelector('.pulse').style.background = isLive ? 'var(--ok)' : 'var(--err)';
        liveIndicator.lastChild.textContent = isLive ? 'LIVE' : 'DISCONNECTED';
    }

    function connect() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/log')
            .withAutomaticReconnect()
            .build();

        connection.on('logLine', appendLine);
        connection.onreconnected(function () { setLiveState(true); joinCurrentApp(); });
        connection.onreconnecting(function () { setLiveState(false); });
        connection.onclose(function () { setLiveState(false); });

        connection.start()
            .then(function () { setLiveState(true); joinCurrentApp(); })
            .catch(function (err) { console.error('SignalR connection failed', err); setLiveState(false); });
    }

    function joinCurrentApp() {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
        connection.invoke('JoinApp', currentApp).catch(function (err) { console.error(err); });
    }

    appSelect.addEventListener('change', function () {
        var previous = currentApp;
        currentApp = appSelect.value;
        stream.innerHTML = '';
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke('LeaveApp', previous).catch(function () {});
            joinCurrentApp();
        }
        history.replaceState(null, '', '/LiveTail?app=' + encodeURIComponent(currentApp));
    });

    pauseBtn.addEventListener('click', function () {
        paused = !paused;
        pauseBtn.textContent = paused ? 'Resume' : 'Pause';
        pauseBtn.classList.toggle('active-toggle', paused);
    });

    clearBtn.addEventListener('click', function () {
        stream.innerHTML = '';
    });

    connect();
})();
