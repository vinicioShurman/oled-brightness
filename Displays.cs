using System.Runtime.InteropServices;

namespace Brilho;

/// Uma tela como o Windows a vê agora.
sealed class DisplayInfo
{
    /// \\.\DISPLAY1 — o número pode ser reaproveitado por outro monitor depois
    /// de uma reconexão, então nunca serve como identidade.
    public string GdiName;
    public string FriendlyName;
    /// Identidade estável do monitor físico NESTA conexão (o caminho do
    /// dispositivo inclui fabricante, modelo e porta). Mesma ideia do
    /// `ddcPathKey` do Vorssaint: a curva de gama ou a preferência salva de um
    /// monitor nunca vai parar em outro que herdou o mesmo número.
    public string Fingerprint;
    public IntPtr HMonitor;
    public Rectangle Bounds;
    public bool Primary;
    public bool Internal;
    /// HDR ou "cor avançada" ligada: o compositor trabalha em luz linear e a
    /// curva de gama legada (SetDeviceGammaRamp) deixa de valer.
    public bool AdvancedColor;
    public uint OutputTechnology;
    internal bool Described;

    public string ConnectionName => OutputTechnology switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS", 10 => "DisplayPort", 11 => "eDP",
        15 => "Miracast", 0x80000000 => "interna", _ => "outra",
    };
}

static class DisplayEnumerator
{
    public static List<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();
        Native.MonitorEnumProc proc = (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                list.Add(new DisplayInfo
                {
                    HMonitor = h,
                    GdiName = mi.szDevice,
                    FriendlyName = mi.szDevice,
                    Fingerprint = mi.szDevice,
                    Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                    Primary = (mi.dwFlags & 1) != 0,
                });
            }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        try { AddDisplayConfigDetails(list); }
        catch (Exception e) { Log.Write("displayconfig falhou: " + e.Message); }

        list.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1)
            : a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X)
            : a.Bounds.Y.CompareTo(b.Bounds.Y));
        return list;
    }

    static void AddDisplayConfigDetails(List<DisplayInfo> list)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, modeCount) * 64);
            try
            {
                int rc = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (rc == 122) continue; // a topologia mudou entre as duas chamadas
                if (rc != 0) return;
                for (int i = 0; i < pathCount; i++) Describe(paths[i], list);
                return;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
    }

    static void Describe(Native.DISPLAYCONFIG_PATH_INFO path, List<DisplayInfo> list)
    {
        var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        source.header = Header(1, Marshal.SizeOf(source), path.sourceInfo.adapterId, path.sourceInfo.id);
        if (Native.DisplayConfigGetDeviceInfo(ref source) != 0) return;
        var info = list.Find(d => string.Equals(d.GdiName, source.viewGdiDeviceName, StringComparison.OrdinalIgnoreCase));
        // Num clone (espelhamento) duas saídas mostram a mesma imagem; fica a primeira.
        if (info == null || info.Described) return;
        info.Described = true;

        var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
        target.header = Header(2, Marshal.SizeOf(target), path.targetInfo.adapterId, path.targetInfo.id);
        if (Native.DisplayConfigGetDeviceInfo(ref target) == 0)
        {
            if (!string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)) info.FriendlyName = target.monitorFriendlyDeviceName;
            if (!string.IsNullOrWhiteSpace(target.monitorDevicePath)) info.Fingerprint = target.monitorDevicePath;
        }

        info.OutputTechnology = path.targetInfo.outputTechnology;
        info.Internal = info.OutputTechnology is 6 or 11 or 13 or 0x80000000;

        var color = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        color.header = Header(9, Marshal.SizeOf(color), path.targetInfo.adapterId, path.targetInfo.id);
        if (Native.DisplayConfigGetDeviceInfo(ref color) == 0)
            info.AdvancedColor = (color.value & 0x2) != 0;
    }

    static Native.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, Native.LUID adapter, uint id) =>
        new() { type = type, size = size, adapterId = adapter, id = id };
}
