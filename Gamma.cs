using System.Text.Json;

namespace Brilho;

/// Escurecimento pela curva de gama da placa de vídeo: a rota `.software` do
/// Vorssaint. A curva original de cada tela é guardada antes da primeira
/// mudança e escalada de forma linear (fator 1 devolve a original bit a bit;
/// fator 0 é preto). Funciona em qualquer conexão, inclusive TVs por HDMI que
/// não falam DDC/CI. Só a thread da interface mexe aqui.
sealed class GammaController
{
    const int RampSize = 256 * 3;

    readonly Dictionary<string, ushort[]> baselines = new();
    /// Telas cuja curva atual é nossa. Enquanto estão aqui a curva viva é uma
    /// cópia escalada, e relê-la como "original" escureceria a tela de novo a
    /// cada reconexão até ficar preta — por isso nunca é relida.
    readonly HashSet<string> dimmed = new();
    readonly Dictionary<string, ushort[]> applied = new();
    readonly GammaJournal journal = GammaJournal.Load();

    public GammaController()
    {
        // Diferente do macOS, o Windows mantém a curva depois que o processo
        // morre (testado nesta máquina). O diário em disco é o que permite
        // desfazer o escurecimento depois de uma falha ou de um "Finalizar tarefa".
        foreach (var (fingerprint, entry) in journal.Entries)
        {
            baselines[fingerprint] = entry.Ramp;
            dimmed.Add(fingerprint);
        }
    }

    public bool IsDimmed(string fingerprint) => dimmed.Contains(fingerprint);

    public bool Apply(DisplayInfo display, double factor)
    {
        string fp = display.Fingerprint;
        if (!EnsureBaseline(display)) return false;
        var baseline = baselines[fp];
        if (factor >= 0.999)
        {
            if (!dimmed.Contains(fp)) return true;
            bool restored = SetRamp(display.GdiName, baseline);
            if (restored)
            {
                dimmed.Remove(fp);
                applied.Remove(fp);
                journal.Remove(fp);
            }
            return restored;
        }

        var ramp = Scale(baseline, factor);
        bool firstDim = !dimmed.Contains(fp);
        if (firstDim) journal.Add(fp, display.GdiName, baseline); // antes de escurecer, nunca depois
        bool ok = SetRamp(display.GdiName, ramp);
        if (ok)
        {
            dimmed.Add(fp);
            applied[fp] = ramp;
        }
        else if (firstDim) journal.Remove(fp);
        return ok;
    }

    /// Mudança de resolução, jogos e o próprio Windows podem trocar a curva
    /// por baixo do app; isto devolve a nossa.
    public void Watchdog(IEnumerable<DisplayInfo> displays)
    {
        foreach (var display in displays)
        {
            if (!applied.TryGetValue(display.Fingerprint, out var expected)) continue;
            var live = GetRamp(display.GdiName);
            if (live == null || (live[255] == expected[255] && live[128] == expected[128] && live[767] == expected[767])) continue;
            SetRamp(display.GdiName, expected);
            Log.Write($"curva de {display.FriendlyName} foi trocada pelo sistema; reaplicada");
        }
    }

    /// Para o modo linha de comando, que não guarda estado entre execuções.
    public double EstimateLevel(DisplayInfo display)
    {
        if (!dimmed.Contains(display.Fingerprint) || !baselines.TryGetValue(display.Fingerprint, out var baseline)) return 1;
        var live = GetRamp(display.GdiName);
        if (live == null || baseline[255] == 0) return 1;
        return Math.Clamp((double)live[255] / baseline[255], 0, 1);
    }

    /// Devolve a curva original de todas as telas que o app escureceu. Uma tela
    /// ausente agora fica no diário até voltar.
    public void RestoreAll(IEnumerable<DisplayInfo> displays)
    {
        foreach (var display in displays)
            if (dimmed.Contains(display.Fingerprint)) Apply(display, 1);
    }

    bool EnsureBaseline(DisplayInfo display)
    {
        if (dimmed.Contains(display.Fingerprint) && baselines.ContainsKey(display.Fingerprint)) return true;
        // Não escurecida por nós: relida toda vez, para que um perfil de cor
        // novo vire a base de tudo.
        var live = GetRamp(display.GdiName);
        if (live == null) return false;
        baselines[display.Fingerprint] = live;
        return true;
    }

    static ushort[] Scale(ushort[] baseline, double factor)
    {
        var ramp = new ushort[RampSize];
        double f = Math.Clamp(factor, 0, 1);
        for (int i = 0; i < RampSize; i++) ramp[i] = (ushort)Math.Round(baseline[i] * f);
        return ramp;
    }

    static ushort[] GetRamp(string device)
    {
        IntPtr hdc = Native.CreateDC(null, device, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            var ramp = new ushort[RampSize];
            return Native.GetDeviceGammaRamp(hdc, ramp) ? ramp : null;
        }
        finally { Native.DeleteDC(hdc); }
    }

    static bool SetRamp(string device, ushort[] ramp)
    {
        IntPtr hdc = Native.CreateDC(null, device, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try { return Native.SetDeviceGammaRamp(hdc, ramp); }
        finally { Native.DeleteDC(hdc); }
    }
}

/// %APPDATA%\Brilho\gamma-journal.json: a curva original de cada tela que está
/// escurecida agora. Existe só enquanto há algo a desfazer.
sealed class GammaJournal
{
    public sealed class Entry
    {
        public string Device { get; set; }
        public string Ramp64 { get; set; }

        public ushort[] Ramp
        {
            get
            {
                var bytes = Convert.FromBase64String(Ramp64);
                var ramp = new ushort[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
                return ramp;
            }
        }
    }

    Dictionary<string, Entry> entries = new();
    static string FilePath => Path.Combine(Log.Dir, "gamma-journal.json");

    public IEnumerable<(string, Entry)> Entries => entries.Select(kv => (kv.Key, kv.Value)).ToList();

    public static GammaJournal Load()
    {
        var journal = new GammaJournal();
        try
        {
            if (File.Exists(FilePath))
            {
                journal.entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
                journal.entries = journal.entries
                    .Where(kv => kv.Value?.Ramp64 != null && kv.Value.Ramp.Length == 768)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (journal.entries.Count > 0) Log.Write($"diário de gama encontrado com {journal.entries.Count} tela(s)");
            }
        }
        catch (Exception e) { Log.Write("diário de gama ilegível: " + e.Message); }
        return journal;
    }

    public void Add(string fingerprint, string device, ushort[] ramp)
    {
        var bytes = new byte[ramp.Length * 2];
        Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);
        entries[fingerprint] = new Entry { Device = device, Ramp64 = Convert.ToBase64String(bytes) };
        Save();
    }

    public void Remove(string fingerprint)
    {
        if (entries.Remove(fingerprint)) Save();
    }

    void Save()
    {
        try
        {
            if (entries.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Log.Dir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, FilePath, true);
        }
        catch (Exception e) { Log.Write("diário de gama não gravado: " + e.Message); }
    }
}
