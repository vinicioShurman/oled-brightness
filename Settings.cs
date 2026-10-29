using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brilho;

enum MethodChoice { Auto, Ddc, Gamma, Overlay }

sealed class MonitorSettings
{
    public string Name { get; set; }
    public MethodChoice Choice { get; set; }
    /// Abaixo do mínimo do monitor: os 25% de baixo do controle deixam o
    /// hardware no mínimo e escurecem a imagem por software.
    public bool Extended { get; set; }
    public double? Level { get; set; }
}

/// %APPDATA%\Brilho\settings.json, por monitor (pela identidade, não pelo número).
sealed class AppSettings
{
    public double Step { get; set; } = 0.05;
    public NightSettings Night { get; set; } = new();
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new();

    static string FilePath => Path.Combine(Log.Dir, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception e) { Log.Write("configurações ilegíveis, usando padrão: " + e.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, FilePath, true);
        }
        catch (Exception e) { Log.Write("configurações não gravadas: " + e.Message); }
    }

    public MonitorSettings For(string fingerprint)
    {
        if (!Monitors.TryGetValue(fingerprint, out var monitor)) Monitors[fingerprint] = monitor = new MonitorSettings();
        return monitor;
    }
}

/// %APPDATA%\Brilho\brilho.log: as decisões do app (rota escolhida, falhas de
/// DDC, curvas reaplicadas), para diagnosticar um monitor que não responde.
static class Log
{
    static readonly object Gate = new();
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brilho");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, "brilho.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

interface IWorker { void Post(Action action); }

/// Fila serial para tudo que fala com o hardware, como a `workQueue` do
/// Vorssaint: DDC é lento e comandos intercalados se perdem.
sealed class SerialWorker : IWorker, IDisposable
{
    readonly BlockingCollection<Action> queue = new();

    public SerialWorker()
    {
        new Thread(() =>
        {
            foreach (var action in queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception e) { Log.Write("erro na fila de hardware: " + e); }
            }
        }) { IsBackground = true, Name = "Brilho hardware" }.Start();
    }

    public void Post(Action action)
    {
        if (!queue.IsAddingCompleted) queue.Add(action);
    }

    public void Dispose() => queue.CompleteAdding();
}

/// Linha de comando: tudo roda na hora, na mesma thread.
sealed class InlineWorker : IWorker
{
    public void Post(Action action) => action();
}
