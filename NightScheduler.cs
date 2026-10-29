using System.Globalization;

namespace Brilho;

/// "Escurecer à noite": as configurações, salvas junto com o resto.
sealed class NightSettings
{
    public bool Enabled { get; set; }
    public string Start { get; set; } = "22:00";
    public string End { get; set; } = "23:30";
    public string Wake { get; set; } = "07:00";
    /// 0...1, o mínimo da noite.
    public double Target { get; set; } = 0.30;
    /// A noite (pela data em que começa) que não deve escurecer.
    public string PausedCycle { get; set; }
    /// A noite em que a pessoa mexeu no brilho: o agendador para de escurecer,
    /// mas de manhã ainda devolve o brilho do dia.
    public string ManualCycle { get; set; }
    /// O brilho de cada tela antes de a noite começar, para onde ela volta de
    /// manhã. Fica no disco para sobreviver a um desligamento no meio da noite.
    public Dictionary<string, double> DayLevels { get; set; } = new();
    /// Onde cada tela estava quando a manhã começou numa noite com ajuste
    /// manual: o clarear parte daí.
    public Dictionary<string, double> WakeFrom { get; set; } = new();
}

enum NightPhase { Day, Dimming, Hold, Waking }

readonly record struct NightCycle(NightPhase Phase, DateTime Start, DateTime End, DateTime Wake, DateTime WakeEnd, double Progress)
{
    public string Key => Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string NextKey => Start.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// Escurece todas as telas devagar entre dois horários e devolve de manhã.
///
/// Tudo é calculado a partir do relógio a cada segundo, nunca contado: um PC
/// ligado ou acordado às 23:00 cai direto no ponto certo da curva, e passar da
/// meia-noite não é caso especial. Quem manda no fim é sempre a pessoa: mexer
/// no brilho durante a noite pausa o agendador até a manhã.
sealed class NightScheduler
{
    public static readonly TimeSpan WakeFade = TimeSpan.FromMinutes(15);
    /// Um salto (app aberto no meio da noite, horário mudado) vira uma
    /// transição de uns 20 s em vez de um corte.
    const double MaxStepPerTick = 0.04;
    /// Cerca de um degrau da curva de gama: abaixo disso a mudança não aparece,
    /// e um monitor DDC não precisa receber um comando por segundo.
    const double MinChange = 0.004;

    readonly NightSettings night;
    readonly BrightnessService service;
    readonly Action markDirty;

    public NightScheduler(NightSettings night, BrightnessService service, Action markDirty)
    {
        this.night = night;
        this.service = service;
        this.markDirty = markDirty;
    }

    public NightSettings Settings => night;

    // ---- Cálculo puro (testado por `Brilho.exe --simular-noite`)

    public static int ParseMinutes(string hhmm)
    {
        var parts = (hhmm ?? "").Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m)
            ? Math.Clamp(h, 0, 23) * 60 + Math.Clamp(m, 0, 59) : 0;
    }

    public static string FormatMinutes(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    static int Mod(int minutes) => ((minutes % 1440) + 1440) % 1440;

    /// O mínimo tem que chegar antes (ou na hora) da volta ao normal.
    public static bool IsValid(NightSettings s)
    {
        int start = ParseMinutes(s.Start);
        int toWake = Mod(ParseMinutes(s.Wake) - start);
        return toWake != 0 && Mod(ParseMinutes(s.End) - start) <= toWake;
    }

    public static NightCycle Evaluate(NightSettings s, DateTime now)
    {
        int start = ParseMinutes(s.Start);
        int toEnd = Mod(ParseMinutes(s.End) - start);
        int toWake = Mod(ParseMinutes(s.Wake) - start);
        if (toWake == 0) toWake = 1440;
        if (toEnd > toWake) toEnd = toWake;

        var startToday = now.Date.AddMinutes(start);
        var begin = now >= startToday ? startToday : startToday.AddDays(-1);
        var end = begin.AddMinutes(toEnd);
        var wake = begin.AddMinutes(toWake);
        var wakeEnd = wake + WakeFade;

        if (now < end)
            return new(NightPhase.Dimming, begin, end, wake, wakeEnd, Math.Clamp((now - begin).TotalSeconds / Math.Max(1, (end - begin).TotalSeconds), 0, 1));
        if (now < wake) return new(NightPhase.Hold, begin, end, wake, wakeEnd, 1);
        if (now < wakeEnd) return new(NightPhase.Waking, begin, end, wake, wakeEnd, Math.Clamp((now - wake).TotalSeconds / WakeFade.TotalSeconds, 0, 1));
        return new(NightPhase.Day, begin, end, wake, wakeEnd, 0);
    }

    /// Onde uma tela deveria estar agora. Uma tela que de dia já está abaixo do
    /// mínimo da noite nunca é clareada por ele.
    /// `wakeFrom`: numa noite com ajuste manual, o clarear parte de onde a
    /// pessoa deixou, não do mínimo.
    public static double Desired(NightCycle cycle, double day, double target, double? wakeFrom)
    {
        double floor = Math.Min(target, day);
        switch (cycle.Phase)
        {
            case NightPhase.Dimming:
                double p = cycle.Progress;
                return day + (floor - day) * (p * p * (3 - 2 * p)); // começa e termina suave
            case NightPhase.Hold:
                return floor;
            case NightPhase.Waking:
                double from = wakeFrom ?? floor;
                return from + (day - from) * cycle.Progress;
            default:
                return day;
        }
    }

    // ---- Ao vivo

    /// A cada segundo, na thread da interface.
    public void Tick(DateTime now)
    {
        if (!night.Enabled || !IsValid(night)) return;

        var cycle = Evaluate(night, now);
        if (night.PausedCycle != null && night.PausedCycle != cycle.Key && night.PausedCycle != cycle.NextKey)
        {
            night.PausedCycle = null; // a pausa era de uma noite que já passou
            markDirty();
        }
        if (night.PausedCycle == cycle.Key || cycle.Phase == NightPhase.Day)
        {
            // De dia (ou pausado): devolve o brilho guardado, se ainda houver.
            if (night.DayLevels.Count > 0 && Approach(s => night.DayLevels.TryGetValue(s.Fingerprint, out var d) ? d : null, final: true))
            {
                night.DayLevels.Clear();
                night.WakeFrom.Clear();
                night.ManualCycle = null;
                markDirty();
                Log.Write("noite: brilho do dia devolvido");
            }
            return;
        }

        foreach (var s in service.Displays)
        {
            if (night.DayLevels.ContainsKey(s.Fingerprint)) continue;
            night.DayLevels[s.Fingerprint] = s.Level;
            markDirty();
            Log.Write($"noite: brilho do dia de {s.Info.FriendlyName} guardado ({s.Level:P0})");
        }
        bool manual = night.ManualCycle == cycle.Key;
        if (manual && cycle.Phase != NightPhase.Waking) return;
        if (manual)
        {
            foreach (var s in service.Displays)
            {
                if (night.WakeFrom.ContainsKey(s.Fingerprint)) continue;
                night.WakeFrom[s.Fingerprint] = s.Level;
                markDirty();
            }
        }
        Approach(s => night.DayLevels.TryGetValue(s.Fingerprint, out var day)
                ? Desired(cycle, day, night.Target, manual && night.WakeFrom.TryGetValue(s.Fingerprint, out var from) ? from : null) : null,
            final: cycle.Phase == NightPhase.Hold);
    }

    /// Leva cada tela em direção ao nível pedido, no máximo um passo por
    /// segundo. Verdadeiro quando todas chegaram.
    bool Approach(Func<DisplayState, double?> desiredFor, bool final)
    {
        bool arrived = true;
        foreach (var s in service.Displays.ToList())
        {
            if (desiredFor(s) is not double want) continue;
            double diff = want - s.Level;
            if (Math.Abs(diff) < 0.0005) continue;
            if (!final && Math.Abs(diff) < MinChange) continue;
            if (Math.Abs(diff) > MaxStepPerTick) arrived = false;
            service.SetLevel(s, s.Level + Math.Clamp(diff, -MaxStepPerTick, MaxStepPerTick), byUser: false);
        }
        return arrived;
    }

    /// A pessoa mexeu no brilho (controle, atalho, "remover escurecimento").
    public void OnUserAdjusted()
    {
        if (!night.Enabled) return;
        var cycle = Evaluate(night, DateTime.Now);
        if (night.PausedCycle == cycle.Key) return;
        switch (cycle.Phase)
        {
            case NightPhase.Dimming or NightPhase.Hold:
                if (night.ManualCycle == cycle.Key) return;
                night.ManualCycle = cycle.Key;
                Log.Write("noite: ajuste manual; o agendador espera até a manhã");
                break;
            default:
                // De manhã quem mexe assume; não há mais o que devolver.
                if (night.DayLevels.Count == 0) return;
                night.DayLevels.Clear();
                night.WakeFrom.Clear();
                night.ManualCycle = null;
                break;
        }
        markDirty();
    }

    public bool IsPaused(DateTime now)
    {
        var cycle = Evaluate(night, now);
        return night.PausedCycle == cycle.Key || (cycle.Phase == NightPhase.Day && night.PausedCycle == cycle.NextKey);
    }

    /// "Pausar até amanhã": esta noite (ou a próxima, se ainda é dia) não
    /// escurece, e o brilho do dia volta já.
    public void TogglePause(DateTime now)
    {
        if (IsPaused(now)) night.PausedCycle = null;
        else
        {
            var cycle = Evaluate(night, now);
            night.PausedCycle = cycle.Phase == NightPhase.Day ? cycle.NextKey : cycle.Key;
            night.ManualCycle = null;
        }
        markDirty();
    }

    public void SetEnabled(bool enabled)
    {
        night.Enabled = enabled;
        night.PausedCycle = null;
        night.ManualCycle = null;
        if (!enabled)
        {
            // Desligar no meio da noite devolve o brilho do dia na hora.
            foreach (var s in service.Displays.ToList())
                if (night.DayLevels.TryGetValue(s.Fingerprint, out var day)) service.SetLevel(s, day, byUser: false);
            night.DayLevels.Clear();
            night.WakeFrom.Clear();
        }
        markDirty();
    }

    public string Status(DateTime now)
    {
        if (!night.Enabled) return null;
        if (!IsValid(night)) return "O mínimo precisa chegar antes da volta ao normal.";
        var cycle = Evaluate(night, now);
        string target = Percent(night.Target);
        string current = service.Displays.Count > 0 ? Percent(service.Displays[0].Level) : "—";
        if (IsPaused(now))
            return cycle.Phase == NightPhase.Day && night.PausedCycle == cycle.NextKey
                ? "Esta noite não vai escurecer."
                : $"Pausado · volta a escurecer amanhã às {night.Start}";
        bool manual = night.ManualCycle == cycle.Key;
        return cycle.Phase switch
        {
            NightPhase.Dimming when manual => $"Ajustado por você · volta ao normal às {night.Wake}",
            NightPhase.Hold when manual => $"Ajustado por você · volta ao normal às {night.Wake}",
            NightPhase.Dimming => $"Escurecendo · {current} → {target} às {night.End}",
            NightPhase.Hold => $"No mínimo ({target}) até {night.Wake}",
            NightPhase.Waking => $"Clareando · {current}",
            _ => $"Brilho normal · começa às {night.Start}",
        };
    }

    static string Percent(double level) => $"{Math.Round(level * 100)}%";
}
