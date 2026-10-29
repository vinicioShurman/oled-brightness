using System.Globalization;
using System.Text;

namespace Brilho;

/// As rotas do Vorssaint, traduzidas: `.system` → Wmi, `.ddc` → Ddc,
/// `.software` → Gamma, mais Overlay para quando a curva de gama não vale (HDR).
enum Route { Wmi, Ddc, Gamma, Overlay }

sealed class DisplayState
{
    public DisplayInfo Info;
    public Route Method;
    public MethodChoice Choice;
    public bool Extended;
    public bool Readable;
    /// 0...1, a posição do controle.
    public double Level;
    public string Note;
    public DateTime LevelKnownAt;

    public string Fingerprint => Info.Fingerprint;
    public bool IsHardware => Method is Route.Wmi or Route.Ddc;

    /// Quem escurece a imagem: a própria rota de software, ou, num monitor com
    /// DDC/WMI e escurecimento extra, a curva de gama (overlay se HDR).
    public Route PictureMethod => !IsHardware ? Method
        : Choice == MethodChoice.Overlay || Info.AdvancedColor ? Route.Overlay : Route.Gamma;

    public string MethodLabel => Method switch
    {
        Route.Wmi => "Interno (WMI)",
        Route.Ddc => Readable ? "DDC/CI" : "DDC/CI (sem leitura)",
        Route.Gamma => "Gamma",
        _ => "Overlay",
    } + (Extended ? " + extra" : "");
}

sealed class BrightnessService
{
    /// O quarto de baixo do controle, no escurecimento extra.
    public const double ExtendedRange = 0.25;
    /// Uma tela que acabou de aparecer (ou o app que acabou de abrir) nunca
    /// volta mais escura que isto: religar o cabo é o último recurso de quem
    /// está diante de uma tela preta, e tem que dar em algo visível (issue #301).
    public const double ReconnectFloor = 0.25;
    /// Por quanto tempo um nível lembrado vale sem perguntar ao monitor, que
    /// tem botões próprios (issue #370).
    const double TrustWindowSeconds = 3;

    readonly AppSettings settings;
    readonly Action<Action> ui;
    readonly IWorker worker;
    readonly bool commandLine;
    readonly GammaController gamma = new();
    readonly Dictionary<string, OverlaySet> overlays = new();
    /// Só a thread de trabalho toca aqui.
    readonly Dictionary<string, IHardwareRoute> hardware = new();
    readonly object pendingLock = new();
    /// Um arrasto do controle vira uma escrita só, do valor mais novo.
    readonly Dictionary<string, double> pending = new();
    bool drainScheduled;
    readonly Dictionary<string, double> session = new();
    readonly Dictionary<string, DateTime> userTouchedAt = new();
    HashSet<string> known = new();
    bool settingsDirty;
    int generation;

    public List<DisplayState> Displays { get; private set; } = new();
    public event Action Changed;
    public event Action<DisplayState> LevelChanged;
    public event Action UserAdjusted;

    public BrightnessService(AppSettings settings, Action<Action> ui, IWorker worker, bool commandLine)
    {
        this.settings = settings;
        this.ui = ui;
        this.worker = worker;
        this.commandLine = commandLine;
    }

    // ---- Descoberta

    /// Relê todas as telas: na abertura, quando a topologia muda e ao acordar
    /// (os canais DDC de antes do repouso não levam mais a lugar nenhum).
    public void Rebuild()
    {
        var choices = settings.Monitors.ToDictionary(kv => kv.Key, kv => kv.Value.Choice);
        int current = ++generation;
        worker.Post(() =>
        {
            var infos = DisplayEnumerator.Enumerate();
            foreach (var route in hardware.Values) route.Dispose();
            hardware.Clear();
            var probed = new List<Probed>();
            foreach (var info in infos)
            {
                choices.TryGetValue(info.Fingerprint, out var choice);
                var p = Probe(info, choice);
                if (p.Hardware != null) hardware[info.Fingerprint] = p.Hardware;
                Log.Write($"tela {info.GdiName} '{info.FriendlyName}' [{info.ConnectionName}{(info.AdvancedColor ? ", HDR" : "")}] rota {p.Method}"
                    + (p.HardwareLevel is double h ? $" nível {h:P0}" : "") + (p.Note != null ? $" — {p.Note}" : ""));
                probed.Add(p);
            }
            ui(() => { if (current == generation) Publish(probed); });
        });
    }

