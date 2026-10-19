# RazerLite 0.16

Razer HyperPolling Wireless Dongle (`1532:00B3`) ve DeathAdder V3 Pro / Viper V3 Pro için
Synapse'siz DPI / polling rate aracı. Tek dosya `RazerLite.exe`, Windows HID API (`HidD_SetFeature`) üzerinden konuşur;
sürücü/servis kurmaz.

DeathAdder V4 Pro (`00BE`/`00BF`) protokol olarak aynı komutları kullandığı için listede yer alır; gerçek cihazda
henüz doğrulanmadı — çalışmama ihtimali var.

**Kablolu / kablosuz:** Program USB tak-çıkar olaylarını izler. Kabloyu takınca kablolu fareye (örn. `00B6`),
çıkarınca dongle'a otomatik geçer; yeniden başlatmak gerekmez. Kablolu modda pil "şarj oluyor" gösterir ve
polling rate seçenekleri farenin kablolu limitine göre (DA V3 Pro: 125/500/1000 Hz) kısıtlanır.
Kablolu DeathAdder V3 Pro'da genişletilmiş yoklama sorgusu gönderilmez. Profil kablolu ve kablosuz hızı ayrı tutar;
kablo takılıyken kaydetmek kablosuz hızın üzerine yazmaz.

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
- Pil yüzdesi ve şarj durumu (2 dakikada bir yenilenir); düşük pil uyarısı: %20 ve altında, şarjda değilken sağ altta kendi uyarı penceresi (Windows bildirimleri kapalı olsa da görünür), her %5 düşüşte tekrar eder; tam ekran oyun/sunumda ertelenir
- Boşta uyku: 1–15 dakika. Kaydırıcı bırakılınca fareye yazılır
- Düşük güç: kablosuzdayken pilin bu yüzdenin (%5–%100, adım 5) altına düşünce fare düşük güce geçer. Yoklama 1000 Hz üzerindeyken kaydırıcı kapalıdır; kablodayken de görünür
- Yüzey kalibrasyonu: akıllı izleme 1 / 2 / 3 mm. Asimetrik kesim açılınca kaldırma ve iniş ayrı kaydırıcı olur (1–26). Seçim anında uygulanır. Elle yüzey tarama sihirbazı yoktur
- Dongle LED (yalnızca HyperPolling dongle; kablolu farede gizli): Bağlantı, Pil durumu, Yalnızca düşük pil
- DPI kademeleri: farenin altındaki DPI tuşunun döndüğü 1–5 kademelik tablo okunur ve tablonun tamamı fareye yazılır. Bu tablo profilde saklanmaz
- Profiller (en fazla 5): her biri bir DPI, bir kablolu Hz ve bir kablosuz Hz tutar. Aktif bağlantının hızı uygulanır, diğeri durur. Kaydet / güncelle / sil; `Ctrl+Alt+1…5` global kısayol veya tepsi menüsünden tek tıkla uygula
- Sistem tepsisi: X'e basınca tepsiye küçülür (kapatılabilir), çift tık geri getirir, sağ tık menüsünde profiller ve çıkış
- Windows ile başlat (`--minimized` ile tepside açılır)
- Debug / Device Log anahtarı
- Tek örnek: exe tekrar çalıştırılırsa açık olan pencere öne gelir

Ayarlar: `%AppData%\RazerLite\settings.json`. Beklenmeyen hata olursa aynı klasöre `crash.log` yazılır.

## Notlar

- Polling rate değişiminde "SET step 2: TIMEOUT" görülmesi normaldir; dongle yeniden numaralanır, program bağlantıyı kendisi yeniler ve okuyarak doğrular.
- Synapse ile aynı anda çalışabilir; ikisi de aynı HID yolunu paylaşımlı açar.
- Kablosuz (dongle) modda **fare hareket ederken** dongle ayar komutlarını BUSY ile geri çevirir (ölçüm: hareket halinde ~%85, polling hızından bağımsız). Program komutu Synapse ile aynı tempoda yeniden gönderir (25 ms aralık, en fazla 20 deneme); fare durduğu an yanıt gelir. Yine de yanıt alınamazsa ekrandaki değerler korunur, "Fare yanıt vermiyor" gösterilir ve fare tekrar ulaşılabilir olunca eksik okuma kendiliğinden tamamlanır. Ayar değiştirirken fareyi sabit tutmak en hızlısıdır.
- Ayar yazılmadan önce Synapse gibi cihaz modu 3 uygulanır.
- Bilgisayar açıldığında veya dongle yeni takıldığında dongle hemen görünür, fare telsizi birkaç saniye sürebilir. İlk okuma düşerse durum "Fare bağlanıyor…" olur ve bağlantı kurulana kadar birkaç saniyede bir kendiliğinden yeniden denenir; Yenile gerekmez.
- Bilgisayar uykuya/bekletmeye girince HID bağlantısı bırakılır; uyanınca program Yenile'ye basmadan otomatik yeniden bağlanır (USB hazır olana kadar birkaç saniye sürebilir).
