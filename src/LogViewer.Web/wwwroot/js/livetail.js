(function () {
    var stream = document.getElementById('logstream');
    var appSelect = document.getElementById('app-select');
    var pauseBtn = document.getElementById('pause-btn');
    var clearBtn = document.getElementById('clear-btn');
    var keywordFilter = document.getElementById('keyword-filter');
    var autoscroll = document.getElementById('autoscroll');
    var liveIndicator = document.getElementById('live-indicator');
    var fileList = document.getElementById('file-list');
    var levelToggles = Array.prototype.slice.call(document.querySelectorAll('.level-toggle'));

    if (!stream || !appSelect) return;

    var paused = false;
    var currentApp = appSelect.value;
    var connection = null;
    var MAX_LINES = 500;

    var buffer = [];
    var knownFiles = {};
    var fileToggles = [];

    function pad(n, len) { return n.toString().padStart(len || 2, '0'); }

    function formatTimestamp(ts) {
        var d = ts ? new Date(ts) : new Date();
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(d.getMilliseconds(), 3);
    }

    function today() {
        var d = new Date();
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
    }

    function levelClass(level) {
        return (level || 'unknown').toString().toLowerCase();
    }

    function activeLevels() {
        return levelToggles.filter(function (cb) { return cb.checked; }).map(function (cb) { return cb.value; });
    }

    function activeFiles() {
        return fileToggles.filter(function (cb) { return cb.checked; }).map(function (cb) { return cb.value; });
    }

    function passesFilters(entry) {
        if (activeLevels().indexOf(levelClass(entry.level)) === -1) return false;

        // An empty file list means the fetch failed or the app has nothing for
        // today - filter on nothing rather than filtering out everything, which
        // would blank the pane and read as "live tail is broken".
        if (fileToggles.length && activeFiles().indexOf(entry.sourceFile || '') === -1) return false;

        var keyword = keywordFilter.value.trim().toLowerCase();
        if (keyword && entry.message.toLowerCase().indexOf(keyword) === -1) return false;

        return true;
    }

    function renderOne(entry) {
        var cls = levelClass(entry.level);
        var el = document.createElement('div');
        el.className = 'logline';
        el.innerHTML =
            '<span class="ts">' + formatTimestamp(entry.timestamp) + '</span>' +
            '<span class="lvl ' + cls + '">' + cls.toUpperCase().padEnd(7) + '</span>' +
            '<span class="src">' + escapeHtml(entry.sourceFile || '') + '</span>' +
            '<span class="msg">' + escapeHtml(entry.message) + '</span>';
        stream.appendChild(el);
    }

    function scrollIfFollowing() {
        if (autoscroll.checked) stream.scrollTop = stream.scrollHeight;
    }

    function render() {
        stream.innerHTML = '';
        buffer.filter(passesFilters).forEach(renderOne);
        scrollIfFollowing();
    }

    function appendLine(entry) {
        if (paused) return;

        ensureFileToggle(entry.sourceFile);

        buffer.push(entry);
        while (buffer.length > MAX_LINES) buffer.shift();

        if (!passesFilters(entry)) return;

        renderOne(entry);
        while (stream.children.length > MAX_LINES) {
            stream.removeChild(stream.firstChild);
        }
        scrollIfFollowing();
    }

    function addFileToggle(name, checked) {
        if (!fileList) return;
        knownFiles[name] = true;

        var label = document.createElement('label');
        label.className = 'checkline';

        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.className = 'file-toggle';
        cb.value = name;
        cb.checked = checked;
        cb.addEventListener('change', render);

        var text = document.createElement('span');
        text.className = 'filename';
        text.textContent = name;
        text.title = name;

        label.appendChild(cb);
        label.appendChild(text);
        fileList.appendChild(label);
        fileToggles.push(cb);
    }

    // A file can show up after the list was loaded (created mid-session, or a
    // rotation producing a new name). Add it already ticked so its lines are
    // never silently dropped.
    function ensureFileToggle(name) {
        if (!name || knownFiles[name]) return;
        addFileToggle(name, true);
    }

    function resetFiles() {
        knownFiles = {};
        fileToggles = [];
        if (fileList) fileList.innerHTML = '';
    }

    function loadFiles(appName) {
        if (!fileList) return;

        fetch('/api/apps/' + encodeURIComponent(appName) + '/files?date=' + today())
            .then(function (res) { return res.ok ? res.json() : []; })
            .then(function (files) {
                if (appName !== currentApp) return;
                files.forEach(function (name) { ensureFileToggle(name); });
                render();
            })
            .catch(function (err) { console.error('Could not load file list', err); });
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

        buffer = [];
        stream.innerHTML = '';
        resetFiles();
        loadFiles(currentApp);

        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            connection.invoke('LeaveApp', previous).catch(function () {});
            joinCurrentApp();
        }
        history.replaceState(null, '', '/LiveTail?app=' + encodeURIComponent(currentApp));
    });

    levelToggles.forEach(function (cb) { cb.addEventListener('change', render); });
    keywordFilter.addEventListener('input', render);

    pauseBtn.addEventListener('click', function () {
        paused = !paused;
        pauseBtn.textContent = paused ? 'Resume' : 'Pause';
        pauseBtn.classList.toggle('active-toggle', paused);
    });

    clearBtn.addEventListener('click', function () {
        buffer = [];
        stream.innerHTML = '';
    });

    loadFiles(currentApp);
    connect();
})();
