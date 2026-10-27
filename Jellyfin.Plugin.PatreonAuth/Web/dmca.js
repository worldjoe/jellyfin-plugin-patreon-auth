// Adds a "Report DMCA" button to jellyfin-web item detail pages.
(function () {
    'use strict';
    if (window.__dmcaButtonLoaded) { return; }
    window.__dmcaButtonLoaded = true;

    var BTN_CLASS = 'dmcaReportButton';

    function itemIdFromHash() {
        var m = /[?&]id=([0-9a-fA-F]{32}|[0-9a-fA-F-]{36})/.exec(window.location.hash);
        return m ? m[1] : null;
    }

    function authHeader() {
        var c = window.ApiClient;
        return c && c.accessToken() ? 'MediaBrowser Token="' + c.accessToken() + '"' : null;
    }

    function dashedGuid(id) {
        var h = String(id).replace(/-/g, '');
        return h.length === 32
            ? h.slice(0, 8) + '-' + h.slice(8, 12) + '-' + h.slice(12, 16) + '-' + h.slice(16, 20) + '-' + h.slice(20)
            : id;
    }

    function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }

    var FIELDS = [
        ['CopyrightedWork', 'Copyrighted work you claim is infringed (title, owner, URL/registration)', 'textarea'],
        ['ComplainantName', 'Your full legal name', 'text'],
        ['Email', 'Email address', 'email'],
        ['Phone', 'Phone number', 'tel'],
        ['Address', 'Postal address', 'text'],
        ['Signature', 'Electronic signature (type your full legal name)', 'text']
    ];

    function openForm(itemId) {
        var auth = authHeader();
        if (!auth) { window.alert('You must be signed in to report.'); return; }
        var ov = document.createElement('div');
        ov.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.75);z-index:100000;overflow:auto;display:flex;align-items:flex-start;justify-content:center;padding:2em 1em';
        var html = '<form style="background:#202020;color:#eee;max-width:34em;width:100%;padding:1.5em;border-radius:.5em">'
            + '<h2 style="margin-top:0">DMCA takedown notice</h2>'
            + '<p style="font-size:.9em;opacity:.8">17 U.S.C. 512(c)(3). Knowingly misrepresenting infringement may make you liable for damages.</p>';
        FIELDS.forEach(function (f) {
            var input = f[2] === 'textarea'
                ? '<textarea name="' + f[0] + '" rows="3" required maxlength="600" style="width:100%;box-sizing:border-box"></textarea>'
                : '<input name="' + f[0] + '" type="' + f[2] + '" required style="width:100%;box-sizing:border-box">';
            html += '<label style="display:block;margin:.8em 0 .2em">' + esc(f[1]) + '</label>' + input;
        });
        html += '<label style="display:block;margin-top:1em"><input type="checkbox" name="GoodFaithStatement" required> '
            + 'I have a good faith belief that use of the material in the manner complained of is not authorized by the copyright owner, its agent, or the law.</label>'
            + '<label style="display:block;margin-top:.6em"><input type="checkbox" name="AccuracyStatement" required> '
            + 'The information in this notice is accurate, and under penalty of perjury, I am authorized to act on behalf of the owner of an exclusive right that is allegedly infringed.</label>'
            + '<div style="margin-top:1.2em;text-align:right"><button type="button" class="dmcaCancel" style="margin-right:1em">Cancel</button>'
            + '<button type="submit">Submit notice</button></div></form>';
        ov.innerHTML = html;
        document.body.appendChild(ov);
        var form = ov.querySelector('form');
        ov.querySelector('.dmcaCancel').addEventListener('click', function () { ov.remove(); });
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var body = { ItemId: dashedGuid(itemId) };
            FIELDS.forEach(function (f) { body[f[0]] = form.elements[f[0]].value.trim(); });
            body.GoodFaithStatement = form.elements.GoodFaithStatement.checked;
            body.AccuracyStatement = form.elements.AccuracyStatement.checked;
            fetch(window.ApiClient.serverAddress() + '/Dmca/report', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Authorization': auth },
                body: JSON.stringify(body)
            }).then(function (r) {
                if (r.ok) { window.alert('Notice sent. Thank you.'); ov.remove(); return; }
                if (r.status === 429) { window.alert('Too many reports, try again later.'); return; }
                return r.text().then(function (t) {
                    var msg = t;
                    try {
                        var j = JSON.parse(t);
                        if (j.errors) {
                            msg = Object.keys(j.errors).map(function (k) { return k + ': ' + j.errors[k].join(' '); }).join('\n');
                        } else if (j.title) { msg = j.title; }
                    } catch (e) { /* plain text */ }
                    window.alert('Could not send notice (' + r.status + ').\n' + msg);
                });
            }).catch(function () { window.alert('Could not send notice.'); });
        });
    }

    function report(itemId) { openForm(itemId); }

    function inject() {
        var id = itemIdFromHash();
        var pages = document.querySelectorAll('#itemDetailPage:not(.hide), .itemDetailPage:not(.hide)');
        pages.forEach(function (page) {
            var bar = page.querySelector('.mainDetailButtons');
            if (!bar) { return; }
            var existing = bar.querySelector('.' + BTN_CLASS);
            if (!id) { if (existing) { existing.remove(); } return; }
            if (existing) { existing.setAttribute('data-item-id', id); return; }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'button-flat detailButton emby-button ' + BTN_CLASS;
            btn.setAttribute('data-item-id', id);
            btn.title = 'Report DMCA violation';
            btn.innerHTML = '<span class="material-icons detailButton-icon" aria-hidden="true">report</span>'
                + '<span class="detailButton-title">Report DMCA</span>';
            btn.addEventListener('click', function () { report(btn.getAttribute('data-item-id')); });
            bar.appendChild(btn);
        });
    }

    new MutationObserver(inject).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('hashchange', inject);
    inject();
})();
