/* Kart çalışma ekranı (FSRS). Kart: önce soru → "Cevabı göster" → 4 puan butonu. */
(async function () {
    const { api, esc, tts, posNames } = App;
    const scenarioId = $('#studyApp').data('scenario-id') || null;
    const REQUEUE_MINUTES = 20; // bu süre içinde tekrar gelecek kartlar aynı oturumda tekrar gösterilir

    const ratings = [
        { key: 'Again', n: 1, label: 'Bilemedim', cls: 'btn-outline-danger' },
        { key: 'Hard', n: 2, label: 'Zor', cls: 'btn-outline-warning' },
        { key: 'Good', n: 3, label: 'Bildim', cls: 'btn-outline-success' },
        { key: 'Easy', n: 4, label: 'Çok kolay', cls: 'btn-outline-primary' }
    ];

    App.audio.prefsControl('#audioPrefs');

    let queue = [];
    let current = null;
    let revealed = false;
    let shownAt = 0;
    let busy = false;
    const stats = { answered: 0, correct: 0, again: 0, startedAt: Date.now(), total: 0, done: 0 };

    // ---------- Oturumu yükle ----------
    let session;
    try {
        session = await api.get('study/session', scenarioId ? { scenarioId } : undefined);
    } catch (e) {
        App.showError(e);
        return;
    }

    queue = session.items.map(i => ({ ...i, dueAt: null }));
    stats.total = queue.length;
    $('#scenarioName').text(session.scenarioTitle ? '📍 ' + session.scenarioTitle : 'Karışık çalışma');
    await tts.init();

    if (!queue.length) return renderEmpty();
    next();

    // ---------- Akış ----------
    function next() {
        updateCounters();
        if (!queue.length) return renderFinished();

        // Zamanı gelmemiş (öğrenme adımındaki) kartları sona at
        const now = Date.now();
        const idx = queue.findIndex(i => !i.dueAt || i.dueAt <= now);
        if (idx === -1) return renderWaiting();

        current = queue.splice(idx, 1)[0];
        revealed = false;
        shownAt = Date.now();
        renderCard();
    }

    function updateCounters() {
        const pending = queue.length + (current ? 1 : 0);
        const fresh = queue.filter(i => i.isNew).length + (current?.isNew ? 1 : 0);
        $('#cntNew').text(`${fresh} yeni`);
        $('#cntReview').text(`${pending - fresh} tekrar`);
        const done = stats.done;
        const total = Math.max(stats.total, done + pending);
        $('#progressText').text(`${done}/${total}`);
        $('#progressBar').css('width', total ? (100 * done / total) + '%' : '0%');
    }

    // ---------- Kart çizimi ----------
    function highlight(ex) {
        if (!ex) return '';
        const t = ex.textEn;
        if (ex.highlightStart == null) return esc(t);
        const s = ex.highlightStart, l = ex.highlightLength;
        return esc(t.slice(0, s)) + '<mark>' + esc(t.slice(s, s + l)) + '</mark>' + esc(t.slice(s + l));
    }

    function renderCard() {
        const c = current;
        const badge = c.isNew ? '<span class="badge bg-primary mb-3">YENİ</span>' : '';
        let front, back;

        if (c.word) {
            const w = c.word;
            front = `
                ${badge}
                <div class="d-flex justify-content-center align-items-center gap-3 mb-2">
                    <div class="flash-word">${esc(w.lemma)}</div>
                    <button class="speak-btn" data-say="word" title="Dinle (R)"><i class="bi bi-volume-up"></i></button>
                </div>
                <div class="flash-ipa mb-1">${esc(w.ipa || '')}</div>
                <div class="small text-body-secondary mb-4">${esc(posNames[w.partOfSpeech] || '')} · <span class="level-badge">${esc(w.level)}</span></div>
                ${c.example ? `
                <div class="flash-example">
                    ${highlight(c.example)}
                    <button class="btn btn-sm btn-icon ms-1" data-say="example" title="Cümleyi dinle"><i class="bi bi-volume-up"></i></button>
                </div>` : ''}`;
            back = `
                <div class="flash-answer">
                    <div class="flash-meaning mb-2">${esc(w.meaningTr)}</div>
                    ${w.definitionEn ? `<div class="text-body-secondary small mb-2"><i>${esc(w.definitionEn)}</i></div>` : ''}
                    ${c.example ? `<div class="mb-2">${esc(c.example.textTr)}</div>` : ''}
                    ${w.usageNoteTr ? `<div class="alert alert-light border small text-start mb-0 mt-3"><i class="bi bi-lightbulb text-warning me-1"></i>${esc(w.usageNoteTr)}</div>` : ''}
                    <div class="mt-3" data-shadow-scope>${App.shadow.button(c.example?.textEn || w.lemma, c.example ? 'Cümleyi tekrar et' : 'Tekrar et')}</div>
                </div>`;
        } else {
            const s = c.sentence;
            front = `
                ${badge}
                <div class="small text-body-secondary mb-3"><i class="bi bi-headphones me-1"></i>Dinle ve anlamaya çalış${s.speakerRole ? ` · <b>${esc(s.speakerRole)}</b>` : ''}</div>
                <div class="d-flex justify-content-center gap-3 mb-4">
                    <button class="speak-btn speak-lg" data-say="sentence" title="Dinle (R)"><i class="bi bi-volume-up"></i></button>
                    <button class="speak-btn align-self-end" data-say="slow" title="Yavaş dinle"><i class="bi bi-hourglass-split"></i></button>
                </div>
                <div class="flash-example blur-text" id="sentenceText" title="Metni görmek için tıkla">${esc(s.textEn)}</div>
                <div class="small text-body-secondary mt-2">Önce sadece dinle. Zorlanırsan metne tıkla.</div>`;
            back = `
                <div class="flash-answer">
                    <div class="flash-meaning" style="font-size:1.25rem">${esc(s.textTr)}</div>
                    <div class="mt-3" data-shadow-scope>${App.shadow.button(s.textEn, 'Cümleyi tekrar et')}</div>
                </div>`;
        }

        $('#stage').html(`
            <div class="card flashcard">
                <div class="card-body" lang="en">
                    <div id="front">${front}</div>
                    <div id="back" class="d-none">${back}</div>
                </div>
            </div>`);
        $('#actions').html(`<button class="btn btn-primary btn-lg w-100" id="revealBtn">Cevabı göster</button>`);

        // Otomatik seslendirme
        setTimeout(() => say(c.word ? 'word' : 'sentence'), 250);
    }

    function reveal() {
        if (revealed || !current) return;
        revealed = true;
        $('#back').removeClass('d-none');
        $('#sentenceText').removeClass('blur-text');
        const previews = Object.fromEntries((current.previews || []).map(p => [p.rating, p.label]));
        $('#actions').html(`
            <div class="rating-bar">
                ${ratings.map(r => `
                    <button class="btn ${r.cls}" data-rating="${r.key}">
                        ${r.label}<small>${esc(previews[r.key] || '')}</small>
                    </button>`).join('')}
            </div>`);
        if (current.sentence) say('sentence');
    }

    function say(what) {
        const c = current; if (!c) return;
        const $btn = $(`[data-say="${what}"]`).addClass('speaking');
        // Hazır doğal ses (Kokoro MP3) varsa o, yoksa tarayıcı sesi
        // Kendi eklediğin cümlelerin (yakalama / seyahat kiti) hazır MP3'ü yok: Gemini doğal sesi, olmazsa tarayıcı
        const personal = c.sentence && !c.sentence.audioUrl;
        let p;
        if (what === 'word') p = App.audio.speak({ item: c.word, text: c.word?.lemma });
        else if (what === 'example') p = App.audio.speak({ item: c.example, text: c.example?.textEn });
        else if (what === 'slow') p = personal ? App.audio.speakAi(c.sentence.textEn, null, 0.7) : App.audio.speak({ item: c.sentence, text: c.sentence?.textEn, rate: 0.7 });
        else p = personal ? App.audio.speakAi(c.sentence.textEn) : App.audio.speak({ item: c.sentence, text: c.sentence?.textEn });
        Promise.resolve(p).finally(() => $btn.removeClass('speaking'));
    }

    // ---------- Cevap ----------
    async function answer(rating) {
        if (!revealed || busy || !current) return;
        busy = true;
        $('#actions button').prop('disabled', true);
        const c = current;

        try {
            const res = await api.post('study/answer', {
                cardId: c.cardId,
                cardType: c.cardType,
                wordId: c.wordId,
                sentenceId: c.sentenceId,
                rating,
                durationMs: Date.now() - shownAt
            });

            stats.answered++;
            if (rating === 'Good' || rating === 'Easy') stats.correct++;
            if (rating === 'Again') stats.again++;
            if (res.becameLeech) App.toast(`"${c.word?.lemma || 'Bu cümle'}" zorlandıkların listesine eklendi`, 'info');

            // Kısa süre sonra tekrar gelecekse (öğrenme adımı: 1-10 dk) aynı oturumda kuyruğa geri koy.
            // Süre sunucudan saniye olarak gelir: tarayıcının saat dilimi farklı olsa da doğru çalışır.
            const gapMs = res.dueInSeconds * 1000;
            if (gapMs < REQUEUE_MINUTES * 60000) {
                queue.push({ ...c, cardId: res.cardId, isNew: false, state: res.state, previews: res.previews, dueAt: Date.now() + gapMs });
            } else {
                stats.done++;
            }
            current = null;
            busy = false;
            next();
        } catch (e) {
            busy = false;
            $('#actions button').prop('disabled', false);
            App.showError(e);
        }
    }

    // ---------- Ekranlar ----------
    function renderEmpty() {
        $('#stage').html(`
            <div class="card"><div class="card-body empty-state">
                <i class="bi bi-cup-hot"></i>
                <h2 class="h5 text-body">Şu an çalışılacak kart yok</h2>
                <p>Bugünkü tekrarların bitti ve günlük yeni kelime hakkını kullandın${session.scenarioTitle ? ' (veya bu senaryodaki her şeyi çalıştın)' : ''}.</p>
                <div class="d-flex gap-2 justify-content-center flex-wrap">
                    <a href="/" class="btn btn-primary">Durumuma bak</a>
                    <a href="/Study" class="btn btn-outline-primary">Senaryoları dinle</a>
                </div>
                <p class="small mt-3 mb-0">Günlük yeni kelime limitini Profil &gt; Çalışma ayarlarından değiştirebilirsin.</p>
            </div></div>`);
        $('#actions').empty();
        updateCounters();
    }

    function renderWaiting() {
        const soonest = Math.min(...queue.map(i => i.dueAt));
        const mins = Math.max(1, Math.ceil((soonest - Date.now()) / 60000));
        $('#stage').html(`
            <div class="card"><div class="card-body empty-state">
                <i class="bi bi-hourglass-split"></i>
                <h2 class="h5 text-body">Kısa bir mola</h2>
                <p>${queue.length} kart ${mins} dakika içinde tekrar gelecek. Hafızaya oturması için biraz beklemek iyi.</p>
                <div class="d-flex gap-2 justify-content-center">
                    <button class="btn btn-primary" id="showNow">Şimdi göster</button>
                    <button class="btn btn-outline-secondary" id="finishNow">Bitir</button>
                </div>
            </div></div>`);
        $('#actions').empty();
        clearTimeout(window._waitTimer);
        window._waitTimer = setTimeout(next, soonest - Date.now() + 200);
    }

    function renderFinished() {
        const mins = Math.max(1, Math.round((Date.now() - stats.startedAt) / 60000));
        const acc = stats.answered ? Math.round(100 * stats.correct / stats.answered) : 0;
        $('#stage').html(`
            <div class="card"><div class="card-body text-center py-5">
                <div style="font-size:3rem">🎉</div>
                <h2 class="h4 fw-bold mt-2">Oturum tamamlandı!</h2>
                <p class="text-body-secondary">Harika iş çıkardın. Düzenli tekrar, kalıcı öğrenmenin anahtarı.</p>
                <div class="row g-2 my-4 justify-content-center">
                    <div class="col-4 col-md-3"><div class="card stat-card"><div class="stat-value">${stats.answered}</div><div class="stat-label">cevap</div></div></div>
                    <div class="col-4 col-md-3"><div class="card stat-card"><div class="stat-value">%${acc}</div><div class="stat-label">doğruluk</div></div></div>
                    <div class="col-4 col-md-3"><div class="card stat-card"><div class="stat-value">${mins}</div><div class="stat-label">dakika</div></div></div>
                </div>
                <div class="d-flex gap-2 justify-content-center flex-wrap">
                    <a href="/" class="btn btn-primary">Durumuma bak</a>
                    <a href="/Study/Session${scenarioId ? '?scenarioId=' + scenarioId : ''}" class="btn btn-outline-primary">Devam et</a>
                </div>
            </div></div>`);
        $('#actions').empty();
        $('#progressBar').css('width', '100%');
    }

    // ---------- Olaylar ----------
    $(document).on('click', '#revealBtn', reveal);
    $(document).on('click', '[data-rating]', function () { answer($(this).data('rating')); });
    $(document).on('click', '[data-say]', function (e) { e.stopPropagation(); say($(this).data('say')); });
    $(document).on('click', '#sentenceText.blur-text', function () { $(this).removeClass('blur-text'); });
    $(document).on('click', '#showNow', function () { queue.forEach(i => i.dueAt = null); clearTimeout(window._waitTimer); next(); });
    $(document).on('click', '#finishNow', function () { queue = []; clearTimeout(window._waitTimer); renderFinished(); });

    $(document).on('keydown', function (e) {
        if ($(e.target).is('input, textarea, select')) return;
        if (e.code === 'Space' || e.key === 'Enter') { e.preventDefault(); if (!revealed) reveal(); }
        else if (e.key === 'r' || e.key === 'R') say(current?.word ? 'word' : 'sentence');
        else if (revealed && ['1', '2', '3', '4'].includes(e.key)) answer(ratings[+e.key - 1].key);
    });

    window.addEventListener('beforeunload', () => tts.stop());
})();
