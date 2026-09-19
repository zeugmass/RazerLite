namespace RazerLite;

internal static class Protocol
{
    public const byte Tx = 0x1F;

    public static readonly Dictionary<int, byte> PollToCode = new()
    {
        [8000] = 0x01,
        [4000] = 0x02,
        [2000] = 0x04,
        [1000] = 0x08,
        [500] = 0x10,
        [250] = 0x20,
        [125] = 0x40,
    };

    public static readonly Dictionary<byte, int> CodeToPoll =
        PollToCode.ToDictionary(kv => kv.Value, kv => kv.Key);

    // Klasik polling komutu (0x00/0x05 set, 0x00/0x85 get) - kablolu DeathAdder V3 Pro gibi
    // genisletilmis komutu desteklemeyen cihazlar icin. Kaynak: OpenRazer razer_chroma_misc_set_polling_rate.
    public static readonly Dictionary<int, byte> ClassicPollToCode = new()
    {
        [1000] = 0x01,
        [500] = 0x02,
        [125] = 0x08,
    };

    public static readonly Dictionary<byte, int> ClassicCodeToPoll =
        ClassicPollToCode.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static IReadOnlyCollection<int> SupportedRates(HidWin.Device device) =>
        device.ExtendedPolling == false ? ClassicPollToCode.Keys : PollToCode.Keys;

    public static string StatusText(byte code) => code switch
    {
        0x00 => "NEW",
        0x01 => "BUSY",
        0x02 => "SUCCESS",
        0x03 => "FAILURE",
        0x04 => "TIMEOUT",
        0x05 => "NOT_SUPPORTED",
        _ => $"0x{code:x2}",
    };

    public static byte[] MakeReport(
        byte dataSize,
        byte commandClass,
        byte commandId,
        byte[] args,
        byte transactionId = Tx)
    {
        if (dataSize > 80)
        {
            throw new ArgumentException("Invalid data size.");
        }

        if (args.Length > 80)
        {
            throw new ArgumentException("Invalid arguments.");
        }

        var r = new byte[HidWin.ReportSize];
        r[0] = 0x00;
        r[1] = transactionId;
        r[5] = dataSize;
        r[6] = commandClass;
        r[7] = commandId;
        Buffer.BlockCopy(args, 0, r, 8, args.Length);

        byte crc = 0;
        for (int i = 2; i < 88; i++)
        {
            crc ^= r[i];
        }

        r[88] = crc;
        return r;
    }

    public const int DefaultBudgetMs = 3000;

    public static bool IsSuccess(byte status) => status == 0x02;

    /// <summary>Cihaz mesgul / fare ulasilamaz: komut tekrar gonderilebilir.</summary>
    public static bool IsTransient(byte status) => status is 0x00 or 0x01 or 0x04;

    /// <summary>
    /// Komutu gonderir ve yaniti okur. Olcum (HyperPolling dongle, fare hareket ederken):
    /// dongle BUSY (0x01) dondugunde ayni yaniti tekrar okumak 5 sn boyunca hicbir sey
    /// degistirmiyor; komutu YENIDEN gondermek ise fare durdugu anda SUCCESS veriyor.
    /// Bu yuzden BUSY / TIMEOUT / yanki uyusmazliginda komut butce dolana kadar tekrar
    /// gonderilir. Fare sabitken ilk gonderim basarilidir; davranis oncekiyle aynidir.
    /// </summary>
    /// <param name="resendOnTimeout">
    /// false: TIMEOUT'ta hemen don. Polling SET icin kullanilir; orada TIMEOUT dongle'in komutu
    /// kabul edip yeniden numaralandigi anlamina gelir, komutu tekrar gondermek gerekmez.
    /// BUSY ise komutun kabul edilmedigi anlamina gelir ve her zaman tekrar gonderilir.
    /// </param>
    public static async Task<byte[]> RequestAsync(
        HidWin.Device device,
        byte[] report,
        int budgetMs = DefaultBudgetMs,
        bool resendOnTimeout = true)
    {
        var started = Environment.TickCount64;
        byte[]? last = null;

        while (true)
        {
            HidWin.SetFeature(device, report);
            await Task.Delay(70);

            var data = HidWin.GetFeature(device);
            last = data;

            bool echoOk = data[6] == report[6] && data[7] == report[7];
            if (echoOk && !IsTransient(data[0]))
            {
                return data;
            }

            if (!resendOnTimeout && data[0] == 0x04)
            {
                return data;
            }

            if (Environment.TickCount64 - started >= budgetMs)
            {
                return last;
            }

            await Task.Delay(100);
        }
    }

