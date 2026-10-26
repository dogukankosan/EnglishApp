(async function () {
    const { api, fmt, esc } = App;

    let d;
    try {
        d = await api.get('stats/dashboard');
    } catch (e) {
        App.showError(e);
        return;
    }

    // Seviye testi yapılmadıysa hatırlat
    if (!d.placementTestDone) $('#placementBanner').removeClass('d-none');

    // Ayda bir dinleme ölçümü (seviye testi bittiyse ve son 30 günde test yoksa)
    const l = d.listening || {};
    $('#listenScore').text(l.averagePercent30d != null ? '%' + l.averagePercent30d : '-');
    if (d.placementTestDone && l.testDue) {
        if (l.lastAt) $('#listeningBannerText').text(`Son dinleme testin ${fmt.date(l.lastAt)} tarihinde. Yeni bir test çöz, gelişimini gör.`);
        $('#listeningBanner').removeClass('d-none');
    }

    // Hedef
    const g = d.goal || {};
    if (!g.hasGoal) {
        $('#goalBox').html(`<div class="text-body-secondary small mb-2">Bir hedef koy: hangi seviyeye, ne zamana kadar? Uygulama günlük tempoyu hesaplasın.</div>
            <a href="/Profile" class="btn btn-sm btn-primary"><i class="bi bi-flag me-1"></i>Hedef koy</a>`);
    } else {
        const cls = g.aheadDays >= 0 ? 'text-success' : 'text-danger';
        $('#goalBox').html(`
            <div class="d-flex justify-content-between align-items-baseline mb-1">
                <div><span class="fs-4 fw-bold">${esc(g.targetLevel)}</span> <span class="small text-body-secondary">· ${fmt.date(g.targetDate)} (${g.daysLeft} gün)</span></div>
                <b>%${g.progressPercent}</b>
            </div>
            <div class="progress mb-2"><div class="progress-bar" style="width:${g.progressPercent}%"></div></div>
            <div class="small mb-1">${fmt.number(g.currentVocabulary)} / ${fmt.number(g.targetVocabulary)} kelime · günde <b>${g.wordsPerDayNeeded.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}</b> yeni kelime gerekiyor (şu anki tempon ${g.currentPacePerDay.toLocaleString('tr-TR', { maximumFractionDigits: 1 })})</div>
            <div class="small fw-semibold ${cls}">${esc(g.statusTr)}</div>`);
    }

    // Haftalık koç
    const renderCoach = r => {
        if (!r) return $('#coachBox').html('<div class="text-body-secondary small">Son 7 gününe bakıp sana özel 3 odak noktası ve haftalık plan çıkarır. Haftalık rapor mailinde de gelir.</div>');
        $('#coachBox').html(`
            <div class="small text-body-secondary mb-1">${fmt.date(r.weekStart)} haftası · ${fmt.dateTime(r.createdAt)}</div>
            <p class="mb-2">${esc(r.summaryTr)}</p>
            ${r.winsTr.length ? `<div class="small mb-2">${r.winsTr.map(w => `<div><i class="bi bi-check2 text-success me-1"></i>${esc(w)}</div>`).join('')}</div>` : ''}
            ${r.focus.map((f, i) => `<div class="mb-2"><div class="fw-semibold">${i + 1}. ${esc(f.titleTr)}</div>
                <div class="small text-body-secondary">${esc(f.whyTr)}</div><div class="small"><i class="bi bi-arrow-right-short"></i>${esc(f.practiceTr)}</div></div>`).join('')}
            ${r.phrases.length ? `<details class="small mb-2"><summary class="fw-semibold">Bu hafta öğren (${r.phrases.length} kalıp)</summary>
                ${r.phrases.map(p => `<div class="mt-1"><b lang="en">${esc(p.en)}</b> — ${esc(p.tr)}</div>`).join('')}</details>` : ''}
            ${r.nextWeekPlanTr.length ? `<details class="small"><summary class="fw-semibold">Günlük plan</summary><ul class="mb-0 mt-1">${r.nextWeekPlanTr.map(p => `<li>${esc(p)}</li>`).join('')}</ul></details>` : ''}`);
    };
    api.get('coach/weekly').then(renderCoach).catch(() => renderCoach(null));
    $('#coachBtn').on('click', async function () {
        App.busy(this, true, 'Hazırlanıyor (15-30 sn)');
        try { renderCoach(await api.post('coach/weekly')); App.toast('Koç raporun hazır'); } catch (e) { App.showError(e); }
        App.busy(this, false);
    });

    // Hero
    if (d.dueNow > 0) {
        $('#heroTitle').text(`${d.dueNow} tekrar seni bekliyor`);
        $('#heroText').text('Önce tekrarlar, sonra günlük yeni kelimelerin. Unutmadan pekiştir!');
    } else if (d.studiedToday) {
        $('#heroTitle').text('Bugünkü tekrarlar tamam 🎉');
        $('#heroText').text('İstersen yeni kelime öğrenmeye devam edebilir veya bir senaryoyu dinleyebilirsin.');
        $('#heroBtn').html('<i class="bi bi-plus-lg me-1"></i>Yeni kelime çalış');
    } else {
        $('#heroTitle').text('Bekleyen tekrar yok');
        $('#heroText').text('Yeni kelimelerle başla. Her gün biraz, en etkili yöntem.');
    }
    $('#greetingText').text(d.streak > 0
        ? `🔥 ${d.streak} günlük serin var${d.studiedToday ? ', bugün de çalıştın!' : '. Bugün çalışırsan devam eder.'}`
        : 'Bugün başla, ilk serini oluştur.');

    // İstatistik kartları
    $('#statLevel').text(d.estimatedLevel);
    $('#statStreak').text(d.streak + ' gün');
    $('#statBest').text(d.bestStreak > 0 ? `(en iyi: ${d.bestStreak})` : '');
    $('#statToday').text(fmt.number(d.today.reviewCount));
    $('#statAccuracy').text(d.week.reviewCount ? '%' + d.week.accuracyPercent : '-');
    $('#weekSummary').text(`Bu hafta: ${d.week.reviewCount} kart · ${d.week.newCount} yeni · ${d.week.studyMinutes} dk`);

    $('#cntLearned').text(d.learnedWords);
    $('#cntLearning').text(d.learningWords);
    $('#cntStruggling').text(d.strugglingWords);
    $('#cntKnown').text(d.knownWords);
    $('#levelNote').text(d.estimatedLevelNote);

    // 30 günlük grafik
    const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
    const gridColor = dark ? 'rgba(255,255,255,.06)' : 'rgba(0,0,0,.05)';
    const textColor = dark ? '#9aa3b2' : '#6b7280';
    new Chart(document.getElementById('chart30'), {
        type: 'bar',
        data: {
            labels: d.last30Days.map(x => fmt.dayMonth(x.date)),
            datasets: [
                { label: 'Doğru', data: d.last30Days.map(x => x.correctCount), backgroundColor: '#6366f1', borderRadius: 4, stack: 's' },
                { label: 'Yanlış/Zor', data: d.last30Days.map(x => Math.max(0, x.reviewCount - x.correctCount)), backgroundColor: dark ? '#3730a3' : '#c7d2fe', borderRadius: 4, stack: 's' }
            ]
        },
        options: {
            maintainAspectRatio: false,
            plugins: { legend: { labels: { color: textColor, boxWidth: 12 } }, tooltip: { mode: 'index', intersect: false } },
            scales: {
                x: { stacked: true, grid: { display: false }, ticks: { color: textColor, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 } },
                y: { stacked: true, grid: { color: gridColor }, ticks: { color: textColor, precision: 0 }, beginAtZero: true }
            }
        }
    });

    // Isı haritası
    const max = Math.max(1, ...d.heatmap.map(x => x.count));
    $('#heatmap').html(d.heatmap.map(x => {
        const r = x.count / max;
        const l = x.count === 0 ? '' : r < .25 ? 'l1' : r < .5 ? 'l2' : r < .75 ? 'l3' : 'l4';
        return `<span class="day ${l}" title="${fmt.date(x.date)}: ${x.count} kart"></span>`;
    }).join(''));
    const hm = document.getElementById('heatmap'); hm.scrollLeft = hm.scrollWidth;

    // Senaryo ilerlemesi
    const started = d.scenarios.filter(s => s.isStarted || s.studiedItems > 0);
    const list = (started.length ? started : d.scenarios).slice(0, 6);
    $('#scenarioProgress').html(list.length ? list.map(s => `
        <a href="/Study/Scenario/${s.id}" class="d-flex align-items-center gap-3 py-2 text-decoration-none text-body">
            <span class="scenario-icon" style="width:40px;height:40px;font-size:1.1rem"><i class="bi ${esc(s.iconName || 'bi-chat')}"></i></span>
            <div class="flex-grow-1">
                <div class="d-flex justify-content-between small mb-1"><span class="fw-semibold">${esc(s.titleTr)}</span><span class="text-body-secondary">%${s.progressPercent}</span></div>
                <div class="progress"><div class="progress-bar" style="width:${s.progressPercent}%"></div></div>
            </div>
        </a>`).join('') : '<div class="empty-state py-3">Henüz senaryo yok.</div>');
})();