    sealed class Probed
    {
        public DisplayInfo Info;
        public Route Method;
        public bool Readable;
        public double? HardwareLevel;
        public string Note;
        public IHardwareRoute Hardware;
    }

    static Probed Probe(DisplayInfo info, MethodChoice choice)
    {
        var p = new Probed { Info = info };
        var software = info.AdvancedColor ? Route.Overlay : Route.Gamma;
        switch (choice)
        {
            case MethodChoice.Gamma:
                p.Method = Route.Gamma;
                if (info.AdvancedColor) p.Note = "Com HDR ligado o Windows ignora a curva de gama; use Overlay";
                return p;
            case MethodChoice.Overlay:
                p.Method = Route.Overlay;
                return p;
        }
        string note;
        IHardwareRoute route = choice == MethodChoice.Auto && info.Internal
            ? WmiRoute.Open(out note)
            : DdcRoute.Open(info.HMonitor, choice == MethodChoice.Ddc, out note);
        p.Note = note;
        if (route == null)
        {
            // Sem canal de hardware (TV, adaptador HDMI): escurece a imagem.
            p.Method = software;
            return p;
        }
        p.Hardware = route;
        p.Method = route is WmiRoute ? Route.Wmi : Route.Ddc;
        p.Readable = route.Readable;
        p.HardwareLevel = route.InitialLevel;
        return p;
    }

    void Publish(List<Probed> probed)
    {
        var states = new List<DisplayState>();
        var seen = new HashSet<string>();
        foreach (var p in probed)
        {
            string fp = p.Info.Fingerprint;
            seen.Add(fp);
            var saved = settings.For(fp);
            saved.Name = p.Info.FriendlyName;
            var state = new DisplayState
            {
                Info = p.Info, Method = p.Method, Choice = saved.Choice,
                Readable = p.Readable, Note = p.Note,
            };
            state.Extended = saved.Extended && state.IsHardware;
            double? remembered = session.TryGetValue(fp, out var r) ? r : saved.Level;

            if (state.IsHardware)
            {
                if (p.HardwareLevel is double h)
                {
                    state.Level = state.Extended ? ExtendedLevel(h, remembered, PictureDimmed(fp)) : h;
                    state.LevelKnownAt = DateTime.UtcNow;
                }
                else state.Level = remembered ?? 0.5; // só escrita: começa do último valor aplicado
            }
            else if (commandLine) state.Level = state.Method == Route.Gamma ? gamma.EstimateLevel(p.Info) : remembered ?? 1;
            else state.Level = remembered ?? 1;

            bool returning = !known.Contains(fp);
            if (!commandLine && returning && (!state.IsHardware || state.Extended) && state.Level < ReconnectFloor)
            {
                Log.Write($"{p.Info.FriendlyName} voltou escurecida a {state.Level:P0}; subindo para {ReconnectFloor:P0}");
                state.Level = ReconnectFloor;
            }
            states.Add(state);
        }

        if (!commandLine)
        {
            // Uma tela que mudou de rota devolve a imagem da rota anterior.
            foreach (var fp in overlays.Keys.ToList())
            {
                if (states.Any(s => s.Fingerprint == fp && s.PictureMethod == Route.Overlay)) continue;
                overlays[fp].Dispose();
                overlays.Remove(fp);
            }
            foreach (var s in states)
                if (s.PictureMethod != Route.Gamma && gamma.IsDimmed(s.Fingerprint)) gamma.Apply(s.Info, 1);
        }

        Displays = states;
        known = seen;
        if (!commandLine)
        {
            foreach (var s in states)
            {
                session[s.Fingerprint] = s.Level;
                ApplyPicture(s);
            }
        }
        Changed?.Invoke();
    }

    // ---- Níveis

    /// Muda o nível de uma tela. A imagem (gama/overlay) muda na hora; o
    /// hardware vai para a fila, e um arrasto vira uma escrita do valor final.
    /// `byUser` separa a pessoa do agendador noturno, que precisa saber quando
    /// foi ultrapassado.
    public bool SetLevel(DisplayState state, double level, bool byUser = true)
    {
        if (byUser) UserAdjusted?.Invoke();
        level = Math.Clamp(level, 0, 1);
        state.Level = level;
        session[state.Fingerprint] = level;
        userTouchedAt[state.Fingerprint] = DateTime.UtcNow;
        settings.For(state.Fingerprint).Level = level;
        settingsDirty = true;
        bool ok = ApplyPicture(state);
        if (state.IsHardware)
        {
            QueueHardware(state.Fingerprint, state.Extended ? Components(level).Hardware : level);
            state.LevelKnownAt = DateTime.UtcNow;
        }
        LevelChanged?.Invoke(state);
        return ok;
    }

