(async function () {
    const { api, esc, fmt, tts, store } = App;
    const csrf = document.querySelector('meta[name="csrf-token"]').content;
    let profile;

    function initials(name) {
        return (name || '').split(' ').filter(Boolean).slice(0, 2).map(p => p[0].toLocaleUpperCase('tr-TR')).join('');
    }

    function renderAvatar() {
        const html = profile.profileImage
            ? `<img src="/bff/files/profile/${encodeURIComponent(profile.profileImage)}" class="avatar avatar-xl" alt="" />`
            : `<span class="avatar avatar-xl avatar-initials">${esc(initials(profile.fullName))}</span>`;
        $('#avatarBox').html(html);
        $('#photoDelete').toggleClass('d-none', !profile.profileImage);
        // Üst menüdeki avatarı ve adı da bu sayfada hemen güncelle
        $('[data-user-avatar]').replaceWith(profile.profileImage
            ? `<img src="/bff/files/profile/${encodeURIComponent(profile.profileImage)}" class="avatar avatar-sm" alt="" data-user-avatar />`
            : `<span class="avatar avatar-sm avatar-initials" data-user-avatar>${esc(initials(profile.fullName))}</span>`);
        $('[data-user-name]').text(profile.fullName);
    }

    /** Oturum çerezindeki kullanıcı bilgisini yeniler: diğer sayfalarda da yeni resim/ad hemen görünsün. */
    async function refreshSession() {
        try {
            await $.ajax({
                url: '/Account/RefreshProfile', method: 'POST',
                headers: { 'RequestVerificationToken': csrf, 'X-Requested-With': 'XMLHttpRequest' }
            });
        } catch (e) {
            console.warn('Oturum bilgisi yenilenemedi (en geç 15 dk içinde kendiliğinden güncellenir).', e);
        }
    }

    try {
        profile = await api.get('profile');
    } catch (e) { App.showError(e); return; }

    const s = profile.settings;
    // Kayıtlı ses tercihini bu tarayıcıya da uygula
    store.set('ttsVoice', s.ttsVoice || '');
    store.set('ttsRate', String(s.ttsRate));
    $('#pName').text(profile.fullName);
    $('#pEmail').text(profile.email);
    $('#pRole').text(profile.role === 'Admin' ? 'Yönetici' : 'Kullanıcı');
    $('#pTokens').text(profile.dailyTokenLimit == null ? 'Sınırsız' : fmt.number(profile.dailyTokenLimit) + ' token');
    $('#pCreated').text(fmt.date(profile.createdAt));
    $('#fullName').val(profile.fullName);
    renderAvatar();

    $('#sNew').val(s.dailyNewWordLimit);
    $('#sReview').val(s.dailyReviewLimit);
    $('#sRetention').val(s.desiredRetention);
    $('#sReminder').prop('checked', s.reminderEnabled);
    $('#sReminderTime').val(s.reminderTime);
    $('#sWeekly').prop('checked', s.weeklyReportEnabled);
    $('#sWeeklyDay').val(s.weeklyReportDay);
    $('#sMonthly').prop('checked', s.monthlyReportEnabled);
    $('#sRate').val(s.ttsRate);

    // ---- Hedef ----
    $('#gLevel').val(s.targetLevel || '');
    $('#gDate').val(s.targetDate || '').attr('min', new Date(Date.now() + 86400000).toISOString().slice(0, 10));
    $('#gFreeze').prop('checked', s.streakFreeze);
    $('#goalForm').on('submit', async function (e) {
        e.preventDefault();
        const targetLevel = $('#gLevel').val() || null, targetDate = $('#gDate').val() || null;
        if (!!targetLevel !== !!targetDate) return App.toast('Hedef için seviye ve tarihi birlikte seç', 'info');
        const $b = $(this).find('button[type=submit]');
        App.busy($b, true, 'Kaydediliyor');
        try {
            await api.put('profile/goal', { targetLevel, targetDate, streakFreeze: $('#gFreeze').is(':checked') });
            App.toast(targetLevel ? 'Hedefin kaydedildi 🎯' : 'Kaydedildi');
        } catch (err) { App.showError(err); }
        App.busy($b, false);
    });

    const updateLabels = () => {
        $('#retLabel').text('%' + Math.round($('#sRetention').val() * 100));
        $('#rateLabel').text(parseFloat($('#sRate').val()).toFixed(2) + 'x');
    };
    updateLabels();
    $('#sRetention, #sRate').on('input', updateLabels);

    // Sesler (tarayıcıdan)
    await tts.init();
    $('#sVoice').append(tts.voices.map(v => `<option value="${esc(v.name)}">${esc(v.name)} (${esc(v.lang)})</option>`).join(''));
    $('#sVoice').val(s.ttsVoice || '');
    $('#testVoice').on('click', () => {
        const name = $('#sVoice').val();
        tts.speak('Hello! Could I have a table for two, please?', {
            voice: name ? tts.voices.find(v => v.name === name) : tts.pickVoice(''),
            rate: parseFloat($('#sRate').val())
        });
    });

    // Ad soyad
    $('#nameForm').on('submit', async function (e) {
        e.preventDefault();
        const btn = $(this).find('button');
        App.busy(btn, true, 'Kaydediliyor');
        try {
            await api.put('profile', { fullName: $('#fullName').val().trim() });
            profile.fullName = $('#fullName').val().trim();
            $('#pName').text(profile.fullName);
            renderAvatar();
            await refreshSession();
            App.toast('Bilgilerin kaydedildi');
        } catch (err) { App.showError(err); }
        App.busy(btn, false);
    });

    // Ayarlar
    $('#settingsForm').on('submit', async function (e) {
        e.preventDefault();
        const btn = $(this).find('button[type=submit]');
        App.busy(btn, true, 'Kaydediliyor');
        const payload = {
            dailyNewWordLimit: parseInt($('#sNew').val() || '0', 10),
            dailyReviewLimit: parseInt($('#sReview').val() || '0', 10),
            desiredRetention: parseFloat($('#sRetention').val()),
            reminderEnabled: $('#sReminder').is(':checked'),
            reminderTime: $('#sReminderTime').val() || '20:00',
            weeklyReportEnabled: $('#sWeekly').is(':checked'),
            weeklyReportDay: parseInt($('#sWeeklyDay').val(), 10),
            monthlyReportEnabled: $('#sMonthly').is(':checked'),
            ttsVoice: $('#sVoice').val() || null,
            ttsRate: parseFloat($('#sRate').val())
        };
        try {
            await api.put('profile/settings', payload);
            // Ses tercihleri bu tarayıcıda hemen kullanılsın
            store.set('ttsVoice', payload.ttsVoice || '');
            store.set('ttsRate', String(payload.ttsRate));
            App.toast('Ayarların kaydedildi');
        } catch (err) { App.showError(err); }
        App.busy(btn, false);
    });

    // Profil resmi
    $('#photoInput').on('change', async function () {
        const file = this.files[0];
        this.value = '';
        if (!file) return;
        if (file.size > 2 * 1024 * 1024) return App.error('Resim en fazla 2 MB olabilir.');
        const fd = new FormData();
        fd.append('file', file);
        try {
            profile.profileImage = await api.upload('profile/photo', fd);
            renderAvatar();
            await refreshSession();
            App.toast('Profil resmin güncellendi');
        } catch (err) { App.showError(err); }
    });

    $('#photoDelete').on('click', async function () {
        if (!await App.confirm('Profil resmi silinsin mi?', '', 'Sil')) return;
        try {
            await api.del('profile/photo');
            profile.profileImage = null;
            renderAvatar();
            await refreshSession();
        } catch (err) { App.showError(err); }
    });

    // Şifre (MVC üzerinden: yeni token cookie'ye yazılsın diye)
    $('#pwForm').on('submit', function (e) {
        e.preventDefault();
        const $f = $(this), btn = $f.find('button');
        App.busy(btn, true, 'Değiştiriliyor');
        $.ajax({
            url: '/Account/ChangePassword', method: 'POST', data: $f.serialize(),
            headers: { 'RequestVerificationToken': csrf, 'X-Requested-With': 'XMLHttpRequest' }
        }).done(res => {
            if (res.success) { $f[0].reset(); App.toast('Şifren değiştirildi'); }
            else App.error(res.error?.message || 'Şifre değiştirilemedi.');
        }).fail(() => App.error('Şifre değiştirilemedi.'))
          .always(() => App.busy(btn, false));
    });
})();
