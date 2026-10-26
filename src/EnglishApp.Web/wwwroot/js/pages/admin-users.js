(function () {
    const { api, esc, fmt } = App;
    const modal = new bootstrap.Modal('#userModal');
    let users = [];

    const trLang = {
        search: '', searchPlaceholder: 'Ara…', lengthMenu: '_MENU_ kayıt', info: '_TOTAL_ kullanıcıdan _START_-_END_',
        infoEmpty: 'Kayıt yok', infoFiltered: '(_MAX_ kayıt içinden)', zeroRecords: 'Eşleşen kayıt yok', emptyTable: 'Henüz kullanıcı yok',
        paginate: { first: '«', last: '»', next: '›', previous: '‹' }
    };

    const initials = n => (n || '').split(' ').filter(Boolean).slice(0, 2).map(p => p[0].toLocaleUpperCase('tr-TR')).join('');

    const table = new DataTable('#usersTable', {
        data: [],
        language: trLang,
        order: [[0, 'asc']],
        pageLength: 25,
        columns: [
            {
                data: 'fullName', render: (d, t, u) => t !== 'display' ? d + ' ' + u.email : `
                    <div class="d-flex align-items-center gap-2">
                        ${u.profileImage ? `<img src="/bff/files/profile/${encodeURIComponent(u.profileImage)}" class="avatar avatar-md" alt="">`
                                         : `<span class="avatar avatar-md avatar-initials">${esc(initials(u.fullName))}</span>`}
                        <div><div class="fw-semibold">${esc(u.fullName)}</div><div class="small text-body-secondary">${esc(u.email)}</div></div>
                    </div>`
            },
            { data: 'role', render: d => d === 'Admin' ? '<span class="badge bg-primary">Yönetici</span>' : '<span class="badge bg-secondary-subtle text-secondary">Kullanıcı</span>' },
            {
                data: 'isActive', render: (d, t, u) => {
                    const badges = [d ? '<span class="badge bg-success-subtle text-success">Aktif</span>' : '<span class="badge bg-danger-subtle text-danger">Pasif</span>'];
                    if (u.isLocked) badges.push('<span class="badge bg-warning-subtle text-warning">Kilitli</span>');
                    if (u.mustChangePassword) badges.push('<span class="badge bg-info-subtle text-info" title="İlk girişte şifre değiştirecek">Şifre bekliyor</span>');
                    return badges.join(' ');
                }
            },
            { data: 'dailyTokenLimit', render: (d, t, u) => t !== 'display' ? (d ?? Infinity) : `${fmt.number(u.todayTokens)} / ${d == null ? '∞' : fmt.number(d)}` },
            { data: 'lastLoginAt', render: (d, t) => t !== 'display' ? (d || '') : (d ? fmt.dateTime(d) : '<span class="text-body-secondary">Hiç</span>') },
            {
                data: null, orderable: false, className: 'text-end text-nowrap', render: (d, t, u) => `
                    <div class="dropdown">
                        <button class="btn btn-sm btn-light" data-bs-toggle="dropdown"><i class="bi bi-three-dots"></i></button>
                        <ul class="dropdown-menu dropdown-menu-end shadow-sm">
                            <li><button class="dropdown-item" data-act="edit" data-id="${u.id}"><i class="bi bi-pencil me-2"></i>Düzenle</button></li>
                            <li><button class="dropdown-item" data-act="reset" data-id="${u.id}"><i class="bi bi-key me-2"></i>Şifre sıfırla</button></li>
                            ${u.isLocked ? `<li><button class="dropdown-item" data-act="unlock" data-id="${u.id}"><i class="bi bi-unlock me-2"></i>Kilidi aç</button></li>` : ''}
                            <li><button class="dropdown-item" data-act="${u.isActive ? 'deactivate' : 'activate'}" data-id="${u.id}">
                                <i class="bi ${u.isActive ? 'bi-pause-circle' : 'bi-play-circle'} me-2"></i>${u.isActive ? 'Pasife al' : 'Aktif et'}</button></li>
                            <li><hr class="dropdown-divider"></li>
                            <li><button class="dropdown-item text-danger" data-act="delete" data-id="${u.id}"><i class="bi bi-trash me-2"></i>Sil</button></li>
                        </ul>
                    </div>`
            }
        ]
    });

    async function load() {
        try {
            users = await api.get('admin/users');
            table.clear().rows.add(users).draw();
        } catch (e) { App.showError(e); }
    }

    function genPassword() {
        const chars = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ';
        const digits = '23456789';
        const rnd = n => crypto.getRandomValues(new Uint32Array(1))[0] % n;
        let p = '';
        for (let i = 0; i < 7; i++) p += chars[rnd(chars.length)];
        for (let i = 0; i < 3; i++) p += digits[rnd(digits.length)];
        return p;
    }

    function openModal(u) {
        $('#uId').val(u?.id || '');
        $('#uName').val(u?.fullName || '');
        $('#uEmail').val(u?.email || '');
        $('#uRole').val(u?.role || 'User');
        $('#uTokens').val(u ? (u.dailyTokenLimit ?? '') : '');
        $('#uPassword').val(u ? '' : genPassword());
        $('#pwGroup').toggle(!u);
        $('#userModalTitle').text(u ? 'Kullanıcıyı düzenle' : 'Yeni kullanıcı');
        modal.show();
    }

    $('#newUserBtn').on('click', () => openModal(null));
    $('#genPw').on('click', () => $('#uPassword').val(genPassword()));

    $('#userForm').on('submit', async function (e) {
        e.preventDefault();
        const id = $('#uId').val();
        const tokens = $('#uTokens').val();
        const body = {
            fullName: $('#uName').val().trim(),
            email: $('#uEmail').val().trim(),
            role: $('#uRole').val(),
            dailyTokenLimit: tokens === '' ? null : parseInt(tokens, 10)
        };
        App.busy('#userSaveBtn', true, 'Kaydediliyor');
        try {
            if (id) {
                await api.put('admin/users/' + id, body);
                App.toast('Kullanıcı güncellendi');
            } else {
                body.password = $('#uPassword').val();
                await api.post('admin/users', body);
                modal.hide();
                await Swal.fire({
                    icon: 'success', title: 'Kullanıcı oluşturuldu',
                    html: `Giriş bilgilerini kullanıcıya ilet:<br><br><b>${esc(body.email)}</b><br>Geçici şifre: <code style="font-size:1.1rem">${esc(body.password)}</code>`,
                    confirmButtonColor: '#4f46e5', confirmButtonText: 'Tamam'
                });
            }
            modal.hide();
            load();
        } catch (err) { App.showError(err); }
        App.busy('#userSaveBtn', false);
    });

    $('#usersTable').on('click', '[data-act]', async function () {
        const id = $(this).data('id');
        const u = users.find(x => x.id === id);
        const act = $(this).data('act');
        try {
            switch (act) {
                case 'edit':
                    return openModal(u);
                case 'reset': {
                    const pw = genPassword();
                    if (!await App.confirm('Şifre sıfırlansın mı?', `${u.fullName} için yeni geçici şifre oluşturulacak ve tüm oturumları kapanacak.`, 'Sıfırla', 'question', false)) return;
                    await api.post(`admin/users/${id}/reset-password`, { newPassword: pw });
                    await Swal.fire({ icon: 'success', title: 'Şifre sıfırlandı', html: `Yeni geçici şifre: <code style="font-size:1.1rem">${esc(pw)}</code>`, confirmButtonColor: '#4f46e5' });
                    break;
                }
                case 'unlock':
                    await api.post(`admin/users/${id}/unlock`);
                    App.toast('Kilit açıldı');
                    break;
                case 'deactivate':
                    if (!await App.confirm('Pasife alınsın mı?', `${u.fullName} giriş yapamayacak ve açık oturumları kapanacak.`, 'Pasife al')) return;
                    await api.post(`admin/users/${id}/deactivate`);
                    App.toast('Kullanıcı pasife alındı');
                    break;
                case 'activate':
                    await api.post(`admin/users/${id}/activate`);
                    App.toast('Kullanıcı aktif edildi');
                    break;
                case 'delete':
                    if (!await App.confirm('Kullanıcı silinsin mi?', `${u.fullName} silinecek. Bu işlem kayıt altına alınır.`, 'Sil')) return;
                    await api.del(`admin/users/${id}`);
                    App.toast('Kullanıcı silindi');
                    break;
            }
            load();
        } catch (err) { App.showError(err); }
    });

    load();
})();
