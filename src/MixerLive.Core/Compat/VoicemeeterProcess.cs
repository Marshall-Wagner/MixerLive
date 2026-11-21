using System.Diagnostics;

namespace MixerLive.Core.Compat;

/// <summary>Detects (and on request closes) a running Voicemeeter so both mixers do not fight over the same outputs.</summary>
public static class VoicemeeterProcess
{
    private static readonly HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "voicemeeter", "voicemeeter_x64",
        "voicemeeterpro", "voicemeeterpro_x64",
        "voicemeeter8", "voicemeeter8x64"
    };

    /// <summary>One process snapshot (GetProcessesByName would take a full snapshot per name).</summary>
    public static Process[] Find()
    {
        var hits = new List<Process>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return Array.Empty<Process>(); }

        foreach (var p in all)
        {
            bool match = false;
            try { match = ProcessNames.Contains(p.ProcessName); }
            catch { /* process exited meanwhile */ }
            if (match) hits.Add(p);
            else p.Dispose();
        }
        return hits.ToArray();
    }

    public static bool IsRunning()
    {
        var ps = Find();
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>Asks Voicemeeter to close; kills it if it is still alive after the grace period.</summary>
    public static bool TryClose(TimeSpan grace)
    {
        var ps = Find();
        if (ps.Length == 0) return true;
        try
        {
            foreach (var p in ps)
            {
                try { p.CloseMainWindow(); } catch { /* ignore */ }
            }
            var deadline = DateTime.UtcNow + grace;
            while (DateTime.UtcNow < deadline && ps.Any(p => { p.Refresh(); return !p.HasExited; }))
                Thread.Sleep(200);
            foreach (var p in ps)
            {
                p.Refresh();
                if (!p.HasExited)
                {
                    try { p.Kill(); } catch { /* ignore */ }
                }
            }
            return !IsRunning();
        }
        finally
        {
            foreach (var p in ps) p.Dispose();
        }
    }
}
