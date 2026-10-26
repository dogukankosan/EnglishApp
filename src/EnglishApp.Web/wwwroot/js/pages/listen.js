/* Dinleme testi: metinsiz dinle → Türkçe soruları cevapla → sonuç + metin */
(async function () {
    const { api, esc, fmt, tts } = App;
    const scenarioId = $('#listenApp').data('scenario-id') || null;
    const MAX_PLAYS = 3;

    if (!scenarioId) return renderList();
    return renderQuiz(scenarioId);

    // ---------- Liste ----------
    async function renderList() {
        $('#listView').removeClass('d-none');
        try {
            const [list, dash] = await Promise.all([api.get('listening'), api.get('stats/dashboard')]);
            const s = dash.listening;
            $('#summaryScore').text(s.averagePercent30d != null ? '%' + s.averagePercent30d : '-');
            $('#summaryText').text(s.attempts30d
                ? `Son 30 günde ${s.attempts30d} test çözdün. ${s.testDue ? 'Bu ayki ölçüm için yeni bir test çöz.' : 'Ayda en az bir test çözmen seviyeni takip etmeni sağlar.'}`
                : 'Henüz test çözmedin. Bir senaryo seç ve başla: ayda bir test, anlama seviyendeki gelişimi gösterir.');

            $('#scenarioList').html(list.map(x => `
                <a href="/Practice/Listen/${x.scenarioId}" class="list-group-item list-group-item-action d-flex align-items-center gap-3 py-3">
                    <span class="scenario-icon" style="width:42px;height:42px;font-size:1.15rem"><i class="bi ${esc(x.iconName || 'bi-chat')}"></i></span>
                    <div class="flex-grow-1">
                        <div class="fw-semibold">${esc(x.titleTr)} <span class="level-badge ms-1">${esc(x.level)}</span></div>
                        <div class="small text-body-secondary">${x.attempts ? `${x.attempts} kez çözdün · son: %${x.lastPercent} · en iyi: %${x.bestPercent}` : 'Henüz çözülmedi'}</div>
                    </div>
                    <i class="bi bi-chevron-right text-body-secondary"></i>
                </a>`).join('') || '<div class="empty-state">Senaryo yok.</div>');
        } catch (e) { App.showError(e); }
    }

    // ---------- Test ----------
    async function renderQuiz(id) {
        $('#quizView').removeClass('d-none');
        App.audio.prefsControl('#audioPrefs');
        $('#questions').html(`<div class="card"><div class="card-body text-center py-4">
            <div class="spinner-border text-primary mb-2"></div><div class="small text-body-secondary">Test hazırlanıyor… (ilk açılışta sorular yapay zekayla oluşturulur)</div></div></div>`);

        let quiz;
        try { quiz = await api.get('listening/' + id); }
        catch (e) { $('#questions').empty(); return App.showError(e); }

        document.title = 'Dinleme: ' + quiz.titleTr + ' · EnglishApp';
        $('#quizTitle').text(quiz.titleTr);
        $('#quizSubtitle').text(`${quiz.titleEn} · ${quiz.level}`);

        const answers = new Array(quiz.questions.length).fill(-1);
        let plays = 0, playing = false;
        await tts.init();

        // Konuşmacılara göre farklı tarayıcı sesi (MP3 yoksa)
        const speakers = [...new Set(quiz.lines.map(l => l.speakerRole))];
        const main = tts.pickVoice();
        const second = tts.voices.find(v => v !== main && /en-(US|GB)/i.test(v.lang)) || main;

        $('#playBtn').on('click', async function () {
            if (playing) { playing = false; App.audio.stop(); return; }
            if (plays >= MAX_PLAYS) return App.toast('Dinleme hakkın bitti, soruları cevapla', 'info');
            plays++; playing = true;
            $(this).html('<i class="bi bi-stop-fill"></i>');
            $('#playInfo').text(`Dinleniyor… (${plays}/${MAX_PLAYS})`);
            for (let i = 0; i < quiz.lines.length && playing; i++) {
                $('#playProgress').css('width', (100 * i / quiz.lines.length) + '%');
                const l = quiz.lines[i];
                const src = App.audio.pick(l);
                if (!(src && await App.audio.playUrl('/bff/' + src, App.audio.rate)))
                    await tts.speak(l.textEn, { voice: speakers.indexOf(l.speakerRole) % 2 ? second : main });
                await new Promise(r => setTimeout(r, 350));
            }
            playing = false;
            $('#playProgress').css('width', '100%');
            $(this).html(plays >= MAX_PLAYS ? '<i class="bi bi-check2"></i>' : '<i class="bi bi-arrow-repeat"></i>');
            $('#playInfo').text(plays >= MAX_PLAYS ? 'Dinleme hakkın bitti. Soruları cevapla.' : `Tekrar dinleyebilirsin (${MAX_PLAYS - plays} hak kaldı).`);
        });

        $('#questions').html(quiz.questions.map(q => `
            <div class="card mb-3" data-q="${q.index}">
                <div class="card-body">
                    <div class="fw-semibold mb-2">${q.index + 1}. ${esc(q.questionTr)}</div>
                    ${q.options.map((o, i) => `<button type="button" class="quiz-option" data-o="${i}">${String.fromCharCode(65 + i)}) ${esc(o)}</button>`).join('')}
                </div>
            </div>`).join(''));

        $('#questions').on('click', '.quiz-option', function () {
            if ($('#result').children().length) return; // sonuçtan sonra değiştirilemez
            const $card = $(this).closest('[data-q]');
            answers[$card.data('q')] = $(this).data('o');
            $card.find('.quiz-option').removeClass('selected');
            $(this).addClass('selected');
            $('#submitBtn').prop('disabled', answers.some(a => a < 0));
        });

        $('#submitBtn').on('click', async function () {
            App.audio.stop(); playing = false;
            App.busy(this, true, 'Kontrol ediliyor');
            try {
                const r = await api.post('listening/submit', { scenarioId: id, answers, replayCount: plays, accent: App.audio.accent });
                showResult(r);
                $('#submitWrap').addClass('d-none');
            } catch (e) { App.showError(e); App.busy(this, false); }
        });

        function showResult(r) {
            r.items.forEach((it, qi) => {
                const $card = $(`[data-q="${qi}"]`);
                $card.find('.quiz-option').each(function () {
                    const o = $(this).data('o');
                    if (o === it.correctIndex) $(this).addClass('correct');
                    else if (o === it.yourIndex) $(this).addClass('wrong');
                });
                $card.find('.card-body').append(`<div class="small mt-1 ${it.isCorrect ? 'text-success' : 'text-danger'}">
                    <i class="bi ${it.isCorrect ? 'bi-check-circle' : 'bi-x-circle'} me-1"></i>${esc(it.explanationTr)}</div>`);
            });

            const msg = r.scorePercent >= 80 ? 'Harika, karşındakini rahatça anlıyorsun! 🎉' : r.scorePercent >= 60 ? 'İyi gidiyor. Kaçırdığın kısımları metinden kontrol et.' : 'Bu senaryoyu birkaç kez daha dinle, önce metinle sonra metinsiz.';
            $('#result').html(`
                <div class="card mb-3">
                    <div class="card-body d-flex flex-column flex-sm-row gap-3 align-items-sm-center">
                        <div class="score-circle" style="--p:${r.scorePercent}"><span>${r.correct}/${r.total}</span></div>
                        <div>
                            <div class="fw-bold mb-1">${msg}</div>
                            <div class="small text-body-secondary">${plays} kez dinledin · ${App.audio.accent === 'uk' ? 'İngiliz' : 'Amerikan'} aksanı
                                ${r.summary.averagePercent30d != null ? ` · Son 30 gün ortalaman: <b>%${r.summary.averagePercent30d}</b>` : ''}</div>
                        </div>
                    </div>
                </div>
                <div class="card mb-3">
                    <div class="card-header d-flex justify-content-between align-items-center"><span><i class="bi bi-card-text me-2"></i>Diyaloğun metni</span>
                        <a href="/Study/Scenario/${id}" class="small">Senaryoya git</a></div>
                    <div class="card-body dialogue">
                        ${quiz.lines.map(l => `
                            <div class="bubble ${speakers.indexOf(l.speakerRole) % 2 === 0 ? 'left' : 'right'}">
                                <div class="speaker" lang="en">${esc(l.speakerRole || '')}</div>
                                <div lang="en">${esc(l.textEn)}</div><div class="tr">${esc(l.textTr)}</div>
                            </div>`).join('')}
                    </div>
                </div>
                <div class="d-flex gap-2 flex-wrap">
                    <a href="/Practice/Listen/${id}" class="btn btn-outline-primary"><i class="bi bi-arrow-repeat me-1"></i>Tekrar çöz</a>
                    <a href="/Practice/Listen" class="btn btn-primary">Başka senaryo</a>
                </div>`);
            $('#result')[0].scrollIntoView({ behavior: 'smooth' });
        }
    }
})();