    /// Um passo de atalho. Depois de uma pausa pergunta ao monitor onde ele
    /// está antes de somar: partir de um valor velho foi o que jogou um monitor
    /// de 80% para quase nada no Vorssaint (issue #370).
    public void Step(DisplayState state, double delta, Action<DisplayState> done)
    {
        bool stale = state.IsHardware && state.Readable
            && (DateTime.UtcNow - state.LevelKnownAt).TotalSeconds > TrustWindowSeconds
            && !(state.Extended && state.Level < ExtendedRange);
        if (!stale)
        {
            SetLevel(state, state.Level + delta);
            done?.Invoke(state);
            return;
        }
        string fp = state.Fingerprint;
        worker.Post(() =>
        {
            double? read = hardware.TryGetValue(fp, out var route) ? route.Read() : null;
            ui(() =>
            {
                if (!Displays.Contains(state)) return;
                if (read is double h)
                    state.Level = state.Extended ? ExtendedLevel(h, state.Level, PictureDimmed(fp)) : h;
                SetLevel(state, state.Level + delta);
                done?.Invoke(state);
            });
        });
    }

    /// Ao abrir o painel: os controles acompanham mudanças feitas pelos botões
    /// do próprio monitor ou pelo Windows.
    public void RefreshHardware()
    {
        var targets = Displays.Where(s => s.IsHardware && s.Readable).ToList();
        if (targets.Count == 0) return;
        worker.Post(() =>
        {
            var reads = new Dictionary<DisplayState, double>();
            foreach (var s in targets)
                if (hardware.TryGetValue(s.Fingerprint, out var route) && route.Read() is double h) reads[s] = h;
            ui(() =>
            {
                foreach (var (s, h) in reads)
                {
                    if (!Displays.Contains(s)) continue;
                    if (userTouchedAt.TryGetValue(s.Fingerprint, out var touched) && (DateTime.UtcNow - touched).TotalSeconds < 2) continue;
                    double level = s.Extended ? ExtendedLevel(h, s.Level, PictureDimmed(s.Fingerprint)) : h;
                    s.LevelKnownAt = DateTime.UtcNow;
                    if (Math.Abs(level - s.Level) < 0.005) continue;
                    s.Level = level;
                    session[s.Fingerprint] = level;
                    LevelChanged?.Invoke(s);
                }
            });
        });
    }

    /// O "pânico": tira todo escurecimento por software. O brilho de hardware
    /// fica onde a pessoa deixou.
    public void RemoveSoftwareDimming()
    {
        foreach (var s in Displays)
        {
            if (!s.IsHardware) SetLevel(s, 1);
            else if (s.Extended && s.Level < ExtendedRange) SetLevel(s, ExtendedRange);
        }
    }

    public void SetChoice(DisplayState state, MethodChoice choice)
    {
        settings.For(state.Fingerprint).Choice = choice;
        settings.Save();
        Rebuild();
    }

    public void SetExtended(DisplayState state, bool extended)
    {
        settings.For(state.Fingerprint).Extended = extended;
        // O nível salvo descrevia o outro modo.
        settings.For(state.Fingerprint).Level = null;
        session.Remove(state.Fingerprint);
        settings.Save();
        Rebuild();
    }

    public DisplayState DisplayAt(Point point) => Displays.FirstOrDefault(s => s.Info.Bounds.Contains(point));

    public void MarkDirty() => settingsDirty = true;

    /// A cada segundo, na thread da interface.
    public void Tick()
    {
        gamma.Watchdog(Displays.Select(s => s.Info));
        foreach (var overlay in overlays.Values) overlay.Raise();
        if (settingsDirty)
        {
            settingsDirty = false;
            settings.Save();
        }
    }

    /// Ao sair: deixar uma tela escura sem nada rodando para explicar é a
    /// diferença entre fechar um app e "minha tela quebrou".
    public void RestoreForExit()
    {
        gamma.RestoreAll(Displays.Select(s => s.Info));
        foreach (var overlay in overlays.Values) overlay.Dispose();
        overlays.Clear();
        settings.Save();
    }

