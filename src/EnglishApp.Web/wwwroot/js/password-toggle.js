/* Tüm şifre alanlarına göz (göster/gizle) butonu ekler. Sonradan oluşan alanlar da yakalanır. */
(function () {
    'use strict';

    function enhance(input) {
        if (input.dataset.pwToggle) return;
        input.dataset.pwToggle = '1';

        let group = input.parentElement;
        if (!group.classList.contains('input-group')) {
            group = document.createElement('div');
            group.className = 'input-group';
            input.parentNode.insertBefore(group, input);
            group.appendChild(input);
        }

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.tabIndex = -1;
        btn.className = 'btn btn-outline-secondary pw-toggle';
        btn.title = 'Şifreyi göster';
        btn.setAttribute('aria-label', 'Şifreyi göster');
        btn.innerHTML = '<i class="bi bi-eye"></i>';
        btn.addEventListener('click', function () {
            const show = input.type === 'password';
            input.type = show ? 'text' : 'password';
            btn.innerHTML = show ? '<i class="bi bi-eye-slash"></i>' : '<i class="bi bi-eye"></i>';
            btn.title = show ? 'Şifreyi gizle' : 'Şifreyi göster';
            input.focus();
        });
        input.after(btn);
    }

    function scan(root) {
        (root.querySelectorAll ? root : document).querySelectorAll('input[type="password"]').forEach(enhance);
    }

    document.addEventListener('DOMContentLoaded', function () {
        scan(document);
        new MutationObserver(muts => muts.forEach(m => m.addedNodes.forEach(n => {
            if (n.nodeType !== 1) return;
            if (n.matches && n.matches('input[type="password"]')) enhance(n); else scan(n);
        }))).observe(document.body, { childList: true, subtree: true });
    });
})();
