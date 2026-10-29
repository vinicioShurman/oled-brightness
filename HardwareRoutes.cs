using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Brilho;

/// Um canal que muda o brilho de verdade (a luz de fundo), não a imagem.
/// Usado só na thread de trabalho: DDC é lento e os monitores descartam
/// comandos intercalados.
interface IHardwareRoute : IDisposable
{
    bool Readable { get; }
    double? InitialLevel { get; }
    double? Read();
    bool Write(double level);
}

/// DDC/CI pelo dxva2. No Mac o Vorssaint monta os pacotes à mão (endereço
/// 0x37, sub-endereço 0x51, VCP 0x10, checksum XOR) sobre APIs privadas de
/// I2C; no Windows a Monitor Configuration API pública faz o enquadramento.
/// O espaçamento entre comandos continua por nossa conta.
sealed class DdcRoute : IHardwareRoute
{
    const byte Luminance = 0x10;
    /// O padrão DDC/CI pede 50 ms entre comandos completos. Sem isso, arrastar
    /// o controle enfileira comandos rápido demais e alguns monitores perdem o
    /// sinal até serem desligados (issue #301 do Vorssaint).
    const int CommandIntervalMs = 50;

    IntPtr handle;
    uint maximum = 100;
    long lastCommandEnd;

    public bool Readable { get; private set; }
    public double? InitialLevel { get; private set; }

    DdcRoute(IntPtr handle) => this.handle = handle;

    /// Lê o brilho e, com isso, classifica o canal. Sem resposta o monitor
    /// (TVs, adaptadores HDMI) cai para o escurecimento por software, a não
    /// ser que a pessoa force DDC: há monitores que aceitam escrita e nunca
    /// respondem leitura, e só quem olha a tela sabe a diferença.
    public static DdcRoute Open(IntPtr hMonitor, bool force, out string note)
    {
        note = null;
        if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
        {
            note = "Sem monitor físico para DDC/CI";
            return null;
        }
        var monitors = new Native.PHYSICAL_MONITOR[count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
        {
            note = $"DDC/CI indisponível (erro {Marshal.GetLastWin32Error()})";
            return null;
        }
        for (int i = 1; i < count; i++) Native.DestroyPhysicalMonitor(monitors[i].hPhysicalMonitor);

        var route = new DdcRoute(monitors[0].hPhysicalMonitor);
        int error = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (route.TryRead(out uint current, out uint max, out error))
            {
                route.maximum = max > 0 ? max : 100;
                route.Readable = true;
                route.InitialLevel = Math.Clamp((double)current / route.maximum, 0, 1);
                return route;
            }
            Thread.Sleep(CommandIntervalMs);
        }
        if (force)
        {
            note = "DDC/CI forçado: o monitor não responde leituras, só recebe o valor";
            return route;
        }
        note = $"Sem DDC/CI nesta conexão (erro {FormatError(error)})";
        route.Dispose();
        return null;
    }

    public double? Read() =>
        TryRead(out uint current, out uint max, out _) && max > 0 ? Math.Clamp((double)current / max, 0, 1) : null;

    public bool Write(double level)
    {
        uint value = (uint)Math.Round(Math.Clamp(level, 0, 1) * maximum);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Pace();
            bool ok = Native.SetVCPFeature(handle, Luminance, value);
            lastCommandEnd = Stopwatch.GetTimestamp();
            if (ok) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    bool TryRead(out uint current, out uint max, out int error)
    {
        Pace();
        bool ok = Native.GetVCPFeatureAndVCPFeatureReply(handle, Luminance, out _, out current, out max);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        lastCommandEnd = Stopwatch.GetTimestamp();
        return ok;
    }

    void Pace()
    {
        if (lastCommandEnd == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(lastCommandEnd).TotalMilliseconds;
        if (elapsed < CommandIntervalMs) Thread.Sleep((int)Math.Ceiling(CommandIntervalMs - elapsed));
    }

    static string FormatError(int error) => error > 0 && error < 0x10000 ? error.ToString() : $"0x{error:X8}";

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Native.DestroyPhysicalMonitor(handle);
        handle = IntPtr.Zero;
    }
}

/// Painel interno de notebook pelo WMI: o papel do DisplayServices no Mac.
sealed class WmiRoute : IHardwareRoute
{
    readonly string instance;

    public bool Readable => true;
    public double? InitialLevel { get; private set; }

    WmiRoute(string instance) => this.instance = instance;

    public static WmiRoute Open(out string note)
    {
        note = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    return new WmiRoute((string)item["InstanceName"])
                    {
                        InitialLevel = Convert.ToDouble(item["CurrentBrightness"]) / 100.0,
                    };
                }
            }
        }
        catch (Exception e) { Log.Write("wmi indisponível: " + e.Message); }
        note = "Painel interno sem controle de brilho pelo WMI";
        return null;
    }

    public double? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
                using (item)
                    if ((string)item["InstanceName"] == instance)
                        return Convert.ToDouble(item["CurrentBrightness"]) / 100.0;
        }
        catch (Exception e) { Log.Write("wmi leitura falhou: " + e.Message); }
        return null;
    }

    public bool Write(double level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if ((string)item["InstanceName"] != instance) continue;
                    item.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)Math.Round(Math.Clamp(level, 0, 1) * 100) });
                    return true;
                }
            }
        }
        catch (Exception e) { Log.Write("wmi escrita falhou: " + e.Message); }
        return false;
    }

    public void Dispose() { }
}
