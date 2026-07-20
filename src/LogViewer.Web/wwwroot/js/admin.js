(function () {
    var nameInput = document.getElementById('app-name');
    var pathList = document.getElementById('path-list');
    var addPathBtn = document.getElementById('add-path-btn');
    var saveBtn = document.getElementById('save-btn');
    var formError = document.getElementById('form-error');
    var appsBody = document.getElementById('apps-body');

    if (!nameInput) return;

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text || '';
        return div.innerHTML;
    }

    function addPathRow(value) {
        var row = document.createElement('div');
        row.className = 'rowbtn';
        row.style.marginBottom = '8px';

        var input = document.createElement('input');
        input.type = 'text';
        input.className = 'path-input';
        input.placeholder = 'e.g. C:\\Logs\\CustomerPortal';
        input.style.flex = '1';
        input.value = value || '';
        row.appendChild(input);

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'icon-btn';
        removeBtn.textContent = '×';
        removeBtn.title = 'Remove this path';
        removeBtn.addEventListener('click', function () { row.remove(); });
        row.appendChild(removeBtn);

        pathList.appendChild(row);
    }

    function resetForm() {
        nameInput.value = '';
        pathList.innerHTML = '';
        addPathRow('');
        formError.style.display = 'none';
    }

    function showError(message) {
        formError.textContent = message;
        formError.style.display = 'block';
    }

    function loadApps() {
        fetch('/api/apps')
            .then(function (r) { return r.json(); })
            .then(function (apps) {
                if (apps.length === 0) {
                    appsBody.innerHTML = '<tr><td colspan="3" class="empty-hint">No applications registered yet.</td></tr>';
                    return;
                }

                appsBody.innerHTML = apps.map(function (a) {
                    var paths = (a.rootPaths || []).map(escapeHtml).join('<br>');
                    return '<tr>' +
                        '<td>' + escapeHtml(a.name) + '</td>' +
                        '<td>' + paths + '</td>' +
                        '<td><button class="ghost remove-app-btn" data-name="' + escapeHtml(a.name) + '">Remove</button></td>' +
                        '</tr>';
                }).join('');

                Array.prototype.slice.call(appsBody.querySelectorAll('.remove-app-btn')).forEach(function (btn) {
                    btn.addEventListener('click', function () { removeApp(btn.dataset.name); });
                });
            })
            .catch(function (err) {
                console.error(err);
                appsBody.innerHTML = '<tr><td colspan="3" class="empty-hint">Could not load applications.</td></tr>';
            });
    }

    function removeApp(name) {
        if (!confirm('Remove "' + name + '"? This only unregisters it - no log files are touched.')) return;

        fetch('/api/apps/' + encodeURIComponent(name), { method: 'DELETE' })
            .then(function () { loadApps(); })
            .catch(function (err) { console.error(err); });
    }

    function saveApp() {
        var name = nameInput.value.trim();
        var paths = Array.prototype.slice.call(pathList.querySelectorAll('.path-input'))
            .map(function (i) { return i.value.trim(); })
            .filter(function (v) { return v.length > 0; });

        formError.style.display = 'none';

        if (!name) { showError('Application name is required.'); return; }
        if (paths.length === 0) { showError('At least one log folder path is required.'); return; }

        fetch('/api/apps', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: name, rootPaths: paths })
        })
            .then(function (r) { return r.json().then(function (body) { return { ok: r.ok, body: body }; }); })
            .then(function (result) {
                if (!result.ok || !result.body.ok) {
                    showError(result.body.error || 'Could not save this application.');
                    return;
                }
                resetForm();
                loadApps();
            })
            .catch(function (err) {
                console.error(err);
                showError('Could not reach the server.');
            });
    }

    addPathBtn.addEventListener('click', function () { addPathRow(''); });
    saveBtn.addEventListener('click', saveApp);

    loadApps();
})();
