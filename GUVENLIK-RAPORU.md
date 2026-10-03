# Güvenlik taraması — orijinal Omarchy plugin'leri

Tarih: 2026-10-03. Her repo `git clone --depth 1` ile indirildi, tüm dosyalar
şunlar için tarandı: uzak ağ çağrıları (curl/wget/http), base64/obfuscation,
`eval`/`exec`, `sudo`/`pkexec`, `rm -rf`, `.ssh`/`authorized_keys`/`.bashrc`
değişikliği, `LD_PRELOAD`, crontab/systemd kalıcılığı, gizli uzun stringler.

| Plugin | Repo | Satır | Sonuç |
|---|---|---|---|
| omabisync | github.com/hverbruggen/omabisync | ~1.9k | ✅ Temiz. Sadece `systemctl --user` ve `journalctl` okur, "Sync now" kullanıcı servisini başlatır. |
| syncthing.bar | github.com/BeringLogic/omarchy-syncthing-plugin | ~3.8k | ✅ Temiz. Sadece `127.0.0.1:8384`'e curl. API anahtarını argv'ye koymamak için özen gösterilmiş. ⚠️ Marketplace'te **"update-unverified"**: son güncelleme henüz incelenmemiş. `pkexec docker` kullanır (opsiyonel). |
| gadget-batteries | github.com/69Harold69/omarchy-gadget-batteries | ~3.1k | ✅ Temiz. BlueZ D-Bus, `solaar show`, `notify-send`. udev kuralı yalnızca dongle'ın vendor arayüzünü açıyor (tuş vuruşu arayüzleri değil). |
| lutfi.glass | github.com/lutfi-zain/omarchy-glass-blur | ~1.5k | ✅ Zararlı yok. 🐞 **Bug:** `bin/glass-ctl` satır 55'te tanımsız `block` değişkeni → fence bloğu yokken `NameError` ile çöker. `hyprctl eval` string'ine giden değerler float/int/hex-doğrulamalı, enjeksiyon yok. |
| mousekit | github.com/Enoret/omarchy-mousekit | ~2.5k | ✅ Temiz. Sadece `ratbagctl` sarmalayıcısı. |
| pocket | github.com/oidium/omarchy-pocket | ~3.2k | ✅ Temiz. KDE Connect D-Bus, kabuk enterpolasyonu yok, cihaz ID'si regex ile doğrulanıyor. Marketplace'te "requires additional setup". |

**Virüs / zararlı kod bulunmadı.** Hiçbiri dışarıya veri göndermiyor; tüm ağ trafiği localhost veya yerel D-Bus.

## SwarlexBattery (Windows sürümü) güvenlik notları
- Yönetici izni **istemez**; tüm değişiklikler kullanıcı seviyesinde (HKCU, kullanıcı görevleri).
- İnternete çıkan tek istek yok; Syncthing yalnızca `apiBase` (varsayılan localhost).
- Cihaz ID'leri, docker container adı, Syncthing klasör ID'leri doğrulanır/URL-encode edilir; komutlar argv dizisi olarak çağrılır, string birleştirilmez.
- Syncthing yapılandırması asla tam `/rest/config` ile geri yazılmaz (API anahtarını silerdi); sadece ilgili klasöre PATCH.
- Glass motoru başka process'e kod enjekte etmez; yalnızca standart `SetLayeredWindowAttributes` kullanır ve çıkışta geri alır.

## Güncelleyici
- Sadece `api.github.com/repos/yukicanclaude/SwarlexBattery/releases/latest` okunur; indirme adresi `github.com` dışındaysa reddedilir.
- İndirilen exe, aynı sürümdeki `SwarlexBattery.exe.sha256` ile karşılaştırılır; uyuşmazsa silinir ve kurulmaz. Dosyanın gerçekten bir exe (MZ başlığı) olduğu da kontrol edilir.
- Güncelleme sadece kullanıcı menüden "Güncelle"ye bastığında uygulanır; otomatik kurulum yoktur.
- Not: SHA-256 dosyası aynı GitHub sürümünden gelir. Bu, bozuk veya yarım indirmelere karşı korur; GitHub hesabının kendisinin ele geçirilmesine karşı korumaz. Hesapta iki adımlı doğrulamayı açık tut.
