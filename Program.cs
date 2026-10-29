using System.Globalization;

namespace Brilho;

static class Program
{
    static TrayApp app;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) return CommandLine.Run(args);

        using var mutex = new Mutex(true, @"Local\Brilho.SingleInstance", out bool first);
        if (!first)
        {
            Ipc.Send("show");
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("erro na interface: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Última chance de devolver as curvas antes de o processo cair.
            Log.Write("erro fatal: " + e.ExceptionObject);
            try { app?.Cleanup(); } catch { }
        };

        ApplicationConfiguration.Initialize();
        app = new TrayApp();
        Application.Run(app);
        return 0;
    }
}

/// `Brilho.exe --list | --set <n|all> <0-100> | --reset`. Com o app aberto o
/// pedido vai para ele; sem, é aplicado direto (DDC/CI e gama ficam valendo
/// depois que o comando termina, e o diário de gama garante o desfazer).
static class CommandLine
{
    public static int Run(string[] args)
    {
        AttachToParentConsole();
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--list": return List();
                case "--set": return Set(args);
                case "--reset": return Reset();
                case "--preview" when args.Length > 1:
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    var preview = Create(out var previewSettings);
                    if (args.Length > 2 && args[2] == "--noite") previewSettings.Night.Enabled = true; // só na prévia, não é salvo
                    var previewNight = new NightScheduler(previewSettings.Night, preview, () => { });
                    using (var flyout = new Flyout(preview, previewSettings, previewNight, () => { }))
                    using (var image = flyout.Snapshot(Dpi.ScaleAt(Point.Empty)))
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine("Prévia salva em " + args[1]);
                    return 0;
                case "--simular-noite":
                    SimulateNight();
                    return 0;
                case "--quit":
                    bool sent = Ipc.Send("quit");
                    Console.WriteLine(sent ? "Brilho encerrado." : "O Brilho não está aberto.");
                    return sent ? 0 : 1;
                case "--help" or "-h" or "/?": Help(); return 0;
                default:
                    Console.Error.WriteLine($"Opção desconhecida: {args[0]}");
                    Help();
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Erro: " + e.Message);
            return 1;
        }
        finally { Console.Out.Flush(); }
    }

    static void Help()
    {
        Console.WriteLine("Brilho — controle de brilho por tela (DDC/CI, WMI, curva de gama, overlay)");
        Console.WriteLine();
        Console.WriteLine("  Brilho.exe                     abre o app na bandeja");
        Console.WriteLine("  Brilho.exe --list              lista as telas, a rota usada e o nível");
        Console.WriteLine("  Brilho.exe --set <n|all> <0-100>");
        Console.WriteLine("  Brilho.exe --reset             remove todo escurecimento por software");
        Console.WriteLine("  Brilho.exe --quit              fecha o app da bandeja (devolvendo as curvas)");
    }

    static int List()
    {
        if (Ipc.IsRunning && Ipc.Send("list"))
        {
            Console.Write(File.ReadAllText(Path.Combine(Log.Dir, "status.txt")));
            Console.WriteLine("(estado do Brilho em execução)");
            return 0;
        }
        var service = Create(out _);
        Console.Write(service.FormatStatus());
        return 0;
    }

    static int Set(string[] args)
    {
        if (args.Length < 3 || !double.TryParse(args[2].TrimEnd('%').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            || percent < 0 || percent > 100)
        {
            Console.Error.WriteLine("Uso: Brilho.exe --set <n|all> <0-100>");
            return 1;
        }
        if (Ipc.IsRunning)
        {
            bool sent = Ipc.Send($"set|{args[1]}|{percent.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine(sent ? "Aplicado pelo Brilho em execução." : "O Brilho em execução recusou o pedido (tela inexistente?).");
            return sent ? 0 : 1;
        }
        var service = Create(out var settings);
        var targets = service.Resolve(args[1]);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"Tela '{args[1]}' não encontrada. Veja --list.");
            return 1;
        }
        int failures = 0;
        foreach (var target in targets)
        {
            if (!BrightnessService.CanApplyFromCommandLine(target))
            {
                Console.Error.WriteLine($"{target.Info.FriendlyName}: o overlay só existe com o app aberto.");
                failures++;
                continue;
            }
            bool ok = service.SetLevel(target, percent / 100);
            Console.WriteLine($"{target.Info.FriendlyName}: {percent}% via {target.MethodLabel}{(ok ? "" : " (recusado)")}");
            if (!ok) failures++;
        }
        settings.Save();
        return failures == 0 ? 0 : 1;
    }

    static int Reset()
    {
        if (Ipc.IsRunning)
        {
            Ipc.Send("reset");
            Console.WriteLine("Escurecimento removido pelo Brilho em execução.");
            return 0;
        }
        var service = Create(out var settings);
        service.RemoveSoftwareDimming();
        settings.Save();
        Console.WriteLine("Curvas de gama devolvidas.");
        return 0;
    }

    /// Diagnóstico: a curva da noite configurada, minuto a minuto, sem tocar
    /// em nenhuma tela. Simula uma tela que de dia fica em 100% e outra em 20%.
    static void SimulateNight()
    {
        var night = AppSettings.Load().Night;
        Console.WriteLine($"Começa {night.Start}, mínimo {night.Target:P0} às {night.End}, volta às {night.Wake} (+{NightScheduler.WakeFade.TotalMinutes} min)");
        Console.WriteLine("hora   fase      tela 100%  tela 20%");
        var day = DateTime.Today.AddHours(18);
        for (int minute = 0; minute <= 15 * 60; minute++)
        {
            var now = day.AddMinutes(minute);
            var cycle = NightScheduler.Evaluate(night, now);
            double a = NightScheduler.Desired(cycle, 1, night.Target, null);
            double b = NightScheduler.Desired(cycle, 0.2, night.Target, null);
            if (minute % 30 == 0 || (cycle.Phase is NightPhase.Waking && minute % 5 == 0))
                Console.WriteLine($"{now:HH:mm}  {cycle.Phase,-8}  {a,8:P0}  {b,8:P0}");
        }
    }

    static BrightnessService Create(out AppSettings settings)
    {
        settings = AppSettings.Load();
        var service = new BrightnessService(settings, action => action(), new InlineWorker(), commandLine: true);
        service.Rebuild();
        return service;
    }

    /// Um app de janela não tem console; preso ao do terminal que o chamou, o
    /// texto aparece lá. Com a saída redirecionada (pipe) não precisa.
    static void AttachToParentConsole()
    {
        IntPtr stdout = Native.GetStdHandle(-11);
        if ((stdout == IntPtr.Zero || stdout == new IntPtr(-1)) && Native.AttachConsole(-1))
            Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
}
