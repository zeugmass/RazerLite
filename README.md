# RazerLite 0.25

Razer HyperPolling Wireless Dongle (`1532:00B3`) ve DeathAdder V3 Pro / Viper V3 Pro için
Synapse'siz DPI / polling rate aracı. Tek dosya `RazerLite.exe`, Windows HID API (`HidD_SetFeature`) üzerinden konuşur;
sürücü/servis kurmaz.

DeathAdder V4 Pro (`00BE`/`00BF`) protokol olarak aynı komutları kullandığı için listede yer alır; gerçek cihazda
henüz doğrulanmadı — çalışmama ihtimali var. Çalışmazsa aşağıdaki [Sorun giderme](#sorun-giderme) bölümüne bak.

**Kablolu / kablosuz:** Program USB tak-çıkar olaylarını izler. Kabloyu takınca kablolu fareye (örn. `00B6`),
çıkarınca dongle'a otomatik geçer; yeniden başlatmak gerekmez. Kablolu modda pil "Şarj oluyor" gösterir ve
polling rate seçenekleri farenin kablolu limitine göre (DA V3 Pro: 125/500/1000 Hz) kısıtlanır.
Kablolu DeathAdder V3 Pro'da genişletilmiş yoklama sorgusu gönderilmez. Profil kablolu ve kablosuz hızı ayrı tutar;
kablo takılıyken kaydetmek kablosuz hızın üzerine yazmaz.

## Klasör

| Dosya | Amaç |
|---|---|
| `build.cmd` | Tek EXE'yi derler (`RazerLite.exe`). Sadece .NET SDK 10 gerekir. |
| `src\` | Kaynak kod (C# / WPF). |
| `PollingRateTesterApp_v1.00.01.exe` | "Hz Testi" butonunun açtığı Razer test aracı. Derlemede `RazerLite.exe`'nin içine gömülür; ayrıca kopyalamaya gerek yok. |

## Derleme

`build.cmd` dosyasına çift tıkla. .NET SDK yoksa nasıl kurulacağını söyler
(`winget install Microsoft.DotNet.SDK.10`). Çıktı: bu klasörde `RazerLite.exe`.

## Yeni sürüm yayınlama

Sürüm tek yerde tutulur: `src/RazerLite.csproj` içindeki `<Version>`. Başlıktaki, menüdeki ve Hakkında'daki sürüm
yazıları buradan okunur.

1. `<Version>`'ı yeni sürüme çek (ör. `0.25.0`), commit'le ve pushla.
2. Aynı sürümle bir etiket pushla:
   ```
   git tag v0.25.0
   git push origin v0.25.0
   ```
3. GitHub Actions ([.github/workflows/release.yml](.github/workflows/release.yml)) exe'yi etiketteki sürümle derler,
   `RazerLite v0.25.0` adlı bir release oluşturur ve `RazerLite.exe`'yi ekler. Sürüm notları commit'lerden üretilir;
   istersen release sayfasından düzenleyebilirsin.

`-` içeren etiketler (ör. `v0.25.0-beta`) ön sürüm olarak işaretlenir ve programdaki denetimde görünmez.
Exe imzasız olduğu için Windows SmartScreen ilk açılışta uyarı verebilir.

## Arayüz

Koyu tema, tek parça üst çubuk (Windows başlık çubuğu yok; pencere üst kenarından sürüklenir, kenarlardan
boyutlandırılır). İçerik sığmazsa (ör. uyarı satırı çıkınca) pencere çalışma alanına kadar kendiliğinden uzar; ekran
da yetmezse sayfa ince bir kaydırma çubuğuyla kaydırılır, hiçbir yazı kesilmez. Sol menüde üç sayfa:

- **Ana Sayfa:** DPI ve polling rate, profil seçimi / kaydet / sil, Uygula; DPI kademeleri; cihazın desteklediği
  kartlar (Boşta Uyku, Düşük Güç, Yüzey Kalibrasyonu, Dongle LED). Cihaz bir özelliği desteklemiyorsa o kart
  gizlenir; kalan kartlar sütunu doldurur.
- **Ayarlar:** Windows ile başlat, kapatınca tepsiye küçült, düşük pil uyarısı, güncellemeleri otomatik denetle,
  Debug / Device Log anahtarı ve cihaz günlüğü.
- **Hakkında:** sürüm ve program güncellemesi, bağlı cihaz bilgisi (fare / dongle yazılım sürümü, firmware
  denetimi, tanılama raporu), desteklenen cihazlar, kısayollar ve proje sayfası bağlantısı.

Başlıkta pil halkası (yüzde kadar dolar; rengi pile göre yeşil / sarı / kırmızı), Hz Testi ve Yenile; sol altta
cihaz durumu kartı (bağlantı türüne göre kablosuz / kablolu ikonu, durum ve cihaz adı) bulunur.

## Özellikler

- DPI ve polling rate (125–8000 Hz) okuma/yazma, uygulama sonrası doğrulama
- Pil yüzdesi ve şarj durumu (2 dakikada bir yenilenir); düşük pil uyarısı: %20 ve altında, şarjda değilken sağ altta kendi uyarı penceresi (Windows bildirimleri kapalı olsa da görünür), her %5 düşüşte tekrar eder; tam ekran oyun/sunumda ertelenir
- Boşta uyku: 1–15 dakika. Kaydırıcı bırakılınca fareye yazılır
- Düşük güç: kablosuzdayken pilin bu yüzdenin (%5–%100, adım 5) altına düşünce fare düşük güce geçer. Yoklama 1000 Hz üzerindeyken kaydırıcı kapalıdır; kablodayken de görünür
- Yüzey kalibrasyonu: akıllı izleme 1 / 2 / 3 mm. Asimetrik kesim açılınca kaldırma ve iniş ayrı kaydırıcı olur (1–26). Seçim anında uygulanır. Elle yüzey tarama sihirbazı yoktur
- Yazılım sürümü: fare `0x00/0x87`, dongle `0x00/0x93`. Hakkında’daki Bağlı cihaz kartında görünür. "Firmware güncellemesini kontrol et" Razer’ın o fare için yayınladığı kurulum sürümünü okur. Daha yeniyse kayıt yeri sorulur ve kurulum dosyası indirilir. Aynı veya daha eskiyse güncel der. Dosya çalıştırılmaz, cihaza yazılmaz. Dongle’ın çip arşivi (hex zip) güncelleme olarak sunulmaz
- Program güncellemesi: Hakkında → "Güncellemeleri denetle" GitHub'daki son release'e bakar. Yeni sürüm varsa sürüm notlarını gösterir; "İndir ve yeniden başlat" yeni `RazerLite.exe`'yi yalnızca bu reponun release'inden indirir, boyut / exe başlığı / (GitHub veriyorsa) SHA-256 ile doğrular, eskisinin yerine koyar ve programı yeniden açar (bir adım başarısız olursa eski exe geri konur). Ayarlar'daki anahtar açıkken açılışta günde en fazla bir kez kendiliğinden denetler; yeni sürüm varsa menüde Hakkında'nın yanında yeşil nokta ve tepsi bildirimi çıkar
- Dongle LED. Çubuk dongle (`00B3`): Bağlantı, Pil durumu, Yalnızca düşük pil. Üç LED'li yarım küre dongle Windows'ta ayrı cihaz açmaz; DeathAdder V4 Pro kablosuz (`00BF`) üzerinden okunur. Cevap gelirse soldan sağa LED 1, LED 2 ve LED 3 ayrı seçilir. Pil ışığı yüzdeye göre kırmızıdan yeşile kayar (%50 sarı, %100 yeşil). Bağlantı kalitesi cihazdan okunur: zayıf kırmızı, fena değil turuncu, iyi sarı, mükemmel yeşil. Polling Rate: 125 mor, 500 mavi, 1000 yeşil, 2000 sarı, 4000 turuncu, 8000 kırmızı. DPI kademesi: 1 kırmızı, 2 yeşil, 3 mavi, 4 camgöbeği, 5 sarı. Seçilenin anlamı kutunun altında yazar. Cevap yoksa kart gizlenir
- DPI kademeleri: farenin altındaki DPI tuşunun döndüğü 1–5 kademelik tablo okunur ve tablonun tamamı fareye yazılır. Bu tablo profilde saklanmaz
- Profiller (en fazla 5): her biri bir DPI, bir kablolu Hz ve bir kablosuz Hz tutar. Aktif bağlantının hızı uygulanır, diğeri durur. Kaydet / güncelle / sil; `Ctrl+Alt+1…5` global kısayol veya tepsi menüsünden tek tıkla uygula
- Sistem tepsisi: X'e basınca tepsiye küçülür (kapatılabilir), çift tık geri getirir, sağ tık menüsünde profiller ve çıkış
- Windows ile başlat (`--minimized` ile tepside açılır)
- Debug / Device Log anahtarı (Ayarlar sayfası)
- Tek örnek: exe tekrar çalıştırılırsa açık olan pencere öne gelir

Ayarlar: `%AppData%\RazerLite\settings.json`. Beklenmeyen hata olursa aynı klasöre `crash.log` yazılır.

## Notlar

- Polling rate değişiminde "SET step 2: TIMEOUT" görülmesi normaldir; dongle yeniden numaralanır, program bağlantıyı kendisi yeniler ve okuyarak doğrular.
- Synapse ile aynı anda çalışabilir; ikisi de aynı HID yolunu paylaşımlı açar.
- Kablosuz (dongle) modda **fare hareket ederken** dongle ayar komutlarını BUSY ile geri çevirir (ölçüm: hareket halinde ~%85, polling hızından bağımsız). Program komutu Synapse ile aynı tempoda yeniden gönderir (25 ms aralık, en fazla 20 deneme); fare durduğu an yanıt gelir. Yine de yanıt alınamazsa ekrandaki değerler korunur, "Fare yanıt vermiyor" gösterilir ve fare tekrar ulaşılabilir olunca eksik okuma kendiliğinden tamamlanır. Ayar değiştirirken fareyi sabit tutmak en hızlısıdır.
- Ayar yazılmadan önce Synapse gibi cihaz modu 3 uygulanır.
- Bilgisayar açıldığında veya dongle yeni takıldığında dongle hemen görünür, fare telsizi birkaç saniye sürebilir. İlk okuma düşerse durum "Fare bağlanıyor…" olur ve bağlantı kurulana kadar birkaç saniyede bir kendiliğinden yeniden denenir; Yenile gerekmez.
- Bilgisayar uykuya/bekletmeye girince HID bağlantısı bırakılır; uyanınca program Yenile'ye basmadan otomatik yeniden bağlanır (USB hazır olana kadar birkaç saniye sürebilir).

## Sorun giderme

**Otomatik tarama.** Her bağlantıda program cihaza yalnızca okuma yapan tek bir komut gönderir (firmware sürümü,
`0x00/0x81`) ve cihazın yanıt verip vermediğine bakar. Yanıt gelirse ek bir şey yapılmaz. Gelmezse sırayla şunlar
denenir; hiçbiri cihaza ayar yazmaz:

1. Farklı transaction ID'ler: `0x1F` (varsayılan), `0x3F`, `0x9F`, `0xFF`
2. Aynı cihazın diğer USB arayüzleri (varsayılan `mi_00`)

Yanıt veren değer o oturum boyunca kullanılır. Denemeler Debug log'da `PROBE ->` satırları olarak görünür.

**Cihaz hâlâ çalışmıyorsa** (DPI/Hz okunmuyor, "Fare yanıt vermiyor", kartlar boş):

1. Programı aç, fareyi bağla, birkaç saniye bekle ve **Yenile**'ye bas.
2. **Hakkında** sayfasında **Tanılama raporunu kopyala**'ya bas (Ayarlar → Cihaz Günlüğü'ndeki
   **Raporu kopyala** da aynı raporu verir).
3. [GitHub'da yeni bir issue](https://github.com/zeugmass/RazerLite/issues/new) aç, fare modelini yaz ve raporu
   yapıştır.

Rapor cihaz adını ve PID'ini, HID yolunu, kullanılan arayüz ve transaction ID'yi, firmware sürümünü, tarama
sonuçlarını, desteklenen özellikleri ve son log satırlarını içerir. Kişisel bilgi içermez.

**İleri seviye (isteğe bağlı):** Synapse'in fareyle nasıl konuştuğunu görmek istersen
[USBPcap](https://desowin.org/usbpcap/) ve Wireshark ile Synapse açıkken DPI değiştirip trafiği kaydedebilirsin.
`SET_REPORT` paketlerinde 2. bayt transaction ID'yi, 7–8. baytlar komut sınıfı ve numarasını gösterir. Kaydı
(`.pcapng`) issue'ya eklemek protokolün doğrulanmasını çok hızlandırır.