    public static bool CanApplyFromCommandLine(DisplayState s) =>
        s.PictureMethod != Route.Overlay || (s.IsHardware && !s.Extended);

    public List<DisplayState> Resolve(string target)
    {
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target.Equals("todas", StringComparison.OrdinalIgnoreCase))
            return Displays.ToList();
        return int.TryParse(target, out int n) && n >= 1 && n <= Displays.Count ? new() { Displays[n - 1] } : new();
    }

    public string FormatStatus()
    {
        var sb = new StringBuilder();
        if (Displays.Count == 0) sb.AppendLine("Nenhuma tela encontrada.");
        for (int i = 0; i < Displays.Count; i++)
        {
            var s = Displays[i];
            sb.AppendLine($"{i + 1}. {s.Info.FriendlyName}  [{s.Info.GdiName}, {s.Info.ConnectionName}{(s.Info.AdvancedColor ? ", HDR" : "")}{(s.Info.Primary ? ", principal" : "")}]");
            sb.AppendLine($"   rota: {s.MethodLabel}   nível: {Math.Round(s.Level * 100).ToString(CultureInfo.InvariantCulture)}%");
            if (s.Note != null) sb.AppendLine($"   nota: {s.Note}");
        }
        return sb.ToString();
    }

    // ---- Aplicação

    bool ApplyPicture(DisplayState s)
    {
        double picture = !s.IsHardware ? s.Level : s.Extended ? Components(s.Level).Picture : 1;
        string fp = s.Fingerprint;
        if (s.PictureMethod == Route.Gamma)
        {
            CloseOverlay(fp);
            bool ok = gamma.Apply(s.Info, picture);
            if (!ok) Log.Write($"curva de gama recusada para {s.Info.FriendlyName}");
            return ok;
        }
        if (gamma.IsDimmed(fp)) gamma.Apply(s.Info, 1);
        if (commandLine) return picture >= 0.999; // a camada morreria com este processo
        if (!overlays.TryGetValue(fp, out var overlay))
        {
            if (picture >= 0.999) return true;
            overlays[fp] = overlay = new OverlaySet();
        }
        overlay.SetDim(picture, s.Info.AdvancedColor, s.Info.Bounds);
        return true;
    }

    void CloseOverlay(string fp)
    {
        if (!overlays.Remove(fp, out var overlay)) return;
        overlay.Dispose();
    }

    bool PictureDimmed(string fp) => gamma.IsDimmed(fp) || (overlays.TryGetValue(fp, out var o) && o.Visible);

    void QueueHardware(string fp, double value)
    {
        bool post;
        lock (pendingLock)
        {
            pending[fp] = value;
            post = !drainScheduled;
            drainScheduled = true;
        }
        if (post) worker.Post(DrainHardware);
    }

    void DrainHardware()
    {
        while (true)
        {
            string fp;
            double value;
            lock (pendingLock)
            {
                if (pending.Count == 0)
                {
                    drainScheduled = false;
                    return;
                }
                var next = pending.First();
                pending.Remove(next.Key);
                (fp, value) = (next.Key, next.Value);
            }
            if (!hardware.TryGetValue(fp, out var route)) continue;
            bool ok = route.Write(value);
            if (!ok) Log.Write($"escrita de hardware falhou ({value:P0}) em {fp}");
        }
    }

    // ---- Escurecimento extra (abaixo do mínimo do monitor)

    /// Controle 0...0,25 → hardware no mínimo, imagem 0...1;
    /// controle 0,25...1 → hardware 0...1, imagem inteira.
    public static (double Hardware, double Picture) Components(double level)
    {
        level = Math.Clamp(level, 0, 1);
        return level < ExtendedRange
            ? (0, level / ExtendedRange)
            : ((level - ExtendedRange) / (1 - ExtendedRange), 1);
    }

    /// O inverso, a partir do que o monitor responde. No mínimo de hardware só
    /// o app sabe quanto a imagem está escurecida, então vale o lembrado.
    static double ExtendedLevel(double hardware, double? remembered, bool pictureDimmed)
    {
        double physical = ExtendedRange + Math.Clamp(hardware, 0, 1) * (1 - ExtendedRange);
        if (pictureDimmed && hardware <= 0.001 && remembered is double r && r < ExtendedRange) return Math.Max(r, 0);
        return physical;
    }
}
