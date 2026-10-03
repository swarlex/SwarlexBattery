# SwarlexBattery

Kablosuz mouse, klavye ve kulaklıkların pilini Windows sistem tepsisinde gösterir.
Tek ikon: halkanın sol yarısı mouse, sağ yarısı kulaklık. Şarj olurken ortada şimşek çıkar,
cihaz uykudayken ikon soluklaşır. Sol tık detay paneli, sağ tık menü açar.

`dist\SwarlexBattery.exe` tek dosya, kurulum gerektirmez. Ek program (Python, .NET SDK) gerekmez.

## Desteklenen cihazlar

| Marka | Cihazlar | Yöntem |
|---|---|---|
| Razer | BlackShark V2 HyperSpeed (alıcı + kablo), BlackShark V2 Pro, kablosuz Razer mouse/klavyeler | MediaTek / "PA" / 90 bayt özellik raporu |
| Logitech | Lightspeed / Unifying / Bolt alıcılı mouse ve klavyeler, kablolu G serisi, G533/535/633/635/733/933/935, G PRO X (2) | HID++ 2.0 |
| SteelSeries | Arctis Nova 7 / 7X / 7P / 5 / 3, Arctis 7+, GameBuds, Nova Pro Wireless, Aerox 3/5/9, Rival 3 Wireless | b0 / d2 / aa sorguları |
| HyperX | Cloud II Wireless, Cloud III Wireless, Cloud Alpha 2 | üretici sorguları |
| Corsair | Void v2 Wireless, Virtuoso Max, HS80 Max | arayüz 4 sorgusu |
| ATK / VXE / Pulsar / Hitscan | MAD serisi ve aynı protokolü kullanan mouse'lar (alıcı + kablo) | 17 bayt güç sorgusu |
| Diğer | Windows Ayarlar'da pili görünen Bluetooth cihazlar, Xbox/XInput kumandalar, laptop pili | Windows API |

Kendi cihazın listede yoksa `%APPDATA%\SwarlexBattery\gadgets\collectors.d\` içine JSON yazan bir script koyabilirsin:
```json
[{"id":"speaker","name":"Hoparlör","kind":"speaker","pct":64,"charging":false}]
```

## Okumalar ne kadar doğru?

- Değer **sadece cihazın kendisi cevap verdiğinde** gösterilir; tahmin edilmiş ya da uydurulmuş değer yoktur.
- Cevap gelmezse son okuma 45 saniye boyunca gösterilir; sonra "uyku modunda — son okuma N dk önce" olarak soluk görünür, 24 saat sonra kaybolur.
- Cihazın kendisi kademeli seviye veriyorsa (ör. 4 kademeli kulaklıklar, voltajdan hesaplanan Logitech değerleri) panelde "yaklaşık" ve tooltip'te `~` görünür.
- Kablosuz alıcılar bazen kapalı cihazın eski değerini tekrar eder. Bu yüzden BlackShark'ta önce bağlantı durumu sorulur, ATK mouse'larda pil voltajı kontrol edilir; bu kontrolleri geçmeyen cevaplar gösterilmez.
- Kabloda ve %100 olan cihaz "şarj oluyor" değil, **"dolu"** olarak gösterilir.
- Cihaz takılıp çıkarıldığında (kablo, alıcı) piller 1,5 saniye içinde yeniden okunur; normalde 10 saniyede bir okunur.

## Güncellemeler

Uygulama açılışta ve 6 saatte bir `github.com/yukicanclaude/SwarlexBattery` deposundaki son sürüme bakar.
Yeni sürüm varsa bir bildirim gösterir ve sağ tık menüsünün en üstünde **"Güncelle (vX.Y.Z)"** çıkar.
Tıklayınca yeni exe indirilir, sürümle birlikte yayınlanan SHA-256 değeriyle doğrulanır, eskisiyle değiştirilir ve uygulama yeniden başlar.
Menüdeki "Güncellemeleri denetle" hemen kontrol eder.

Yeni sürüm yayınlamak için tek komut yeterli:
```powershell
.\tools\release.ps1 -Notes "Yenilikler"                  # 1.1.1 -> 1.1.2
.\tools\release.ps1 -Version 1.2.0 -Notes "Yenilikler"   # büyük değişiklik
```
Bu komut `VERSION` dosyasını artırır, exe'yi derler, tüm değişiklikleri commit'leyip GitHub'a gönderir ve
`SwarlexBattery.exe` + `SwarlexBattery.exe.sha256` ile yeni sürümü yayınlar. Derleme başarısız olursa hiçbir şey gönderilmez.
GitHub CLI (`gh`) kurulu ve `gh auth login` ile giriş yapılmış olmalı.

## Ayarlar

`%APPDATA%\SwarlexBattery\config.json`:
- `"plugins": { "gadgets": { "combine": false } }`: her cihaz için ayrı ikon
- `"plugins": { "gadgets": { "lowThreshold": 20 } }`: düşük pil bildirimi eşiği
- `"monochrome": false`: renkli ikonlar (turuncu/kırmızı düşük pil, yeşil şarj)
- `"update": { "check": false }`: otomatik güncelleme kontrolünü kapat

Günlük: `%LOCALAPPDATA%\SwarlexBattery\swarlexbattery.log`

## Derleme

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1     # -> dist\SwarlexBattery.exe (+ .sha256)
```
Windows'ta zaten bulunan C# derleyicisi kullanılır.

## Kaynaklar

Cihaz protokolleri şu açık kaynak projelerin belgelerinden uyarlanmıştır: HaloBattery (MIT), HeadsetControl, Solaar, OpenRazer,
justik13/razer-blackshark-v2-hyperspeed-webhid, python-pulsar-mouse-tool. Gönderilen tüm komutlar salt okunur pil/durum sorgularıdır;
cihaz ayarları değiştirilmez.
