(function () {
    var appSelect = document.getElementById('app-select');
    var dateSelect = document.getElementById('date-select');
    var fileSelect = document.getElementById('file-select');
    var levelSelect = document.getElementById('level-select');
    var keywordInput = document.getElementById('keyword-input');
    var regexToggle = document.getElementById('regex-toggle');
    var rangeFrom = document.getElementById('range-from');
    var rangeTo = document.getElementById('range-to');
    var exportRangeBtn = document.getElementById('export-range-btn');
    var tagSelect = document.getElementById('tag-select');
    var tagValueSelect = document.getElementById('tag-value-select');
    var searchBtn = document.getElementById('search-btn');
    var exportBtn = document.getElementById('export-btn');
    var resultsBody = document.getElementById('results-body');

    if (!appSelect) return;

    var lastResults = [];
    // key -> [values], as discovered in the selected day's logs.
    var availableTags = {};

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text || '';
        return div.innerHTML;
    }

    function currentApp() { return appSelect.value; }

    function loadDates() {
        dateSelect.innerHTML = '<option value="">Loading dates...</option>';
        fetch('/api/apps/' + encodeURIComponent(currentApp()) + '/dates')
            .then(function (r) { return r.json(); })
            .then(function (dates) {
                dateSelect.innerHTML = '';
                if (dates.length === 0) {
                    dateSelect.innerHTML = '<option value="">No log data found</option>';
                    return;
                }
                dates.forEach(function (d) {
                    var opt = document.createElement('option');
                    opt.value = d;
                    opt.textContent = d;
                    dateSelect.appendChild(opt);
                });
                // Default the export range to the full span that actually has
                // logs (dates arrive newest-first).
                rangeFrom.value = dates[dates.length - 1];
                rangeTo.value = dates[0];
                loadFiles();
            })
            .catch(function (err) { console.error(err); dateSelect.innerHTML = '<option value="">Error loading dates</option>'; });
    }

    function loadFiles() {
        var date = dateSelect.value;
        fileSelect.innerHTML = '<option value="">All files for this date</option>';
        if (!date) return;

        fetch('/api/apps/' + encodeURIComponent(currentApp()) + '/files?date=' + encodeURIComponent(date))
            .then(function (r) { return r.json(); })
            .then(function (files) {
                files.forEach(function (f) {
                    var opt = document.createElement('option');
                    opt.value = f;
                    opt.textContent = f;
                    fileSelect.appendChild(opt);
                });
                loadTags();
            })
            .catch(function (err) { console.error(err); });
    }

    // Group-by options come from the logs themselves rather than a fixed list,
    // so a format LogHub has never seen still offers whatever fields it does
    // carry - and a format with none simply shows "No grouping".
    function loadTags() {
        var date = dateSelect.value;
        availableTags = {};
        tagSelect.innerHTML = '<option value="">No grouping</option>';
        resetTagValues();
        if (!date) return;

        var params = new URLSearchParams({ date: date });
        if (fileSelect.value) params.set('file', fileSelect.value);

        fetch('/api/apps/' + encodeURIComponent(currentApp()) + '/tags?' + params.toString())
            .then(function (r) { return r.json(); })
            .then(function (tags) {
                tags.forEach(function (t) {
                    availableTags[t.key] = t.values;
                    var opt = document.createElement('option');
                    opt.value = t.key;
                    opt.textContent = 'Group by: ' + t.key + ' (' + t.values.length + ')';
                    tagSelect.appendChild(opt);
                });
            })
            .catch(function (err) { console.error(err); });
    }

    function resetTagValues() {
        tagValueSelect.innerHTML = '<option value="">All values</option>';
    }

    function onTagChanged() {
        resetTagValues();
        var values = availableTags[tagSelect.value] || [];
        values.forEach(function (v) {
            var opt = document.createElement('option');
            opt.value = v;
            opt.textContent = v;
            tagValueSelect.appendChild(opt);
        });
    }

    // Filters shared by the on-screen search and the date-range export, so the
    // exported file always reflects what the user is looking at.
    function filterParams() {
        var params = new URLSearchParams();
        if (levelSelect.value) params.set('level', levelSelect.value);
        if (keywordInput.value.trim()) params.set('keyword', keywordInput.value.trim());
        if (regexToggle.checked) params.set('regex', 'true');
        if (tagSelect.value) {
            params.set('tagKey', tagSelect.value);
            if (tagValueSelect.value) params.set('tagValue', tagValueSelect.value);
        }
        return params;
    }

    function search() {
        var date = dateSelect.value;
        if (!date) {
            resultsBody.innerHTML = '<tr><td colspan="4" class="empty-hint">Pick a date first.</td></tr>';
            return;
        }

        var params = filterParams();
        params.set('date', date);
        if (fileSelect.value) params.set('file', fileSelect.value);

        resultsBody.innerHTML = '<tr><td colspan="4" class="empty-hint">Searching...</td></tr>';

        fetch('/api/apps/' + encodeURIComponent(currentApp()) + '/logs?' + params.toString())
            .then(function (r) {
                if (r.ok) return r.json();
                // A bad regex comes back as a 400 with an explanation - showing
                // it beats a silent empty table.
                return r.json().then(function (body) {
                    throw new Error((body && body.error) || 'Search failed');
                });
            })
            .then(function (entries) {
                lastResults = entries;
                renderResults(entries);
            })
            .catch(function (err) {
                console.error(err);
                resultsBody.innerHTML = '<tr><td colspan="4" class="empty-hint">' +
                    escapeHtml(err.message || 'Search failed - see browser console.') + '</td></tr>';
            });
    }

    function exportRange() {
        var from = rangeFrom.value;
        var to = rangeTo.value;
        if (!from || !to) {
            alert('Pick both a start and end date for the range export.');
            return;
        }

        var params = filterParams();
        params.set('from', from);
        params.set('to', to);

        // Straight browser navigation so the CSV streams to disk instead of
        // being buffered in memory as a blob.
        window.location.href = '/api/apps/' + encodeURIComponent(currentApp()) + '/export?' + params.toString();
    }

    function entryRow(e) {
        var level = (e.level || 'Unknown');
        var ts = e.timestamp ? new Date(e.timestamp).toLocaleString() : '';
        return '<tr><td>' + escapeHtml(ts) + '</td>' +
            '<td class="lvl ' + level.toLowerCase() + '">' + escapeHtml(level) + '</td>' +
            '<td>' + escapeHtml(e.sourceFile) + '</td>' +
            '<td>' + escapeHtml(e.message) + '</td></tr>';
    }

    function renderResults(entries) {
        if (entries.length === 0) {
            resultsBody.innerHTML = '<tr><td colspan="4" class="empty-hint">No matching log lines.</td></tr>';
            return;
        }

        var groupKey = tagSelect.value;
        if (!groupKey) {
            resultsBody.innerHTML = entries.map(entryRow).join('');
            return;
        }

        // Preserve first-seen order so groups stay in chronological order.
        var order = [];
        var groups = {};
        entries.forEach(function (e) {
            var value = (e.tags && e.tags[groupKey]) || '(none)';
            if (!groups[value]) { groups[value] = []; order.push(value); }
            groups[value].push(e);
        });

        var html = '';
        order.forEach(function (value) {
            html += '<tr class="group-header"><td colspan="4">' +
                escapeHtml(groupKey + ' = ' + value) +
                ' <span class="group-count">' + groups[value].length + '</span></td></tr>';
            html += groups[value].map(entryRow).join('');
        });

        resultsBody.innerHTML = html;
    }

    function exportCsv() {
        if (lastResults.length === 0) return;

        var header = 'Timestamp,Level,File,Message,Tags';
        var lines = lastResults.map(function (e) {
            var tags = Object.keys(e.tags || {}).map(function (k) { return k + '=' + e.tags[k]; }).join(' ');
            var cells = [e.timestamp || '', e.level || '', e.sourceFile || '', e.message || '', tags];
            return cells.map(function (c) {
                var v = String(c).replace(/"/g, '""');
                return '"' + v + '"';
            }).join(',');
        });

        var blob = new Blob([header + '\n' + lines.join('\n')], { type: 'text/csv' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = currentApp() + '_' + (dateSelect.value || 'export') + '.csv';
        a.click();
        URL.revokeObjectURL(url);
    }

    appSelect.addEventListener('change', function () {
        history.replaceState(null, '', '/History?app=' + encodeURIComponent(currentApp()));
        loadDates();
    });
    dateSelect.addEventListener('change', loadFiles);
    fileSelect.addEventListener('change', loadTags);
    tagSelect.addEventListener('change', onTagChanged);
    searchBtn.addEventListener('click', search);
    exportBtn.addEventListener('click', exportCsv);
    exportRangeBtn.addEventListener('click', exportRange);
    keywordInput.addEventListener('keydown', function (e) { if (e.key === 'Enter') search(); });

    loadDates();
})();
