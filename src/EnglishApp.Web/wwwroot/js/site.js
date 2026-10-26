/* EnglishApp ortak JS: API istemcisi, bildirimler, seslendirme, biçimlendirme */
(function (window, $) {
    'use strict';

    const csrf = document.querySelector('meta[name="csrf-token"]')?.content;

    // ---------- Bildirimler (SweetAlert2) ----------
    const Toast = window.Swal ? Swal.mixin({
        toast: true, position: 'top-end', showConfirmButton: false, timer: 2600, timerProgressBar: true
    }) : null;

    function toast(message, icon = 'success') {
        // titleText: başlık düz metin olarak basılır (title HTML kabul eder, kullanıcı/AI verisiyle XSS olurdu)
        if (Toast) Toast.fire({ icon, titleText: String(message ?? '') });
    }

    function error(message, title = 'Bir sorun oluştu') {
        return Swal.fire({ icon: 'error', titleText: title, text: message, confirmButtonColor: '#4f46e5', confirmButtonText: 'Tamam' });
    }

    async function confirm(title, text, confirmText = 'Evet', icon = 'warning', danger = true) {
        const r = await Swal.fire({
            icon, titleText: title, text, showCancelButton: true,
            confirmButtonText: confirmText, cancelButtonText: 'Vazgeç',
            confirmButtonColor: danger ? '#ef4444' : '#4f46e5', reverseButtons: true, focusCancel: danger
        });
        return r.isConfirmed;
    }

    // ---------- API istemcisi (BFF proxy üzerinden) ----------
    class ApiError extends Error {
        constructor(code, message, status, details, correlationId) {
            super(message); this.code = code; this.status = status; this.details = details; this.correlationId = correlationId;
        }
    }

    function request(method, url, data, isForm) {
        const opts = {
            url: '/bff/' + url.replace(/^\/+/, ''),
            method,
            headers: { 'RequestVerificationToken': csrf, 'X-Requested-With': 'XMLHttpRequest' },
            dataType: 'json'
        };
        if (isForm) {
            opts.data = data; opts.processData = false; opts.contentType = false;
        } else if (data !== undefined && method !== 'GET') {
            opts.data = JSON.stringify(data); opts.contentType = 'application/json; charset=utf-8';
        } else if (data !== undefined) {
            opts.data = data;
        }

        return new Promise((resolve, reject) => {
            $.ajax(opts)
                .done(res => {
                    if (res && res.success === false) reject(new ApiError(res.error?.code, res.error?.message, 200, res.error?.details, res.correlationId));
                    else resolve(res ? res.data : null);
                })
                .fail(xhr => {
                    const res = xhr.responseJSON;
                    if (xhr.status === 401 || res?.error?.code === 'SESSION_EXPIRED') {
                        window.location.href = '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname + location.search);
                        return;
                    }
                    if (res?.error?.code === 'PASSWORD_CHANGE_REQUIRED') {
                        window.location.href = '/Account/ChangePassword';
                        return;
                    }
                    const msg = res?.error?.message
                        || (xhr.status === 0 ? 'Sunucuya ulaşılamadı. İnternet bağlantını kontrol et.' : 'Beklenmeyen bir hata oluştu.');
                    reject(new ApiError(res?.error?.code || 'HTTP_' + xhr.status, msg, xhr.status, res?.error?.details, res?.correlationId));
                });
        });
    }

    const api = {
        get: (url, params) => request('GET', url, params),
        post: (url, data) => request('POST', url, data ?? {}),
        put: (url, data) => request('PUT', url, data ?? {}),
        del: (url) => request('DELETE', url),
        upload: (url, formData) => request('POST', url, formData, true)
    };

    /** API hatasını kullanıcıya gösterir. Correlation ID varsa (destek için) ekler. */
    function showError(err) {
        console.error(err);
        const extra = err?.correlationId ? `\n\nHata kodu: ${err.correlationId}` : '';
        return error((err?.message || 'Beklenmeyen bir hata oluştu.') + extra);
    }

    // ---------- Seslendirme (tarayıcının Web Speech API'si) ----------
    const tts = {
        voices: [],
        ready: null,
        get supported() { return 'speechSynthesis' in window; },

        init() {
            if (!this.supported) return Promise.resolve([]);
            if (this.ready) return this.ready;
            this.ready = new Promise(resolve => {
                const load = () => {
                    this.voices = speechSynthesis.getVoices().filter(v => /^en(-|_|$)/i.test(v.lang));
                    if (this.voices.length) resolve(this.voices);
                };
                load();
                speechSynthesis.onvoiceschanged = load;
                setTimeout(() => resolve(this.voices), 1500);
            });
            return this.ready;
        },

        /** Tercih sırası: kullanıcının seçtiği > doğal (Natural/Online) en-US > Google US > ilk en-US > ilk İngilizce */
        pickVoice(preferredName) {
            const v = this.voices;
            const saved = preferredName ?? store.get('ttsVoice');
            const uk = audio.accent === 'uk' && !saved;
            return v.find(x => x.name === saved)
                || (uk && (v.find(x => /en-GB/i.test(x.lang) && /natural|online/i.test(x.name))
                       || v.find(x => /Google UK English/i.test(x.name))
                       || v.find(x => /en-GB/i.test(x.lang))))
                || v.find(x => /en-US/i.test(x.lang) && /natural|online/i.test(x.name))
                || v.find(x => /Google US English/i.test(x.name))
                || v.find(x => /en-US/i.test(x.lang))
                || v[0] || null;
        },

        async speak(text, opts = {}) {
            if (!this.supported || !text) return;
            await this.init();
            if (audio.el) { audio.el.pause(); audio.el = null; } // çalan MP3 ile üst üste binmesin
            speechSynthesis.cancel();
            return new Promise(resolve => {
                const u = new SpeechSynthesisUtterance(text);
                const voice = opts.voice || this.pickVoice();
                if (voice) { u.voice = voice; u.lang = voice.lang; } else { u.lang = 'en-US'; }
                u.rate = opts.rate ?? audio.rate;
                u.onend = u.onerror = () => resolve();
                speechSynthesis.speak(u);
            });
        },

        stop() {
            if (this.supported) speechSynthesis.cancel();
            if (audio.el) { audio.el.pause(); audio.el = null; }
        }
    };

    // ---------- Gerçekçi ses: hazır MP3 (Kokoro) > Gemini doğal sesi > tarayıcı sesi ----------
    const audio = {
        el: null,
        geminiDownUntil: 0,
        warned: false,

        stop() {
            if (this.el) { try { this.el.pause(); } catch { } this.el = null; }
            if ('speechSynthesis' in window) speechSynthesis.cancel();
        },

        /** Bir ses dosyasını çalar; bitince (ya da hata olursa) çözülür. Hata olursa false döner. */
        playUrl(src, rate = 1) {
            this.stop();
            return new Promise(resolve => {
                const el = new Audio(src);
                el.playbackRate = rate;     // yavaş dinleme: ses tonu bozulmadan yavaşlar
                el.preservesPitch = true;
                this.el = el;
                el.onended = () => resolve(true);
                el.onerror = () => resolve(false);
                el.play().catch(() => resolve(false));
            });
        },

        // ---- Dinleme tercihleri (bu tarayıcıda saklanır) ----
        /** 'us' | 'uk' */
        get accent() { return store.get('accent') === 'uk' ? 'uk' : 'us'; },
        set accent(v) { store.set('accent', v === 'uk' ? 'uk' : 'us'); },
        /** Dinleme hızı: 0.75 / 1 / 1.25 */
        get rate() { const r = parseFloat(store.get('audioRate') || store.get('ttsRate') || '1'); return r >= 0.5 && r <= 2 ? r : 1; },
        set rate(v) { store.set('audioRate', String(v)); },

        /** Seçili aksana göre hazır ses adresi (İngiliz sesi yoksa Amerikan). */
        pick(item) {
            if (!item) return null;
            return (this.accent === 'uk' && item.audioUrlUk) || item.audioUrl || null;
        },

        /**
         * Hazır MP3 varsa onu, yoksa tarayıcı sesini kullanır (kelime, örnek cümle, diyalog).
         * item: { audioUrl, audioUrlUk } taşıyan nesne. rate verilmezse kullanıcının hız tercihi.
         */
        async speak({ url, urlUk, item, text, rate }) {
            const src = item ? this.pick(item) : ((this.accent === 'uk' && urlUk) || url);
            const r = rate ?? this.rate;
            if (src && await this.playUrl('/bff/' + src, r)) return;
            // Hazır ses yok (yeni/kişisel içerik): doğal ses (Gemini), o da olmazsa tarayıcı sesi
            await this.speakAi(text, undefined, r);
        },

        /** Hız + aksan seçicisini verilen alana çizer. */
        prefsControl(container) {
            const $c = $(container);
            $c.html(`
                <select class="form-select form-select-sm w-auto py-0 audio-rate" title="Dinleme hızı">
                    <option value="0.75">🐢 0,75x</option><option value="1">1x</option><option value="1.25">🐇 1,25x</option>
                </select>
                <select class="form-select form-select-sm w-auto py-0 audio-accent" title="Aksan">
                    <option value="us">Amerikan aksanı</option><option value="uk">İngiliz aksanı</option>
                </select>`).addClass('d-flex gap-2');
            $c.find('.audio-rate').val(String(this.rate === 1 ? 1 : this.rate)).on('change', e => { this.rate = parseFloat(e.target.value); toast('Hız: ' + e.target.value.replace('.', ',') + 'x', 'info'); });
            $c.find('.audio-accent').val(this.accent).on('change', e => { this.accent = e.target.value; toast(e.target.value === 'uk' ? 'İngiliz aksanı seçildi' : 'Amerikan aksanı seçildi', 'info'); });
        },

        get naturalEnabled() { return store.get('naturalVoice') !== '0'; },

        /**
         * AI cevabı: Gemini doğal sesi. Kota dolarsa / hata olursa 5 dk boyunca tarayıcı sesi kullanılır.
         * Aynı metin sunucuda önbelleğe alındığı için tekrar dinlemek kota harcamaz.
         */
        async speakAi(text, voice, rate) {
            rate = rate ?? this.rate;
            if (!text) return;
            if (!this.naturalEnabled || Date.now() < this.geminiDownUntil) return tts.speak(text, { rate });
            this.stop();
            try {
                const res = await fetch('/bff/ai/tts', {
                    method: 'POST', credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': csrf, 'X-Requested-With': 'XMLHttpRequest' },
                    body: JSON.stringify({ text, voice })
                });
                if (!res.ok || !(res.headers.get('Content-Type') || '').startsWith('audio')) throw new Error('tts ' + res.status);
                const url = URL.createObjectURL(await res.blob());
                const ok = await this.playUrl(url, rate);
                setTimeout(() => URL.revokeObjectURL(url), 1000);
                if (!ok) throw new Error('play');
            } catch (e) {
                console.warn('Doğal ses kullanılamadı, tarayıcı sesine geçiliyor.', e);
                this.geminiDownUntil = Date.now() + 5 * 60000;
                if (!this.warned) { this.warned = true; toast('Doğal ses şu an kullanılamıyor, tarayıcı sesi kullanılıyor', 'info'); }
                await tts.speak(text, { rate });
            }
        }
    };

    // ---------- Konuşmayı yazıya çevirme ----------
    // 1) Tarayıcı tanıması (Chrome/Edge): canlı yazı gösterir ama sesi Google'a gönderir; Brave/Firefox'ta çalışmaz.
    // 2) Sunucu tanıması (Groq Whisper): her tarayıcıda çalışır, aksanlı İngilizceyi daha iyi anlar.
    // "Otomatik" modda tarayıcı denenir; "network" vb. hata verirse bu tarayıcı için kalıcı olarak Whisper'a geçilir.
    const stt = {
        get browserSupported() { return !!(window.SpeechRecognition || window.webkitSpeechRecognition); },
        get recorderSupported() { return !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder); },
        get supported() { return this.browserSupported || this.recorderSupported; },

        /** 'auto' | 'browser' | 'whisper' (kullanıcı tercihi, bu tarayıcıda saklanır) */
        get preference() { return store.get('sttMethod') || 'auto'; },
        set preference(v) { store.set('sttMethod', v); if (v !== 'auto') store.set('sttBrowserBroken', ''); },

        /** Şu an kullanılacak yöntem */
        get method() {
            const p = this.preference;
            if (p === 'whisper' || !this.browserSupported) return 'whisper';
            if (p === 'browser') return 'browser';
            return store.get('sttBrowserBroken') === '1' ? 'whisper' : 'browser';
        },

        listening: false,
        rec: null,
        session: null,

        /**
         * Mikrofonu dinler; konuşma bitince metinle çözülür.
         * onInterim: canlı ara metin (sadece tarayıcı tanımasında), onState: 'listening' | 'transcribing' | 'idle'
         */
        async start(opts = {}) {
            if (!this.supported) throw new Error('Tarayıcın mikrofon kaydını desteklemiyor.');
            this.cancel();
            audio.stop(); // AI konuşurken kendi sesini duymasın

            if (this.method === 'browser') {
                try {
                    return await this._browser(opts);
                } catch (e) {
                    // Brave / ağ engeli / servis kapalı: sunucu tanımasına geç ve bunu hatırla
                    if (['network', 'service-not-allowed', 'language-not-supported'].includes(e.code) && this.recorderSupported) {
                        store.set('sttBrowserBroken', '1');
                        toast('Tarayıcının ses tanıması bu tarayıcıda çalışmıyor; sunucu ile tanımaya geçildi', 'info');
                        return await this._whisper(opts);
                    }
                    throw e;
                }
            }
            return await this._whisper(opts);
        },

        /** Konuşmayı bitir ve sonucu al (mikrofon butonuna tekrar basınca). */
        finish() {
            if (this.rec) { try { this.rec.stop(); } catch { } }
            if (this.session) this.session.finish();
        },

        /** Kaydı iptal et, sonucu atma. */
        cancel() {
            if (this.rec) { try { this.rec.abort(); } catch { } this.rec = null; }
            if (this.session) this.session.cancel();
            this.listening = false;
        },
        stop() { this.cancel(); },

        // ---- Tarayıcı tanıması ----
        _browser({ onInterim, onFinal, onState } = {}) {
            const Rec = window.SpeechRecognition || window.webkitSpeechRecognition;
            const rec = new Rec();
            rec.lang = 'en-US';
            rec.interimResults = true;
            rec.continuous = false;
            rec.maxAlternatives = 1;
            this.rec = rec;

            return new Promise((resolve, reject) => {
                let finalText = '';
                rec.onresult = e => {
                    let interim = '';
                    for (let i = e.resultIndex; i < e.results.length; i++) {
                        const t = e.results[i][0].transcript;
                        if (e.results[i].isFinal) finalText += t; else interim += t;
                    }
                    if (interim && onInterim) onInterim((finalText + ' ' + interim).trim());
                    if (finalText && onFinal) onFinal(finalText.trim());
                };
                rec.onerror = e => {
                    this.listening = false; this.rec = null; onState?.('idle');
                    if (e.error === 'no-speech' || e.error === 'aborted') return resolve('');
                    const err = new Error(e.error === 'not-allowed'
                        ? 'Mikrofon izni verilmedi. Adres çubuğundaki kilit/kamera simgesinden mikrofona izin ver.'
                        : 'Ses tanınamadı: ' + e.error);
                    err.code = e.error;
                    reject(err);
                };
                rec.onend = () => { this.listening = false; this.rec = null; onState?.('idle'); resolve(finalText.trim()); };
                this.listening = true;
                onState?.('listening');
                rec.start();
            });
        },

        // ---- Sunucu tanıması (kayıt → Groq Whisper) ----
        async _whisper({ onState, autoStop = true } = {}) {
            let stream;
            try {
                stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
            } catch (e) {
                const err = new Error(e.name === 'NotAllowedError'
                    ? 'Mikrofon izni verilmedi. Adres çubuğundaki kilit/kamera simgesinden mikrofona izin ver.'
                    : 'Mikrofon açılamadı: ' + (e.message || e.name));
                err.code = e.name;
                throw err;
            }

            const mime = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus']
                .find(t => window.MediaRecorder.isTypeSupported?.(t)) || '';
            const recorder = new MediaRecorder(stream, mime ? { mimeType: mime } : undefined);
            const chunks = [];
            let cancelled = false;
            let ctx = null, raf = 0, timer = 0;

            const cleanup = () => {
                cancelAnimationFrame(raf); clearTimeout(timer);
                stream.getTracks().forEach(t => t.stop());
                if (ctx) ctx.close().catch(() => { });
                this.session = null; this.listening = false;
            };

            const done = new Promise((resolve, reject) => {
                recorder.ondataavailable = e => { if (e.data.size) chunks.push(e.data); };
                recorder.onstop = async () => {
                    cleanup();
                    if (cancelled) { onState?.('idle'); return resolve(''); }
                    const type = recorder.mimeType || mime || 'audio/webm';
                    const blob = new Blob(chunks, { type });
                    if (blob.size < 1500) { onState?.('idle'); return resolve(''); }  // neredeyse boş kayıt
                    onState?.('transcribing');
                    try {
                        const fd = new FormData();
                        fd.append('file', blob, 'speech.' + (type.includes('mp4') ? 'm4a' : type.includes('ogg') ? 'ogg' : 'webm'));
                        const r = await api.upload('ai/stt', fd);
                        onState?.('idle');
                        resolve((r?.text || '').trim());
                    } catch (e) {
                        onState?.('idle');
                        reject(e);
                    }
                };
            });

            this.session = {
                finish: () => { if (recorder.state !== 'inactive') recorder.stop(); },
                cancel: () => { cancelled = true; if (recorder.state !== 'inactive') recorder.stop(); else cleanup(); }
            };

            // Sessizlik algılama: konuştuktan sonra ~1,3 sn sessiz kalınca kendiliğinden durur
            if (autoStop && (window.AudioContext || window.webkitAudioContext)) {
                ctx = new (window.AudioContext || window.webkitAudioContext)();
                const analyser = ctx.createAnalyser();
                analyser.fftSize = 1024;
                ctx.createMediaStreamSource(stream).connect(analyser);
                const data = new Float32Array(analyser.fftSize);
                const startedAt = performance.now();
                let spoke = false, silentSince = 0;
                const tick = () => {
                    if (recorder.state === 'inactive') return;
                    analyser.getFloatTimeDomainData(data);
                    let sum = 0; for (let i = 0; i < data.length; i++) sum += data[i] * data[i];
                    const rms = Math.sqrt(sum / data.length);
                    const now = performance.now();
                    if (rms > 0.02) { spoke = true; silentSince = 0; }
                    else if (spoke) { silentSince ||= now; if (now - silentSince > 1300) return this.session?.finish(); }
                    if (!spoke && now - startedAt > 8000) return this.session?.finish();   // hiç konuşmadı
                    raf = requestAnimationFrame(tick);
                };
                raf = requestAnimationFrame(tick);
            }
            timer = setTimeout(() => this.session?.finish(), 45000); // en fazla 45 sn

            recorder.start(250);
            this.listening = true;
            onState?.('listening');
            return done;
        }
    };

    // ---------- Tarayıcı deposu (hata vermeyen) ----------
    const store = {
        get(k) { try { return localStorage.getItem(k); } catch { return null; } },
        set(k, v) { try { localStorage.setItem(k, v); } catch { } }
    };

    // ---------- Biçimlendirme (Türkiye) ----------
    const fmt = {
        date: d => d ? new Date(d).toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric' }) : '-',
        dateTime: d => d ? new Date(d).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '-',
        dayMonth: d => new Date(d).toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }),
        number: n => (n ?? 0).toLocaleString('tr-TR'),
        relative(d) {
            if (!d) return '-';
            const diff = (new Date(d) - new Date()) / 60000;
            const abs = Math.abs(diff);
            const label = abs < 1 ? 'şimdi' : abs < 60 ? `${Math.round(abs)} dk` : abs < 1440 ? `${Math.round(abs / 60)} sa` : `${Math.round(abs / 1440)} gün`;
            return abs < 1 ? label : diff > 0 ? `${label} sonra` : `${label} önce`;
        }
    };

    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    const levelNames = { A1: 'A1', A2: 'A2', B1: 'B1', B2: 'B2', C1: 'C1', C2: 'C2' };
    const posNames = {
        Noun: 'isim', Verb: 'fiil', Adjective: 'sıfat', Adverb: 'zarf', Pronoun: 'zamir', Preposition: 'edat',
        Conjunction: 'bağlaç', Determiner: 'belirteç', Interjection: 'ünlem', Phrase: 'kalıp', PhrasalVerb: 'phrasal verb', Other: 'diğer'
    };

    /** Buton yükleniyor durumu */
    function busy(btn, on, text) {
        const $b = $(btn);
        if (on) {
            $b.data('html', $b.html()).prop('disabled', true)
              .html(`<span class="spinner-border spinner-border-sm me-1"></span>${text || 'Lütfen bekle'}`);
        } else {
            $b.prop('disabled', false).html($b.data('html'));
        }
    }

    // ---------- Tema ----------
    function setThemeIcon() {
        const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
        $('#themeToggle i').attr('class', dark ? 'bi bi-sun' : 'bi bi-moon-stars');
    }
    $(function () {
        setThemeIcon();
        $('#themeToggle').on('click', function () {
            const next = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-bs-theme', next);
            // Çereze yazılır (1 yıl): sunucu bir sonraki açılışta temayı doğrudan basar
            document.cookie = `theme=${next}; path=/; max-age=${60 * 60 * 24 * 365}; SameSite=Lax${location.protocol === 'https:' ? '; Secure' : ''}`;
            setThemeIcon();
        });
    });

    // ---------- Telaffuz (shadowing): cümleyi tekrar et, doğru söylediğin kelimeleri gör ----------
    const shadow = {
        norm: w => w.toLowerCase().replace(/[’‘`]/g, "'").replace(/[^a-z0-9']/g, ''),

        /** Hedef ve duyulan kelimeleri en uzun ortak alt dizi (LCS) ile eşler. */
        compare(target, heard) {
            const t = target.split(/\s+/).filter(Boolean);
            const tn = t.map(this.norm);
            const hn = heard.split(/\s+/).map(this.norm).filter(Boolean);
            const dp = Array.from({ length: tn.length + 1 }, () => new Array(hn.length + 1).fill(0));
            for (let i = tn.length - 1; i >= 0; i--)
                for (let j = hn.length - 1; j >= 0; j--)
                    dp[i][j] = tn[i] && tn[i] === hn[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
            const ok = new Array(t.length).fill(false);
            for (let i = 0, j = 0; i < tn.length && j < hn.length;) {
                if (tn[i] && tn[i] === hn[j]) { ok[i] = true; i++; j++; }
                else if (dp[i + 1][j] >= dp[i][j + 1]) i++; else j++;
            }
            const scored = tn.filter(Boolean).length || 1;
            const hits = ok.filter((v, i) => v && tn[i]).length;
            return { words: t.map((w, i) => ({ w, ok: ok[i] || !tn[i] })), score: Math.round(100 * hits / scored) };
        },

        render(target, heard) {
            const r = this.compare(target, heard);
            const msg = r.score >= 90 ? 'Mükemmel! 🎉' : r.score >= 70 ? 'Çok iyi 👍' : r.score >= 50 ? 'Fena değil, kırmızıları tekrar dene' : 'Önce dinle, sonra yavaşça tekrar et';
            return `
                <div class="shadow-result">
                    <div class="d-flex justify-content-between align-items-center mb-1">
                        <span class="fw-semibold small">${msg}</span><span class="badge ${r.score >= 70 ? 'bg-success' : r.score >= 50 ? 'bg-warning' : 'bg-danger'}">%${r.score}</span>
                    </div>
                    <div lang="en">${r.words.map(x => `<span class="${x.ok ? 'sh-ok' : 'sh-miss'}">${esc(x.w)}</span>`).join(' ')}</div>
                    <div class="small text-body-secondary mt-1">Duyduğum: <i lang="en">"${esc(heard || '…')}"</i></div>
                </div>`;
        },

        /** [data-shadow-text] butonlarına tıklanınca çalışır. Sonuç en yakın [data-shadow-scope] içindeki .shadow-out'a yazılır. */
        async run(btn) {
            const $btn = $(btn);
            if (stt.listening) { stt.finish(); return; }
            const target = String($btn.data('shadow-text') || '');
            const $scope = $btn.closest('[data-shadow-scope]');
            let $out = $scope.find('.shadow-out').first();
            if (!$out.length) $out = $('<div class="shadow-out mt-2"></div>').appendTo($scope.length ? $scope : $btn.parent());
            const html = $btn.html();
            try {
                const heard = await stt.start({
                    onInterim: t => $out.html(`<div class="small text-body-secondary" lang="en">🎤 ${esc(t)}</div>`),
                    onState: st => {
                        if (st === 'listening') { $btn.addClass('btn-danger text-white').html('<i class="bi bi-stop-fill"></i> Bitir'); $out.html('<div class="small text-body-secondary">🎤 Dinliyorum… cümleyi söyle</div>'); }
                        else if (st === 'transcribing') { $btn.prop('disabled', true).html('<span class="spinner-border spinner-border-sm"></span>'); }
                    }
                });
                $out.html(heard ? this.render(target, heard) : '<div class="small text-body-secondary">Ses duyulmadı, tekrar dene.</div>');
            } catch (e) {
                showError(e);
                $out.empty();
            } finally {
                $btn.prop('disabled', false).removeClass('btn-danger text-white').html(html);
            }
        },

        button(text, label = 'Tekrar et') {
            return `<button type="button" class="btn btn-sm btn-outline-primary shadow-btn" data-shadow-text="${esc(text)}" title="Cümleyi sesli tekrar et, telaffuzunu kontrol et"><i class="bi bi-mic"></i> ${label}</button>`;
        }
    };
    $(document).on('click', '[data-shadow-text]', function (e) { e.preventDefault(); e.stopPropagation(); shadow.run(this); });

    window.App = { api, ApiError, toast, error, confirm, showError, tts, stt, audio, shadow, store, fmt, esc, busy, levelNames, posNames };
})(window, jQuery);
