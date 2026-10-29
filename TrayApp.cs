using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Brilho;

/// O app residente: ícone na bandeja, painel, atalhos globais e os eventos do
/// sistema (mudança de telas, repouso, logoff) que pedem uma releitura.
sealed class TrayApp : ApplicationContext
{
    const int HotkeyUp = 1, HotkeyDown = 2, HotkeyReset = 3;
    const uint VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_HOME = 0x24;

    readonly AppSettings settings = AppSettings.Load();
    readonly SerialWorker worker = new();
    readonly SynchronizationContext context;
    readonly BrightnessService service;
    readonly NightScheduler night;
    readonly NotifyIcon tray;
    readonly Flyout flyout;
    readonly Osd osd = new();
    readonly MessageWindow messages = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer settle = new() { Interval = 600 };
    bool cleanedUp;

    public TrayApp()
    {
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        context = SynchronizationContext.Current;
        service = new BrightnessService(settings, action => context.Post(_ => action(), null), worker, commandLine: false);
        night = new NightScheduler(settings.Night, service, service.MarkDirty);
        service.UserAdjusted += night.OnUserAdjusted;
        flyout = new Flyout(service, settings, night, Quit);

        tray = new NotifyIcon { Icon = AppIcon.Create(), Text = "Brilho", Visible = true, ContextMenuStrip = BuildMenu() };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

        messages.Hotkey += OnHotkey;
        messages.Command = OnCommand;
        RegisterHotkeys();

        tick.Tick += (_, _) =>
        {
            night.Tick(DateTime.Now);
            service.Tick();
            flyout.RefreshNight();
            // A camada escura é reerguida a cada segundo; o painel e o
            // indicador ficam acima dela, senão sumiriam numa tela a 0%.
            flyout.KeepAbove();
            osd.KeepAbove();
        };
        tick.Start();
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            settle.Interval = 600;
            service.Rebuild();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += (_, _) => Cleanup();

        service.Rebuild();
        Log.Write("Brilho iniciado");
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => ShowFlyout());
        menu.Items.Add("Remover escurecimento", null, (_, _) => service.RemoveSoftwareDimming());
        menu.Items.Add("Reler telas", null, (_, _) => service.Rebuild());
        menu.Items.Add("Abrir pasta de dados", null, (_, _) =>
        {
            Directory.CreateDirectory(Log.Dir);
            System.Diagnostics.Process.Start("explorer.exe", Log.Dir);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Quit());
        return menu;
    }

    void ToggleFlyout()
    {
        if (flyout.Visible) flyout.Hide();
        else ShowFlyout();
    }

    void ShowFlyout() => flyout.ShowAt(Cursor.Position);

    void RegisterHotkeys()
    {
        var failed = new List<string>();
        if (!Native.RegisterHotKey(messages.Handle, HotkeyUp, Native.MOD_CONTROL | Native.MOD_ALT, VK_PRIOR)) failed.Add("Ctrl+Alt+PgUp");
        if (!Native.RegisterHotKey(messages.Handle, HotkeyDown, Native.MOD_CONTROL | Native.MOD_ALT, VK_NEXT)) failed.Add("Ctrl+Alt+PgDn");
        if (!Native.RegisterHotKey(messages.Handle, HotkeyReset, Native.MOD_CONTROL | Native.MOD_ALT, VK_HOME)) failed.Add("Ctrl+Alt+Home");
        if (failed.Count == 0) return;
        Log.Write("atalhos já usados por outro app: " + string.Join(", ", failed));
        tray.ShowBalloonTip(5000, "Brilho", "Outro app já usa " + string.Join(", ", failed) + ".", ToolTipIcon.Warning);
    }

    /// Os atalhos agem na tela onde está o cursor, como a opção "teclas de
    /// brilho seguem o ponteiro" do Vorssaint.
    void OnHotkey(int id)
    {
        var state = service.DisplayAt(Cursor.Position) ?? service.Displays.FirstOrDefault();
        if (id == HotkeyReset)
        {
            service.RemoveSoftwareDimming();
            if (state != null) osd.ShowFor(state);
            return;
        }
        if (state == null) return;
        double delta = id == HotkeyUp ? settings.Step : -settings.Step;
        service.Step(state, delta, osd.ShowFor);
    }

