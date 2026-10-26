/* Yazma çalışması: görev seç → yaz → AI değerlendirmesi */
(async function () {
    const { api, esc, fmt, tts, store } = App;
    let tasks = [];
    let selected = null; // null = serbest yazı
    const DRAFT_KEY = 'write.draft';
    $(document).on('click', '[data-say-text]', function () { tts.speak($(this).data('say-text')); });

    const quota = await api.get('ai/quota').catch(() => null);
    if (quota) $('#quotaBadge').html(`<i class="bi bi-cpu me-1"></i>${fmt.number(quota.used)} / ${quota.limit == null ? '∞' : fmt.number(quota.limit)} token`);

    // Eski bir yazıyı görüntüleme
    const existingId = $('#writeApp').data('writing-id');
    if (existingId) {
        try {
            const r = await api.get('ai/writing/' + existingId);
            $('#editor').addClass('d-none');
            renderResult(r);
        } catch (e) { App.showError(e); }
        return;
    }

    try { tasks = await api.get('ai/writing/tasks'); } catch (e) { App.showError(e); return; }

    $('#tasks').html(tasks.map(t => `
        <div class="col-md-6">
            <button type="button" class="word-chip h-100" data-code="${esc(t.code)}">
                <div class="d-flex justify-content-between gap-2"><span class="w">${esc(t.titleTr)}</span><span class="level-badge">${esc(t.level)}</span></div>
                <div class="small text-body-secondary mt-1">${esc(t.instructionTr)}</div>
            </button>
        </div>`).join('') + `
        <div class="col-md-6">
            <button type="button" class="word-chip h-100" data-code="">
                <div class="w"><i class="bi bi-feather me-1"></i>Serbest yazı</div>
                <div class="small text-body-secondary mt-1">İstediğin konuda yaz: günlük, mesaj, mail…</div>
            </button>
        </div>`);

    function selectTask(code) {
        selected = code ? tasks.find(t => t.code === code) : null;
        $('#tasks .word-chip').removeClass('known').filter(`[data-code="${code || ''}"]`).addClass('known');
        $('#freeTopic').toggleClass('d-none', !!selected);
        if (selected) {
            $('#taskInfo').html(`<b>${esc(selected.titleTr)}:</b> ${esc(selected.instructionTr)}
                ${selected.hintEn ? `<div class="mt-1 text-body-secondary">İpucu: <i lang="en">${esc(selected.hintEn)}</i></div>` : ''}`);
            $('#minWords').text(`(önerilen en az ${selected.minWords})`);
        } else {
            $('#taskInfo').text('Serbest yazı: konuyu istersen yukarıya yaz.');
            $('#minWords').text('');
        }
        $('#text').trigger('focus');
    }

    // Taslak (sayfa kapanırsa yazdıkların kaybolmasın)
    const draft = store.get(DRAFT_KEY);
    if (draft) { try { const d = JSON.parse(draft); $('#text').val(d.text || ''); if (d.code !== undefined) selectTask(d.code); } catch { } }
    updateCount();

    function updateCount() {
        const words = ($('#text').val().match(/[A-Za-z']+/g) || []).length;
        $('#wordCount').text(words);
    }

    $('#tasks').on('click', '.word-chip', function () { selectTask($(this).data('code')); saveDraft(); });
    $('#text').on('input', () => { updateCount(); saveDraft(); });
    function saveDraft() { store.set(DRAFT_KEY, JSON.stringify({ text: $('#text').val(), code: selected?.code ?? '' })); }

    $('#submitBtn').on('click', async function () {
        const text = $('#text').val().trim();
        if (text.length < 15) return App.toast('Biraz daha yaz (en az 15 karakter)', 'info');
        App.busy(this, true, 'AI okuyor…');
        try {
            const r = await api.post('ai/writing', {
                taskCode: selected?.code || null,
                promptText: selected ? null : ($('#freeTopic').val().trim() || null),
                text
            });
            store.set(DRAFT_KEY, '');
            history.replaceState(null, '', '/Practice/Write/' + r.id);
            $('#editor').addClass('d-none');
            renderResult(r);
        } catch (e) { App.showError(e); }
        App.busy(this, false);
    });

    function renderResult(r) {
        const typeNames = { grammar: 'dilbilgisi', vocabulary: 'kelime', spelling: 'yazım', 'word-order': 'kelime sırası', punctuation: 'noktalama', style: 'üslup' };
        $('#result').removeClass('d-none').html(`
            <div class="card mb-3">
                <div class="card-body d-flex flex-column flex-sm-row gap-3 align-items-sm-center">
                    <div class="score-circle" style="--p:${r.score}"><span>${r.score}</span></div>
                    <div class="flex-grow-1">
                        <div class="mb-1"><span class="level-badge">Tahmini seviye: ${esc(r.level || '-')}</span>
                             <span class="small text-body-secondary ms-2">${fmt.dateTime(r.createdAt)}</span></div>
                        <div>${esc(r.summaryTr)}</div>
                        ${r.promptText ? `<div class="small text-body-secondary mt-1"><i class="bi bi-card-text me-1"></i>${esc(r.promptText)}</div>` : ''}
                    </div>
                </div>
            </div>

            <div class="row g-3 mb-3">
                <div class="col-md-6">
                    <div class="card h-100"><div class="card-header">Senin yazın</div>
                        <div class="card-body" lang="en" style="white-space:pre-wrap">${esc(r.userText)}</div></div>
                </div>
                <div class="col-md-6">
                    <div class="card h-100"><div class="card-header d-flex justify-content-between align-items-center">Düzeltilmiş hali
                        <button class="btn btn-sm btn-icon" data-say-text="${esc(r.correctedText)}"><i class="bi bi-volume-up"></i></button></div>
                        <div class="card-body" lang="en" style="white-space:pre-wrap">${esc(r.correctedText)}</div></div>
                </div>
            </div>

            ${r.mistakes?.length ? `
            <div class="card mb-3">
                <div class="card-header">Hatalar ve açıklamaları (${r.mistakes.length})</div>
                <div class="list-group list-group-flush">
                    ${r.mistakes.map(m => `
                        <div class="list-group-item py-3">
                            <div class="d-flex flex-wrap align-items-center gap-1">
                                <span class="text-danger text-decoration-line-through" lang="en">${esc(m.original)}</span>
                                <i class="bi bi-arrow-right mx-1"></i>
                                <span class="text-success fw-semibold" lang="en">${esc(m.correction)}</span>
                                ${m.type ? `<span class="badge bg-secondary-subtle text-secondary ms-1">${esc(typeNames[m.type] || m.type)}</span>` : ''}
                            </div>
                            <div class="small text-body-secondary mt-1">${esc(m.explanationTr)}</div>
                        </div>`).join('')}
                </div>
            </div>` : `<div class="alert alert-success border-0">Hiç hata bulunamadı, harika! 🎉</div>`}

            <div class="card mb-3">
                <div class="card-header d-flex justify-content-between align-items-center">
                    <span><i class="bi bi-stars me-1 text-warning"></i>Doğal (native) hali</span>
                    <button class="btn btn-sm btn-icon" data-say-text="${esc(r.betterVersion)}"><i class="bi bi-volume-up"></i></button>
                </div>
                <div class="card-body" lang="en" style="white-space:pre-wrap">${esc(r.betterVersion)}</div>
            </div>

            ${r.newPhrases?.length ? `
            <div class="card mb-3">
                <div class="card-header">Öğrenmeye değer kalıplar</div>
                <div class="list-group list-group-flush">
                    ${r.newPhrases.map(p => `
                        <div class="list-group-item d-flex justify-content-between align-items-center gap-2">
                            <div><div class="fw-semibold" lang="en">${esc(p.en)}</div><div class="small text-body-secondary">${esc(p.tr)}</div></div>
                            <button class="btn btn-sm btn-icon" data-say-text="${esc(p.en)}"><i class="bi bi-volume-up"></i></button>
                        </div>`).join('')}
                </div>
            </div>` : ''}

            <div class="d-flex gap-2 flex-wrap">
                <a href="/Practice/Write" class="btn btn-primary"><i class="bi bi-pencil me-1"></i>Yeni yazı</a>
                <a href="/Practice" class="btn btn-outline-primary">AI Pratik'e dön</a>
            </div>`);
        window.scrollTo({ top: 0, behavior: 'smooth' });
    }
})();
