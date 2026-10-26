/* AI ile sesli rol yapma / serbest sohbet */
(async function () {
    const { api, esc, fmt, tts, stt, store } = App;
    const $app = $('#talkApp');
    let conv = null;
    let sending = false;

    // Tercihler bu tarayıcıda hatırlanır
    const prefs = ['autoSpeak', 'handsFree', 'showTr'];
    prefs.forEach(p => { const v = store.get('talk.' + p); if (v !== null) $('#' + p).prop('checked', v === '1'); });
    $(prefs.map(p => '#' + p).join(',')).on('change', function () {
        store.set('talk.' + this.id, this.checked ? '1' : '0');
        if (this.id === 'showTr') $('.msg .tr').toggleClass('d-none', !this.checked);
        if (this.id === 'handsFree' && this.checked) $('#autoSpeak').prop('checked', true).trigger('change');
    });

    // Doğal ses (Gemini) tercihi: site genelinde App.audio bu ayarı okur
    $('#naturalVoice').prop('checked', store.get('naturalVoice') !== '0').on('change', function () { store.set('naturalVoice', this.checked ? '1' : '0'); });

    // Ses tanıma yöntemi: Otomatik (tarayıcı, olmazsa sunucu) / Tarayıcı / Sunucu (Whisper)
    $('#sttMethod').val(stt.preference).on('change', function () {
        stt.preference = this.value;
        App.toast(this.value === 'whisper' ? 'Sunucu ile ses tanıma (Whisper) seçildi' : this.value === 'browser' ? 'Tarayıcı ses tanıması seçildi' : 'Otomatik seçim');
    });

    await tts.init();
    if (!stt.supported) {
        $('#micBtn').prop('disabled', true).attr('title', 'Tarayıcın mikrofon kaydını desteklemiyor');
        $('#handsFree').prop('disabled', true).prop('checked', false);
    }

    // ---------- Başlat / yükle ----------
    try {
        const existingId = $app.data('conversation-id');
        if (existingId) {
            conv = await api.get('ai/conversations/' + existingId);
        } else {
            $('#typing').removeClass('d-none');
            conv = await api.post('ai/conversations', {
                mode: $app.data('mode') || 'RolePlay',
                scenarioId: $app.data('scenario-id') || null
            });
            history.replaceState(null, '', '/Practice/Talk/' + conv.id);
        }
    } catch (e) {
        $('#typing').addClass('d-none');
        $('#chatTitle').text('Konuşma başlatılamadı');
        await App.showError(e);
        return;
    } finally {
        $('#typing').addClass('d-none');
    }

    renderHeader();
    conv.messages.forEach(m => appendMessage(m, false));
    const last = conv.messages[conv.messages.length - 1];
    if (conv.endedAt) {
        lockInput();
        renderSummary(conv.summary);
    } else {
        $('#finishBtn').prop('disabled', false);
        if (last?.role === 'Assistant') {
            renderSuggestions(last.suggestions);
            if (conv.messages.length === 1) speakThenListen(last.content);
        }
    }
    refreshQuota();

    // ---------- Çizim ----------
    function renderHeader() {
        $('#chatTitle').text(conv.title || 'Konuşma');
        $('#chatRoles').text(conv.mode === 'FreeTalk'
            ? 'Serbest sohbet · AI senin İngilizce konuşan arkadaşın'
            : `Sen: ${conv.userRole} · AI: ${conv.aiRole}`);
        document.title = (conv.title || 'Konuşma') + ' · EnglishApp';
    }

    function appendMessage(m, animate = true) {
        const showTr = $('#showTr').is(':checked');
        let html;
        if (m.role === 'Assistant') {
            html = `
                <div class="msg ai" data-id="${m.id}">
                    <div class="bubble-c" lang="en">${esc(m.content)}</div>
                    ${m.translation ? `<div class="tr ${showTr ? '' : 'd-none'}">${esc(m.translation)}</div>` : ''}
                    <div class="tools">
                        <button class="btn btn-sm" data-say="normal" title="Dinle"><i class="bi bi-volume-up"></i></button>
                        <button class="btn btn-sm" data-say="slow" title="Yavaş dinle"><i class="bi bi-hourglass-split"></i></button>
                        ${m.translation ? `<button class="btn btn-sm" data-toggle-tr title="Türkçesi"><i class="bi bi-translate"></i> Türkçe</button>` : ''}
                    </div>
                </div>`;
        } else {
            const f = m.feedback;
            let fb = '';
            if (f && !f.isCorrect && f.corrected) {
                fb = `<div class="fb fix"><i class="bi bi-pencil me-1"></i><span class="corr" lang="en">${esc(f.corrected)}</span>
                      ${f.explanationTr ? `<div class="mt-1">${esc(f.explanationTr)}</div>` : ''}
                      <button class="btn btn-sm btn-link p-0 mt-1" data-say-text="${esc(f.corrected)}"><i class="bi bi-volume-up"></i> Doğrusunu dinle</button></div>`;
            } else if (f && f.betterWay) {
                fb = `<div class="fb ok"><i class="bi bi-check2-circle me-1"></i>Doğru! Daha doğal: <span lang="en"><b>${esc(f.betterWay)}</b></span></div>`;
            } else if (f) {
                fb = `<div class="fb ok"><i class="bi bi-check2-circle me-1"></i>Doğru 👍</div>`;
            }
            html = `<div class="msg me" data-id="${m.id}"><div class="bubble-c" lang="en">${esc(m.content)}</div>${fb}</div>`;
        }
        const $el = $(html).appendTo('#messages');
        $el.data('msg', m);
        if (animate) $el.hide().fadeIn(150);
        scrollDown();
    }

    function renderSuggestions(list) {
        $('#suggestions').html((list || []).map(s =>
            `<button class="btn btn-sm btn-outline-secondary" data-suggest="${esc(s)}" lang="en"><i class="bi bi-lightbulb me-1"></i>${esc(s)}</button>`).join(''));
    }

    function scrollDown() {
        const el = document.getElementById('chatScroll');
        el.scrollTop = el.scrollHeight;
    }

    function lockInput() {
        $('#inputBar, #suggestions').addClass('d-none');
        $('#finishBtn').prop('disabled', true);
        stt.stop();
    }

    async function refreshQuota(q) {
        try {
            q = q || await api.get('ai/quota');
            $('#quotaBadge').text(`${fmt.number(q.used)} / ${q.limit == null ? '∞' : fmt.number(q.limit)} token`);
        } catch { }
    }

    // ---------- Ses ----------
    async function speakThenListen(text) {
        if ($('#autoSpeak').is(':checked')) await App.audio.speakAi(text, conv.ttsVoice);
        if ($('#handsFree').is(':checked') && !conv.endedAt) listen(true);
    }

    let transcribing = false;
    const defaultPlaceholder = $('#msgInput').attr('placeholder');

    async function listen(autoSend) {
        if (transcribing) return;
        // Dinlerken tekrar basınca: konuşmayı bitir ve yazıya çevir
        if (stt.listening) { stt.finish(); return; }
        try {
            const text = await stt.start({
                onInterim: t => $('#msgInput').val(t),
                onFinal: t => $('#msgInput').val(t),
                onState: st => {
                    if (st === 'listening') {
                        setMic(true);
                        $('#msgInput').attr('placeholder', stt.method === 'whisper'
                            ? 'Dinliyorum… Bitince bir an sus ya da ⏹ butonuna bas'
                            : 'Dinliyorum…');
                    } else if (st === 'transcribing') {
                        transcribing = true;
                        $('#micBtn').prop('disabled', true).removeClass('listening').html('<span class="spinner-border spinner-border-sm"></span>');
                        $('#msgInput').attr('placeholder', 'Sesin yazıya çevriliyor…');
                    } else {
                        transcribing = false;
                        setMic(false);
                    }
                }
            });
            if (text) {
                $('#msgInput').val(text);
                if (autoSend) send(true);
                else $('#msgInput').trigger('focus');
            } else if (!conv.endedAt) {
                App.toast('Ses duyulmadı, tekrar dene', 'info');
            }
        } catch (e) {
            transcribing = false;
            setMic(false);
            App.showError(e);
        }
    }

    function setMic(on) {
        $('#micBtn').prop('disabled', !stt.supported).toggleClass('listening', on)
            .html(on ? '<i class="bi bi-stop-fill"></i>' : '<i class="bi bi-mic-fill"></i>');
        if (!on) $('#msgInput').attr('placeholder', defaultPlaceholder);
    }

    // ---------- Gönder ----------
    async function send(fromVoice = false) {
        const text = $('#msgInput').val().trim();
        if (!text || sending || conv.endedAt) return;
        sending = true;
        tts.stop();
        $('#msgInput').val('').css('height', '');
        $('#sendBtn, #micBtn').prop('disabled', true);
        $('#suggestions').empty();

        // Mesajı hemen göster (değerlendirme gelince güncellenir)
        const temp = $(`<div class="msg me"><div class="bubble-c" lang="en">${esc(text)}</div></div>`).appendTo('#messages');
        $('#typing').removeClass('d-none');
        scrollDown();

        try {
            const r = await api.post(`ai/conversations/${conv.id}/messages`, { text, fromVoice });
            temp.remove();
            appendMessage(r.userMessage);
            appendMessage(r.reply);
            conv.messages.push(r.userMessage, r.reply);
            renderSuggestions(r.reply.suggestions);
            refreshQuota(r.quota);
            if (r.ended) {
                $('#handsFree').prop('checked', false);
                Swal.fire({
                    icon: 'success', title: 'Konuşma tamamlandı 🎉', text: 'Şimdi değerlendirmeni görmek ister misin?',
                    showCancelButton: true, confirmButtonText: 'Değerlendir', cancelButtonText: 'Devam et', confirmButtonColor: '#4f46e5'
                }).then(x => { if (x.isConfirmed) finish(); });
                if ($('#autoSpeak').is(':checked')) App.audio.speakAi(r.reply.content, conv.ttsVoice);
            } else {
                speakThenListen(r.reply.content);
            }
        } catch (e) {
            temp.remove();
            $('#msgInput').val(text);
            App.showError(e);
        } finally {
            $('#typing').addClass('d-none');
            $('#sendBtn').prop('disabled', false);
            $('#micBtn').prop('disabled', !stt.supported);
            sending = false;
            scrollDown();
        }
    }

    // ---------- Bitir ve değerlendir ----------
    async function finish() {
        const userCount = conv.messages.filter(m => m.role === 'User').length;
        if (userCount < 2 && !await App.confirm('Konuşma bitirilsin mi?', 'En az 2 mesaj yazmadığın için değerlendirme yapılmayacak.', 'Bitir', 'question', false)) return;
        App.busy('#finishBtn', true, 'Değerlendiriliyor');
        tts.stop();
        try {
            conv = await api.post(`ai/conversations/${conv.id}/finish`);
            lockInput();
            renderSummary(conv.summary);
            refreshQuota();
        } catch (e) {
            App.busy('#finishBtn', false);
            App.showError(e);
        }
    }

    function renderSummary(s) {
        if (!s) {
            $('#summary').html(`<div class="card"><div class="card-body text-center">Konuşma bitti.
                <a href="/Practice" class="btn btn-sm btn-primary ms-2">Yeni konuşma</a></div></div>`);
            return;
        }
        $('#summary').html(`
            <div class="card">
                <div class="card-header"><i class="bi bi-clipboard-data me-2"></i>Değerlendirmen</div>
                <div class="card-body">
                    <div class="d-flex flex-column flex-sm-row gap-3 align-items-sm-center mb-3">
                        <div class="score-circle" style="--p:${s.score}"><span>${s.score}</span></div>
                        <div>
                            <div class="mb-2">${esc(s.overallTr)}</div>
                            ${(s.strengthsTr || []).map(x => `<div class="small text-success"><i class="bi bi-check2 me-1"></i>${esc(x)}</div>`).join('')}
                        </div>
                    </div>
                    ${s.mistakes?.length ? `
                        <h6 class="fw-bold mt-4">Üzerinde çalışman gerekenler</h6>
                        <div class="list-group mb-3">
                            ${s.mistakes.map(m => `
                                <div class="list-group-item">
                                    <div><span class="text-danger text-decoration-line-through" lang="en">${esc(m.original)}</span>
                                         <i class="bi bi-arrow-right mx-1"></i><span class="text-success fw-semibold" lang="en">${esc(m.correction)}</span>
                                         <button class="btn btn-sm btn-link p-0 ms-1" data-say-text="${esc(m.correction)}"><i class="bi bi-volume-up"></i></button></div>
                                    <div class="small text-body-secondary">${esc(m.explanationTr)}</div>
                                </div>`).join('')}
                        </div>` : ''}
                    ${s.usefulPhrases?.length ? `
                        <h6 class="fw-bold">Bu durumda işine yarayacak kalıplar</h6>
                        <div class="list-group mb-3">
                            ${s.usefulPhrases.map(p => `
                                <div class="list-group-item d-flex justify-content-between align-items-center gap-2">
                                    <div><div class="fw-semibold" lang="en">${esc(p.en)}</div><div class="small text-body-secondary">${esc(p.tr)}</div></div>
                                    <button class="btn btn-sm btn-icon" data-say-text="${esc(p.en)}"><i class="bi bi-volume-up"></i></button>
                                </div>`).join('')}
                        </div>` : ''}
                    <div class="d-flex gap-2 flex-wrap">
                        ${conv.scenarioId ? `<a href="/Practice/Talk?scenarioId=${conv.scenarioId}" class="btn btn-primary"><i class="bi bi-arrow-repeat me-1"></i>Bu senaryoyu tekrar konuş</a>` : ''}
                        <a href="/Practice" class="btn btn-outline-primary">AI Pratik'e dön</a>
                    </div>
                </div>
            </div>`);
        $('#summary')[0].scrollIntoView({ behavior: 'smooth' });
    }

    // ---------- Olaylar ----------
    $('#sendBtn').on('click', () => send(false));
    $('#micBtn').on('click', () => listen($('#handsFree').is(':checked')));
    $('#finishBtn').on('click', finish);
    $('#msgInput').on('keydown', e => {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(false); }
    }).on('input', function () { this.style.height = ''; this.style.height = Math.min(this.scrollHeight, 120) + 'px'; });

    $('#messages').on('click', '[data-say]', function () {
        const m = $(this).closest('.msg').data('msg');
        App.audio.speakAi(m.content, conv.ttsVoice, $(this).data('say') === 'slow' ? 0.75 : 1);
    });
    $(document).on('click', '[data-say-text]', function () { App.audio.speakAi($(this).data('say-text'), conv?.ttsVoice); });
    $('#messages').on('click', '[data-toggle-tr]', function () { $(this).closest('.msg').find('.tr').toggleClass('d-none'); });
    $('#suggestions').on('click', '[data-suggest]', function () {
        $('#msgInput').val($(this).data('suggest')).trigger('focus');
        App.audio.speakAi($(this).data('suggest'), conv?.ttsVoice);
    });
    $(document).on('keydown', e => {
        if ((e.key === 'm' || e.key === 'M') && !$(e.target).is('input, textarea') && !conv?.endedAt) { e.preventDefault(); $('#micBtn').trigger('click'); }
    });
    window.addEventListener('beforeunload', () => { tts.stop(); stt.stop(); });
})();
