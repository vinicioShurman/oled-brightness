namespace Brilho;

/// O painel que abre pelo ícone da bandeja: um controle por tela.
sealed class Flyout : Form
{
    readonly BrightnessService service;
    readonly AppSettings settings;
    readonly NightScheduler night;
    readonly Action exit;
    readonly Dictionary<string, Row> rows = new();
    Label nightStatus;
    LinkLabel nightPause;
    float scale;
    Font titleFont, nameFont, bodyFont, smallFont;
    bool menuOpen;

    sealed class Row
    {
        public DisplayState State;
        public FlatSlider Slider;
        public Label Percent;
    }

    public Flyout(BrightnessService service, AppSettings settings, NightScheduler night, Action exit)
    {
        this.service = service;
        this.settings = settings;
        this.night = night;
        this.exit = exit;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        TopMost = true;
        KeyPreview = true;
        Text = "Brilho";
        service.Changed += () => { if (Visible) Build(); };
        service.LevelChanged += OnLevelChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return; // a escala é calculada aqui, por monitor
        base.WndProc(ref m);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!menuOpen) Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    int S(float v) => (int)Math.Round(v * scale);

    public void ShowAt(Point anchor)
    {
        this.anchor = anchor;
        SetScale(Dpi.ScaleAt(anchor));
        Build();
        Show();
        Place();
        Native.SetForegroundWindow(Handle);
        Activate();
        service.RefreshHardware();
        RefreshNight();
    }

    Point anchor;