    public static async Task<(int? Hz, byte Raw, byte Status)> GetPollingAsync(HidWin.Device device)
    {
        if (device.ExtendedPolling != false)
        {
            var reply = await RequestAsync(
                device,
                MakeReport(0x01, 0x00, 0xC0, [0x00, 0x00]));

            if (reply[0] != 0x05)
            {
                if (reply[0] == 0x02)
                {
                    device.ExtendedPolling = true;
                }

                CodeToPoll.TryGetValue(reply[9], out var hz);
                return (CodeToPoll.ContainsKey(reply[9]) ? hz : null, reply[9], reply[0]);
            }

            // NOT_SUPPORTED: bu cihaz klasik polling komutunu kullanir.
            device.ExtendedPolling = false;
        }

        var classic = await RequestAsync(
            device,
            MakeReport(0x01, 0x00, 0x85, [0x00]));

        ClassicCodeToPoll.TryGetValue(classic[8], out var chz);
        return (ClassicCodeToPoll.ContainsKey(classic[8]) ? chz : null, classic[8], classic[0]);
    }

    private static async Task<(int? Hz, byte Raw, byte Status)> SetPollingClassicAsync(
        HidWin.Device device,
        int hz,
        Action<string>? log)
    {
        if (!ClassicPollToCode.TryGetValue(hz, out var code))
        {
            throw new ArgumentException(
                $"{device.Info.Title} bu baglantida {hz} Hz desteklemiyor. Secenekler: 125 / 500 / 1000 Hz.");
        }

        try
        {
            var reply = await RequestAsync(
                device,
                MakeReport(0x01, 0x00, 0x05, [code]),
                resendOnTimeout: false);
            log?.Invoke($"SET classic: {StatusText(reply[0])}");
            ThrowIfBusy(reply[0], "Polling");
        }
        catch (DeviceBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"SET classic: device reset ({ex.Message})");
        }

        await Task.Delay(250);
        await HidWin.ReconnectAsync(device);
        await Task.Delay(200);

