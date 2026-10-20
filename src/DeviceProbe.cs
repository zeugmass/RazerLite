namespace RazerLite;

/// <summary>
/// Dogrulanmamis / bilinmeyen cihazlar icin salt-okuma tarama. Cihaza yalnizca "firmware surumu oku"
/// (0x00/0x81) gonderir; hicbir ayar yazmaz.
///
/// Sira: mevcut transaction ID -> diger aday ID'ler -> ayni PID'in diger USB arayuzleri.
/// Cihaz paketi tanidigini yankiyla (sinif/komut ayni) gosterdigi an durur: BUSY / TIMEOUT / NOT_SUPPORTED
/// de "dogru adres" demektir (ornegin fare hareket ediyor veya telsiz henuz baglanmadi).
/// Bilinen cihazlarda ilk okuma yanki verdigi icin ek gecikme olmaz.
/// </summary>
internal static class DeviceProbe
{
    /// <summary>Razer cihazlarinda gorulen transaction ID'ler; 0x1F bu programdaki tum bilinen cihazlar.</summary>
    public static readonly byte[] TransactionCandidates = [0x1F, 0x3F, 0x9F, 0xFF];

    private const int AttemptBudgetMs = 600;

    public sealed record Attempt(string Interface, byte TransactionId, string Result);

    public sealed class Result
    {
        public List<Attempt> Attempts { get; } = [];
        public byte? TransactionId { get; set; }
        public string Interface { get; set; } = "";
        public string? Firmware { get; set; }
        public bool Responded => TransactionId is not null;

        /// <summary>Varsayilan disinda bir deger bulunup kullanima alindi.</summary>
        public bool UsedFallback { get; set; }
    }

    /// <summary>
    /// Taramayi yapar; gerekirse cihazin arayuzunu degistirir (device.Adopt) ve bulunan transaction ID'yi
    /// HidWin.TransactionOverrides'a yazar. Hicbiri yanit vermezse varsayilanlar degismez.
    /// </summary>
    public static async Task<Result> RunAsync(HidWin.Device device, Action<string>? log = null)
    {
        var result = new Result { Interface = HidWin.InterfaceOf(device.Path) };

        // 1) Ayni arayuz, once mevcut ID, sonra digerleri.
        if (await TryInterfaceAsync(device, result, log))
        {
            return result;
        }

        // 2) Ayni PID'in diger USB arayuzleri.
        int pid = device.Pid;
        var others = HidWin.ListRazerPaths()
            .Where(p => HidWin.PidFromPath(p) == pid && !string.Equals(p, device.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var path in others)
        {
            using var candidate = HidWin.TryOpenPath(path);
            if (candidate is null)
            {
                result.Attempts.Add(new Attempt(HidWin.InterfaceOf(path), 0, "acilamadi"));
                continue;
            }

            if (await TryInterfaceAsync(candidate, result, log))
            {
                var iface = HidWin.InterfaceOf(path);
                HidWin.InterfaceOverrides[pid] = iface;
                device.Adopt(candidate.Detach(), path);
                result.Interface = iface;
                result.UsedFallback = true;
                log?.Invoke($"PROBE -> arayuz {iface} kullaniliyor");
                return result;
            }
        }

        log?.Invoke("PROBE -> hicbir arayuz / transaction ID yanit vermedi; varsayilanlar kullaniliyor");
        return result;
    }

    private static async Task<bool> TryInterfaceAsync(HidWin.Device device, Result result, Action<string>? log)
    {
        var iface = HidWin.InterfaceOf(device.Path);
        var order = TransactionCandidates.Prepend(device.TransactionId).Distinct();

        foreach (var tx in order)
        {
            string outcome;
            try
            {
                var reply = await Protocol.RequestAsync(
                    device,
                    Protocol.MakeReport(0x02, 0x00, 0x81, []),
                    AttemptBudgetMs,
                    transactionId: tx);

                // Transaction ID de yankilanmali: tamponda kalan eski bir 0x81 yaniti yanlis ID'yi "dogru" gostermesin.
                bool echo = reply[1] == tx && reply[6] == 0x00 && reply[7] == 0x81;
                outcome = echo ? Protocol.StatusText(reply[0]) : "yanit yok";
                result.Attempts.Add(new Attempt(iface, tx, outcome));
                log?.Invoke($"PROBE -> {iface} tx 0x{tx:X2}: {outcome}");

                if (echo)
                {
                    result.TransactionId = tx;
                    result.Interface = iface;
                    if (Protocol.IsSuccess(reply[0]))
                    {
                        result.Firmware = $"v{reply[8]}.{reply[9]}";
                    }

                    if (tx != Protocol.Tx)
                    {
                        HidWin.TransactionOverrides[device.Pid] = tx;
                        result.UsedFallback = true;
                        log?.Invoke($"PROBE -> transaction ID 0x{tx:X2} kullaniliyor");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                outcome = "hata: " + ex.Message;
                result.Attempts.Add(new Attempt(iface, tx, outcome));
                log?.Invoke($"PROBE -> {iface} tx 0x{tx:X2}: {outcome}");

                // Bu arayuz Razer feature raporu kabul etmiyor; diger ID'leri denemek anlamsiz.
                return false;
            }
        }

        return false;
    }
}