    /// Encosta o painel no canto da área de trabalho perto de onde foi aberto.
    /// Chamado de novo sempre que o conteúdo muda de altura, senão o painel
    /// cresce para baixo da barra de tarefas (ligar "Escurecer à noite").
    void Place()
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        int margin = S(12);
        int x = Math.Clamp(anchor.X - Width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        int y = anchor.Y > area.Top + area.Height / 2 ? area.Bottom - Height - margin : area.Top + margin;
        y = Math.Max(area.Top + margin, Math.Min(y, area.Bottom - Height - margin));
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, Width, Height, Native.SWP_NOACTIVATE);
    }

    /// `Brilho.exe --preview arquivo.png`: o painel desenhado sem abrir janela.
    public Bitmap Snapshot(float scale)
    {
        SetScale(scale);
        Build();
        Location = new Point(-30000, -30000);
        Show();
        Application.DoEvents();
        var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        return bitmap;
    }

    /// Mantém o painel acima da camada escura. Com uma lista de horários ou o
    /// menu ⋯ aberto não mexe: subir o painel cobriria a lista, que é outra janela.
    public void KeepAbove()
    {
        if (!Visible || menuOpen || Controls.OfType<ComboBox>().Any(c => c.DroppedDown)) return;
        Native.KeepOnTop(Handle);
    }

    void SetScale(float value)
    {
        if (titleFont != null && Math.Abs(value - scale) < 0.01f) return;
        scale = value;
        foreach (var font in new[] { titleFont, nameFont, bodyFont, smallFont }) font?.Dispose();
        titleFont = new Font("Segoe UI Semibold", 18 * scale, GraphicsUnit.Pixel);
        nameFont = new Font("Segoe UI Semibold", 14 * scale, GraphicsUnit.Pixel);
        bodyFont = new Font("Segoe UI", 13 * scale, GraphicsUnit.Pixel);
        smallFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
    }

    void Build()
    {
        SuspendLayout();
        foreach (var control in Controls.Cast<Control>().ToList()) control.Dispose();
        Controls.Clear();
        rows.Clear();

        int width = S(390), pad = S(18), inner = width - pad * 2, y = pad;
        Controls.Add(new Label { Text = "Brilho", Font = titleFont, ForeColor = Theme.Fg, AutoSize = false, Bounds = new Rectangle(pad, y, inner, S(28)) });
        y += S(38);

        if (service.Displays.Count == 0)
        {
            var empty = Text_(pad, y, inner, "Procurando telas…", bodyFont, Theme.Dim);
            y += empty.Height + S(12);
        }
        foreach (var state in service.Displays) y = AddDisplay(state, pad, y, inner) + S(14);

        Controls.Add(new Panel { BackColor = Theme.Line, Bounds = new Rectangle(pad, y, inner, Math.Max(1, S(1))) });
        y += S(14);
        y = AddNight(pad, y, inner);
        Controls.Add(new Panel { BackColor = Theme.Line, Bounds = new Rectangle(pad, y, inner, Math.Max(1, S(1))) });
        y += S(14);

        var reset = Button_("Remover escurecimento", pad, y, S(200), S(32));
        reset.Click += (_, _) => service.RemoveSoftwareDimming();
        var step = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Card, ForeColor = Theme.Fg, Font = bodyFont,
            Bounds = new Rectangle(pad + inner - S(120), y + S(2), S(120), S(28)),
        };
        step.Items.AddRange(new object[] { "Passo 2%", "Passo 5%", "Passo 10%" });
        step.SelectedIndex = settings.Step < 0.03 ? 0 : settings.Step > 0.07 ? 2 : 1;
        step.SelectedIndexChanged += (_, _) =>
        {
            settings.Step = new[] { 0.02, 0.05, 0.10 }[step.SelectedIndex];
            service.MarkDirty();
        };
        Controls.Add(step);
        y += S(44);

        var startup = new CheckBox
        {
            Text = "Iniciar com o Windows", Checked = Startup.Enabled, Font = bodyFont, ForeColor = Theme.Fg,
            FlatStyle = FlatStyle.Flat, AutoSize = false, Bounds = new Rectangle(pad, y, inner, S(26)),
        };
        startup.CheckedChanged += (_, _) => Startup.Enabled = startup.Checked;
        Controls.Add(startup);
        y += S(34);

        var hint = Text_(pad, y, inner,
            "Ctrl+Alt+PgUp / PgDn: tela sob o cursor\nCtrl+Alt+Home: remover escurecimento", smallFont, Theme.Dim);
        y += hint.Height + S(10);

        var quit = new LinkLabel
        {
            Text = "Sair do Brilho", Font = bodyFont, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Fg,
            LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = true, Location = new Point(pad, y),
        };
        quit.LinkClicked += (_, _) => exit();
        Controls.Add(quit);
        y += S(24);

        ClientSize = new Size(width, y + pad);
        ResumeLayout();
        if (Visible) Place();
    }

    int AddDisplay(DisplayState state, int x, int y, int inner)
    {
        int more = S(30), badgeWidth = S(150);
        string name = state.Info.FriendlyName + (state.Info.Primary && service.Displays.Count > 1 ? " · principal" : "");
        Controls.Add(new Label
        {
            Text = name, Font = nameFont, ForeColor = Theme.Fg, AutoSize = false, AutoEllipsis = true,
            Bounds = new Rectangle(x, y, inner - more - badgeWidth, S(24)),
        });
        Controls.Add(new Label
        {
            Text = state.MethodLabel, Font = smallFont, AutoSize = false, TextAlign = ContentAlignment.MiddleRight,
            ForeColor = state.IsHardware ? Theme.Accent : Theme.Software,
            Bounds = new Rectangle(x + inner - more - badgeWidth - S(6), y, badgeWidth, S(24)),
        });
        var menuButton = Button_("⋯", x + inner - more, y - S(2), more, S(26));
        menuButton.Click += (_, _) => ShowMenu(state, menuButton);
        y += S(28);

        var slider = new FlatSlider { BackColor = Theme.Bg, Bounds = new Rectangle(x - S(2), y, inner - S(56), S(30)), Value = state.Level };
        var percent = new Label
        {
            Text = Percent(state.Level), Font = bodyFont, ForeColor = Theme.Fg, AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(x + inner - S(54), y, S(54), S(30)),
        };
        slider.UserChanged += v => service.SetLevel(state, v);
        Controls.Add(slider);
        Controls.Add(percent);
        rows[state.Fingerprint] = new Row { State = state, Slider = slider, Percent = percent };
        y += S(32);

        string note = Explain(state);
        if (note != null)
        {
            var label = Text_(x, y, inner, note, smallFont, Theme.Dim);
            y += label.Height;
        }
        return y;
    }

    int AddNight(int x, int y, int inner)
    {
        var settingsNight = night.Settings;
        nightStatus = null;
        nightPause = null;
        Controls.Add(new Label
        {
            Text = "Escurecer à noite", Font = nameFont, ForeColor = Theme.Fg, AutoSize = false,
            Bounds = new Rectangle(x, y, inner - S(50), S(24)),
        });
        var toggle = new ToggleSwitch { Checked = settingsNight.Enabled, BackColor = Theme.Bg, Bounds = new Rectangle(x + inner - S(42), y + S(2), S(42), S(21)) };
        toggle.Toggled += on =>
        {
            night.SetEnabled(on);
            // A seção muda de tamanho; reconstruir de dentro do próprio clique
            // descartaria a chave no meio do evento.
            BeginInvoke(Build);
        };
        Controls.Add(toggle);
        y += S(30);

        if (!settingsNight.Enabled)
        {
            var hint = Text_(x, y, inner, "Escurece todas as telas devagar a partir de um horário e devolve o brilho de manhã.", smallFont, Theme.Dim);
            return y + hint.Height + S(10);
        }

        y = TimeRow(x, y, inner, "Começa a escurecer às", settingsNight.Start, v => settingsNight.Start = v);
        y = TimeRow(x, y, inner, "Chega ao mínimo às", settingsNight.End, v => settingsNight.End = v);

        int labelWidth = S(110);
        Controls.Add(new Label
        {
            Text = "Escurecer até", Font = smallFont, ForeColor = Theme.Dim, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(x, y, labelWidth, S(30)),
        });
        var target = new FlatSlider { BackColor = Theme.Bg, Value = settingsNight.Target, Bounds = new Rectangle(x + labelWidth, y, inner - labelWidth - S(52), S(30)) };
        var targetPercent = new Label
        {
            Text = Percent(settingsNight.Target), Font = bodyFont, ForeColor = Theme.Fg, AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(x + inner - S(52), y, S(52), S(30)),
        };
        target.UserChanged += v =>
        {
            settingsNight.Target = Math.Round(v, 2);
            targetPercent.Text = Percent(settingsNight.Target);
            service.MarkDirty();
            RefreshNight();
        };
        Controls.Add(target);
        Controls.Add(targetPercent);
        y += S(36);

        y = TimeRow(x, y, inner, "Volta ao normal às", settingsNight.Wake, v => settingsNight.Wake = v);

        nightStatus = new Label
        {
            Font = smallFont, ForeColor = Color.FromArgb(208, 208, 208), BackColor = Theme.Card, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, S(8), 0),
            Bounds = new Rectangle(x, y + S(4), inner, S(30)),
        };
        Controls.Add(nightStatus);
        y += S(42);

        nightPause = new LinkLabel
        {
            Font = smallFont, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Fg,
            LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = true, Location = new Point(x, y),
        };
        nightPause.LinkClicked += (_, _) =>
        {
            night.TogglePause(DateTime.Now);
            RefreshNight();
        };
        Controls.Add(nightPause);
        Controls.Add(new Label
        {
            Text = "Todas as telas", Font = smallFont, ForeColor = Theme.Dim, AutoSize = false,
            TextAlign = ContentAlignment.TopRight, Bounds = new Rectangle(x + inner - S(140), y, S(140), S(20)),
        });
        y += S(28);
        RefreshNight();
        return y;
    }

    int TimeRow(int x, int y, int inner, string label, string value, Action<string> set)
    {
        Controls.Add(new Label
        {
            Text = label, Font = smallFont, ForeColor = Theme.Dim, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(x, y, inner - S(96), S(30)),
        });
        var times = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card,
            ForeColor = Theme.Fg, Font = bodyFont, MaxDropDownItems = 12,
            Bounds = new Rectangle(x + inner - S(90), y + S(1), S(90), S(28)),
        };
        var options = Enumerable.Range(0, 96).Select(i => NightScheduler.FormatMinutes(i * 15)).ToList();
        string current = NightScheduler.FormatMinutes(NightScheduler.ParseMinutes(value));
        if (!options.Contains(current)) options.Add(current);
        options.Sort(StringComparer.Ordinal);
        times.Items.AddRange(options.Cast<object>().ToArray());
        times.SelectedItem = current;
        times.SelectedIndexChanged += (_, _) =>
        {
            set((string)times.SelectedItem);
            service.MarkDirty();
            RefreshNight();
        };
        Controls.Add(times);
        return y + S(34);
    }

    /// A cada segundo enquanto o painel está aberto.
    public void RefreshNight()
    {
        if (nightStatus == null || nightStatus.IsDisposed) return;
        var now = DateTime.Now;
        nightStatus.Text = night.Status(now) ?? "";
        nightPause.Text = night.IsPaused(now) ? "Voltar a escurecer hoje" : "Pausar até amanhã";
    }

    static string Explain(DisplayState state)
    {
        string how = state.Method switch
        {
            Route.Gamma => "Escurece a imagem pela curva de gama da placa de vídeo.",
            Route.Overlay => "Camada escura por cima da tela (funciona com HDR; o cursor não escurece).",
            _ when state.Extended => "Abaixo de 25% o monitor fica no mínimo e a imagem escurece por software.",
            _ => null,
        };
        if (state.Note == null) return how;
        return how == null ? state.Note : state.Note + ". " + how;
    }

    void OnLevelChanged(DisplayState state)
    {
        if (!rows.TryGetValue(state.Fingerprint, out var row) || row.State != state) return;
        if (!row.Slider.Dragging) row.Slider.Value = state.Level;
        row.Percent.Text = Percent(state.Level);
    }

    void ShowMenu(DisplayState state, Control anchor)
    {
        var menu = new ContextMenuStrip { Font = bodyFont, ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new ToolStripLabel("Como controlar esta tela") { ForeColor = Color.Gray });
        void Choice(string text, MethodChoice choice)
        {
            var item = new ToolStripMenuItem(text) { Checked = state.Choice == choice };
            item.Click += (_, _) => service.SetChoice(state, choice);
            menu.Items.Add(item);
        }
        Choice("Automático", MethodChoice.Auto);
        Choice("DDC/CI — brilho real do monitor", MethodChoice.Ddc);
        Choice("Gamma — curva da placa de vídeo", MethodChoice.Gamma);
        Choice("Overlay — camada escura", MethodChoice.Overlay);
        menu.Items.Add(new ToolStripSeparator());
        var extended = new ToolStripMenuItem("Escurecer abaixo do mínimo do monitor")
        {
            Checked = state.Extended, Enabled = state.IsHardware,
            ToolTipText = state.IsHardware ? null : "Só para telas controladas por DDC/CI ou WMI",
        };
        extended.Click += (_, _) => service.SetExtended(state, !state.Extended);
        menu.Items.Add(extended);
        menu.Opened += (_, _) => menuOpen = true;
        menu.Closed += (_, e) =>
        {
            menuOpen = false;
            if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange) Hide();
            else if (Visible) Activate();
            BeginInvoke(menu.Dispose);
        };
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    Label Text_(int x, int y, int width, string text, Font font, Color color)
    {
        var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
        var label = new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, Bounds = new Rectangle(x, y, width, size.Height + S(2)) };
        Controls.Add(label);
        return label;
    }

    Button Button_(string text, int x, int y, int width, int height)
    {
        var button = new Button
        {
            Text = text, Font = bodyFont, ForeColor = Theme.Fg, BackColor = Theme.Card,
            FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(x, y, width, height), Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.FlatAppearance.BorderColor = Theme.Line;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 58, 58);
        Controls.Add(button);
        return button;
    }

    static string Percent(double level) => $"{Math.Round(level * 100)}%";
}
