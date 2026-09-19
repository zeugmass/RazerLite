# RazerLite 0.16

Razer HyperPolling Wireless Dongle (`1532:00B3`) ve DeathAdder V3 Pro / Viper V3 Pro / DeathAdder V4 Pro ailesi için
Synapse'siz DPI / polling rate aracı. Tek dosya `RazerLite.exe`, Windows HID API (`HidD_SetFeature`) üzerinden konuşur;
sürücü/servis kurmaz.

**Kablolu / kablosuz:** Program USB tak-çıkar olaylarını izler. Kabloyu takınca kablolu fareye (örn. `00B6`),
çıkarınca dongle'a otomatik geçer; yeniden başlatmak gerekmez. Kablolu modda pil "şarj oluyor" gösterir ve
polling rate seçenekleri farenin kablolu limitine göre (DA V3 Pro: 125/500/1000 Hz) kısıtlanır.

## Klasör

| Dosya | Amaç |
|---|---|
| `build.cmd` | Tek EXE'yi derler (`RazerLite.exe`). Sadece .NET SDK 8 gerekir. |
| `src\` | Kaynak kod (C# / WPF). |
| `PollingRateTesterApp_v1.00.01.exe` | "Hz Test" butonunun açtığı Razer test aracı. `RazerLite.exe` ile aynı klasörde durmalı. |

## Derleme

`build.cmd` dosyasına çift tıkla. .NET SDK yoksa nasıl kurulacağını söyler
(`winget install Microsoft.DotNet.SDK.8`). Çıktı: bu klasörde `RazerLite.exe`.

## Özellikler

- DPI ve polling rate (125–8000 Hz) okuma/yazma, uygulama sonrası doğrulama
- Pil yüzdesi ve şarj durumu (2 dakikada bir yenilenir)
- DPI kademeleri (farenin DPI tuşunun döndüğü 1–5 kademe) okuma/yazma
- Profiller (en fazla 5): kaydet / güncelle / sil; `Ctrl+Alt+1…5` global kısayol veya tepsi menüsünden tek tıkla uygula
- Sistem tepsisi: X'e basınca tepsiye küçülür (kapatılabilir), çift tık geri getirir, sağ tık menüsünde profiller ve çıkış
- Windows ile başlat (`--minimized` ile tepside açılır)
- Tek örnek: exe tekrar çalıştırılırsa açık olan pencere öne gelir

Ayarlar: `%AppData%\RazerLite\settings.json`. Beklenmeyen hata olursa aynı klasöre `crash.log` yazılır.

## Notlar

- Polling rate değişiminde "SET step 2: TIMEOUT" görülmesi normaldir; dongle yeniden numaralanır, program bağlantıyı kendisi yeniler ve okuyarak doğrular.
- Synapse ile aynı anda çalışabilir; ikisi de aynı HID yolunu paylaşımlı açar.
- Kablosuz (dongle) modda **fare hareket ederken** dongle ayar komutlarını BUSY ile geri çevirir (ölçüm: hareket halinde ~%85, polling hızından bağımsız). Program komutu 3 sn'ye kadar yeniden gönderir; fare durduğu an yanıt gelir. Yine de yanıt alınamazsa ekrandaki değerler korunur, "Fare yanıt vermiyor" gösterilir ve fare tekrar ulaşılabilir olunca eksik okuma kendiliğinden tamamlanır. Ayar değiştirirken fareyi sabit tutmak en hızlısıdır.
- Dongle yeni takıldığında fare ile telsiz bağlantı birkaç saniye sürer; program ilk okuma başarısızsa 2 sn arayla iki kez daha dener.