    /// Pedidos de `Brilho.exe --set/--reset/--list` vindos de outro processo.
    bool OnCommand(string command)
    {
        var parts = command.Split('|');
        switch (parts[0])
        {
            case "show":
                ShowFlyout();
                return true;
            case "reset":
                service.RemoveSoftwareDimming();
                return true;
            case "quit":
                context.Post(_ => Quit(), null);
                return true;
            case "list":
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(Path.Combine(Log.Dir, "status.txt"), service.FormatStatus(), Encoding.UTF8);
                return true;
            case "set" when parts.Length == 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent):
                var targets = service.Resolve(parts[1]);
                foreach (var target in targets) service.SetLevel(target, percent / 100);
                if (targets.Count == 1) osd.ShowFor(targets[0]);
                return targets.Count > 0;
        }
        return false;
    }

    void OnDisplaySettingsChanged(object sender, EventArgs e) => context.Post(_ =>
    {
        settle.Stop();
        settle.Start();
    }, null);

    /// Ao acordar, os monitores ainda estão renegociando a conexão; quem não
    /// responde agora não está sem DDC, só atrasado. Espera 3 s, como o Vorssaint.
    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        context.Post(_ =>
        {
            settle.Stop();
            settle.Interval = 3000;
            settle.Start();
        }, null);
    }

    void OnSessionEnding(object sender, SessionEndingEventArgs e) => context.Send(_ => Cleanup(), null);

    void Quit()
    {
        Cleanup();
        ExitThread();
    }

    public void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        try
        {
            tick.Stop();
            service.RestoreForExit();
            Native.UnregisterHotKey(messages.Handle, HotkeyUp);
            Native.UnregisterHotKey(messages.Handle, HotkeyDown);
            Native.UnregisterHotKey(messages.Handle, HotkeyReset);
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            tray.Visible = false;
            tray.Dispose();
            messages.Dispose();
            worker.Dispose();
            Log.Write("Brilho encerrado; curvas de gama devolvidas");
        }
        catch (Exception e) { Log.Write("erro ao encerrar: " + e); }
    }
}

/// Janela invisível que recebe os atalhos globais e os comandos de outra
/// instância. Tem título fixo para `FindWindow` achá-la.
sealed class MessageWindow : NativeWindow, IDisposable
{
    public event Action<int> Hotkey;
    public Func<string, bool> Command;

    public MessageWindow() => CreateHandle(new CreateParams { Caption = Ipc.WindowTitle });

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            Hotkey?.Invoke((int)m.WParam);
            return;
        }
        if (m.Msg == Native.WM_COPYDATA)
        {
            var data = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(m.LParam);
            string text = Marshal.PtrToStringUni(data.lpData, data.cbData / 2).TrimEnd('\0');
            bool ok = false;
            try { ok = Command?.Invoke(text) ?? false; }
            catch (Exception e) { Log.Write("comando falhou: " + e.Message); }
            m.Result = ok ? new IntPtr(1) : IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

static class Ipc
{
    public const string WindowTitle = "Brilho.IPC.v1";

    public static bool IsRunning => Native.FindWindow(null, WindowTitle) != IntPtr.Zero;

    public static bool Send(string command)
    {
        IntPtr window = Native.FindWindow(null, WindowTitle);
        if (window == IntPtr.Zero) return false;
        var bytes = Encoding.Unicode.GetBytes(command + "\0");
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var data = new Native.COPYDATASTRUCT { dwData = new IntPtr(0x42524C48), cbData = bytes.Length, lpData = buffer };
            return Native.SendMessage(window, Native.WM_COPYDATA, IntPtr.Zero, ref data) == new IntPtr(1);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Brilho";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, false);
        }
    }
}