        return await GetPollingAsync(device);
    }

    public static async Task<(int? Hz, byte Raw, byte Status)> SetPollingAsync(
        HidWin.Device device,
        int hz,
        Action<string>? log = null)
    {
        if (device.ExtendedPolling is null)
        {
            await GetPollingAsync(device); // yetenegi ogren
        }

        if (device.ExtendedPolling == false)
        {
            return await SetPollingClassicAsync(device, hz, log);
        }

        if (!PollToCode.TryGetValue(hz, out var code))
        {
            throw new ArgumentException($"Unsupported polling rate: {hz}");
        }

        try
        {
            // BUSY'de (fare hareket ediyor) tekrar gonderilir; TIMEOUT'ta (yeniden numaralanma) hemen doner.
            var first = await RequestAsync(
                device,
                MakeReport(0x02, 0x00, 0x40, [0x00, code], 0x1F),
                resendOnTimeout: false);
            log?.Invoke($"SET step 1: {StatusText(first[0])}");
            ThrowIfBusy(first[0], "Polling");
        }
        catch (DeviceBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"SET step 1: device reset ({ex.Message})");
        }

        try
        {
            var second = await RequestAsync(
                device,
                MakeReport(0x02, 0x00, 0x40, [0x01, code], 0xFF),
                resendOnTimeout: false);
            log?.Invoke($"SET step 2: {StatusText(second[0])}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"SET step 2: device reset ({ex.Message})");
        }

        await Task.Delay(250);
        await HidWin.ReconnectAsync(device);
        await Task.Delay(200);

        return await GetPollingAsync(device);
    }

    /// <summary>Fare hareket ettigi icin komut kabul edilmedi (butce doldu, hala BUSY).</summary>
    public sealed class DeviceBusyException(string message) : InvalidOperationException(message);

    private static void ThrowIfBusy(byte status, string what)
    {
        if (status == 0x01)
        {
            throw new DeviceBusyException(
                $"{what} yazilamadi: fare mesgul (BUSY). Fare hareket ederken ayar degistirilemez; fareyi sabit tutup tekrar dene.");
        }
    }

    public static async Task<(int X, int Y, byte Status)> GetDpiAsync(HidWin.Device device)
    {
        var reply = await RequestAsync(
            device,
            MakeReport(0x07, 0x04, 0x85, [0x00]));

        int x = (reply[9] << 8) | reply[10];
        int y = (reply[11] << 8) | reply[12];
        return (x, y, reply[0]);
    }

    public static async Task<(int X, int Y, byte Status)> SetDpiAsync(HidWin.Device device, int dpi)
    {
        if (dpi is < 100 or > 45000)
        {
            throw new ArgumentException("DPI must be an integer between 100 and 45000.");
        }

        byte hi = (byte)((dpi >> 8) & 0xff);
        byte lo = (byte)(dpi & 0xff);

        var reply = await RequestAsync(
            device,
            MakeReport(0x07, 0x04, 0x05, [0x00, hi, lo, hi, lo]));

        if (!IsSuccess(reply[0]))
        {
            throw new DeviceBusyException(
                $"DPI yazilamadi: {StatusText(reply[0])}. Fare hareket ederken veya uykudayken ayar degistirilemez; fareyi sabit tutup tekrar dene.");
        }

        return await GetDpiAsync(device);
    }

    // ------------------------------------------------------------------
    // Ek ozellikler (pil, DPI kademeleri). Mevcut komut yollari degismedi.
    // Yanki dogrulama ve BUSY/TIMEOUT tekrar gonderimi RequestAsync icinde;
    // buradaki sarmalayici yalnizca butceyi tasir.
    // ------------------------------------------------------------------

    public const int MaxDpiStages = 5;

    public sealed record DpiStage(int Index, int X, int Y);

    public sealed record DpiStageSet(int Active, IReadOnlyList<DpiStage> Stages);

    public sealed record BatteryInfo(int? Percent, bool Charging, byte Status);

    private static Task<byte[]> RequestVerifiedAsync(HidWin.Device device, byte[] report, int budgetMs = DefaultBudgetMs) =>
        RequestAsync(device, report, budgetMs);

    /// <param name="budgetMs">Arka plan okumasi icin kisa tutulabilir (fare hareket ederken hizli vazgecer).</param>
    public static async Task<BatteryInfo> GetBatteryAsync(HidWin.Device device, int budgetMs = DefaultBudgetMs)
    {
        var level = await RequestVerifiedAsync(device, MakeReport(0x02, 0x07, 0x80, [0x00, 0x00]), budgetMs);
        if (level[0] != 0x02)
        {
            return new BatteryInfo(null, false, level[0]);
        }

        int percent = (int)Math.Round(level[9] * 100.0 / 255.0);

        var charging = await RequestVerifiedAsync(device, MakeReport(0x02, 0x07, 0x84, [0x00, 0x00]), budgetMs);
        bool isCharging = charging[0] == 0x02 && charging[9] == 0x01;

        return new BatteryInfo(percent, isCharging, level[0]);
    }

    public static async Task<DpiStageSet> GetDpiStagesAsync(HidWin.Device device)
    {
        const byte varstore = 0x01;
        string lastProblem = "yanit yok";

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var r = await RequestVerifiedAsync(device, MakeReport(0x26, 0x04, 0x86, [varstore]));
            if (r[0] != 0x02)
            {
                lastProblem = StatusText(r[0]);
                if (IsTransient(r[0]))
                {
                    // Butce zaten RequestAsync icinde tukendi; fare mesgul/uykuda. Tekrar beklemenin anlami yok.
                    break;
                }

                await Task.Delay(100);
                continue;
            }

            int active = r[9];
            int count = r[10];
            if (r[8] != varstore || count < 1 || count > MaxDpiStages || active < 1 || active > count)
            {
                lastProblem = $"tutarsiz baslik (vs={r[8]:x2} active={active} count={count})";
                await Task.Delay(100);
                continue;
            }

            var stages = new List<DpiStage>(count);
            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                int off = 11 + i * 7;
                int idx = r[off];
                int x = (r[off + 1] << 8) | r[off + 2];
                int y = (r[off + 3] << 8) | r[off + 4];
                if (idx != i + 1 || x == 0)
                {
                    ok = false;
                    lastProblem = $"kademe {i + 1} eksik (idx={idx} x={x})";
                    break;
                }

                stages.Add(new DpiStage(idx, x, y));
            }

            if (ok)
            {
                return new DpiStageSet(active, stages);
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "DPI kademeleri okunamadi: " + lastProblem
            + (lastProblem is "BUSY" or "TIMEOUT" ? " (fare hareket ediyor veya uykuda; sabitken Yenile)" : ""));
    }

    public static async Task<DpiStageSet> SetDpiStagesAsync(
        HidWin.Device device,
        int active,
        IReadOnlyList<int> dpis)
    {
        if (dpis.Count is < 1 or > MaxDpiStages)
        {
            throw new ArgumentException($"1 ile {MaxDpiStages} arasinda kademe girilmelidir.");
        }

        if (active < 1 || active > dpis.Count)
        {
            throw new ArgumentException("Aktif kademe girilen kademelerden biri olmalidir.");
        }

        foreach (var d in dpis)
        {
            if (d is < 100 or > 45000)
            {
                throw new ArgumentException("Her kademe 100 ile 45000 arasinda olmalidir.");
            }
        }

        var args = new byte[3 + 7 * dpis.Count];
        args[0] = 0x01;
        args[1] = (byte)active;
        args[2] = (byte)dpis.Count;
        for (int i = 0; i < dpis.Count; i++)
        {
            int off = 3 + i * 7;
            byte hi = (byte)((dpis[i] >> 8) & 0xff);
            byte lo = (byte)(dpis[i] & 0xff);
            args[off] = (byte)(i + 1);
            args[off + 1] = hi;
            args[off + 2] = lo;
            args[off + 3] = hi;
            args[off + 4] = lo;
        }

        var reply = await RequestVerifiedAsync(device, MakeReport((byte)args.Length, 0x04, 0x06, args));
        if (reply[0] != 0x02)
        {
            throw new InvalidOperationException("DPI kademeleri yazilamadi: " + StatusText(reply[0]));
        }

        await Task.Delay(150);
        return await GetDpiStagesAsync(device);
    }
}
