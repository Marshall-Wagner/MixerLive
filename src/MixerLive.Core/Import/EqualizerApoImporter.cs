using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MixerLive.Core.Model;
using Microsoft.Win32;

namespace MixerLive.Core.Import;

/// <summary>
/// Reads parametric EQ text files into EQ cells. The format is Equalizer APO's filter syntax,
/// which is also what PEQdB, AutoEq ("ParametricEQ.txt") and Room EQ Wizard export, so any such
/// .txt works without Equalizer APO being installed:
///
///   Preamp: -10.4 dB
///   Filter 1: ON LSC Fc 137 Hz Gain -6.8 dB Q 0.70
///   Filter 3: ON PK  Fc 159 Hz Gain -5.3 dB Q 1.11
///   Include: other.txt
///
/// Supported filter types map onto the Voicemeeter-compatible cell types used by the bus EQ.
/// </summary>
public static class EqualizerApoImporter
{
    public sealed class Result
    {
        public double PreampDb { get; set; }
        public List<EqBand> Bands { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Files { get; } = new();

        public string Summary =>
            $"{Bands.Count} filter{(Bands.Count == 1 ? "" : "s")}, preamp {PreampDb:+0.0;-0.0;0.0} dB" +
            (Files.Count > 0 ? $" from {string.Join(" + ", Files.Select(Path.GetFileName))}" : "");
    }

    private const string DefaultConfigDir = @"C:\Program Files\EqualizerAPO\config";

    private static readonly Regex FilterRegex = new(
        @"^(?<state>ON|OFF)\s+(?<type>[A-Z]+)(?:\s+(?<slope>6DB|12DB))?\s+Fc\s+(?<fc>[\d.]+)\s*k?Hz" +
        @"(?:\s+Gain\s+(?<gain>-?[\d.]+)\s*dB)?(?:\s+Q\s+(?<q>[\d.]+))?(?:\s+BW\s+Oct\s+(?<bw>[\d.]+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnusedSlotRegex = new(@"^(ON|OFF)\s+None\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The config.txt Equalizer APO is currently using, if it is installed.</summary>
    public static string? ActiveConfigPath
    {
        get
        {
            string dir = DefaultConfigDir;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                if (key?.GetValue("ConfigPath") is string s && Directory.Exists(s)) dir = s;
            }
            catch { /* registry unavailable */ }
            string path = Path.Combine(dir, "config.txt");
            return File.Exists(path) ? path : null;
        }
    }

    public static Result ImportFile(string path)
    {
        var r = new Result();
        ParseFile(Path.GetFullPath(path), r, 0);
        Finish(r);
        return r;
    }

    public static Result Parse(string text, string? baseDirectory = null)
    {
        var r = new Result();
        ParseText(text, baseDirectory, r, 0);
        Finish(r);
        return r;
    }

    private static void Finish(Result r)
    {
        if (r.Bands.Count > MixerProfile.MaxEqCells)
        {
            r.Warnings.Add($"Only the first {MixerProfile.MaxEqCells} of {r.Bands.Count} filters were kept.");
            r.Bands.RemoveRange(MixerProfile.MaxEqCells, r.Bands.Count - MixerProfile.MaxEqCells);
        }
        if (r.Bands.Count == 0 && !r.Warnings.Any(w => w.StartsWith("No filter", StringComparison.Ordinal)))
            r.Warnings.Add("No filter lines were found.");
    }

    private static void ParseFile(string path, Result r, int depth)
    {
        if (r.Files.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            r.Warnings.Add($"Skipped repeated include of {Path.GetFileName(path)}.");
            return;
        }
        r.Files.Add(path);
        ParseText(File.ReadAllText(path, Encoding.UTF8), Path.GetDirectoryName(path), r, depth);
    }

    private static void ParseText(string text, string? baseDir, Result r, int depth)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>(); // e.g. REW's "Dated:", "Notes:", "Equaliser:" header lines
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            string keyLower = key.ToLowerInvariant();

            if (keyLower == "preamp")
            {
                var m = Regex.Match(value, @"-?[\d.]+");
                if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                    r.PreampDb += db; // Equalizer APO sums successive Preamp lines
                else
                    r.Warnings.Add($"Could not read preamp value \"{value}\".");
                continue;
            }

            if (keyLower.StartsWith("filter", StringComparison.Ordinal))
            {
                var band = ParseFilter(value, r.Warnings, key);
                if (band != null) r.Bands.Add(band);
                continue;
            }

            if (keyLower == "include")
            {
                string inc = value.Trim('"');
                string full = Path.IsPathRooted(inc) ? inc : Path.Combine(baseDir ?? Environment.CurrentDirectory, inc);
                if (depth >= 8) r.Warnings.Add($"Include nesting too deep; skipped {inc}.");
                else if (!File.Exists(full)) r.Warnings.Add($"Included file not found: {full}");
                else ParseFile(Path.GetFullPath(full), r, depth + 1);
                continue;
            }

            switch (keyLower)
            {
                case "channel":
                    if (ignored.Add(key)) r.Warnings.Add("\"Channel:\" selections are ignored; filters are applied to both channels of the bus.");
                    break;
                case "graphiceq":
                    if (ignored.Add(key)) r.Warnings.Add("\"GraphicEQ:\" lines are not supported (only parametric filters are imported).");
                    break;
                case "convolution":
                    if (ignored.Add(key)) r.Warnings.Add("\"Convolution:\" (impulse response) lines are not supported.");
                    break;
                case "device":
                case "stage":
                case "copy":
                case "delay":
                case "eval":
                case "if":
                case "elseif":
                case "else":
                case "endif":
                case "loudnesscorrection":
                case "vst":
                case "expression":
                    if (ignored.Add(key)) r.Warnings.Add($"\"{key}:\" lines are ignored.");
                    break;
                default:
                    if (ignored.Add(key)) unknown.Add(key);
                    break;
            }
        }
        if (unknown.Count > 0)
        {
            string examples = string.Join(", ", unknown.Take(3).Select(k => $"\"{k}:\""));
            r.Warnings.Add($"{unknown.Count} line{(unknown.Count == 1 ? " that is" : "s that are")} not EQ filters ignored ({examples}{(unknown.Count > 3 ? ", …" : "")}).");
        }
    }

    private static EqBand? ParseFilter(string value, List<string> warnings, string key)
    {
        // REW lists unused filter slots as "Filter 12: ON  None".
        if (UnusedSlotRegex.IsMatch(value)) return null;

        var m = FilterRegex.Match(value);
        if (!m.Success)
        {
            warnings.Add($"Could not read \"{key}: {value}\".");
            return null;
        }

        bool on = m.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
        string type = m.Groups["type"].Value.ToUpperInvariant();
        string slope = m.Groups["slope"].Value.ToUpperInvariant();
        double fc = Num(m.Groups["fc"].Value);
        if (value.Contains("kHz", StringComparison.OrdinalIgnoreCase)) fc *= 1000;
        double gain = m.Groups["gain"].Success ? Num(m.Groups["gain"].Value) : 0;
        double? q = m.Groups["q"].Success ? Num(m.Groups["q"].Value) : null;
        if (m.Groups["bw"].Success)
        {
            double bw = Num(m.Groups["bw"].Value);
            double p = Math.Pow(2, bw);
            q = Math.Sqrt(p) / (p - 1);
        }

        int cellType;
        double qDefault = 0.707;
        switch (type)
        {
            case "PK": case "PEQ": case "MODAL": cellType = 0; qDefault = 1.0; break;
            case "LP": case "LPQ": cellType = 1; break;
            case "BP": cellType = 2; qDefault = 1.0; break;
            case "HP": case "HPQ": cellType = 3; break;
            case "NO": cellType = 4; qDefault = 10.0; break;
            case "LS": case "LSC": cellType = 5; break;
            case "HS": case "HSC": cellType = 6; break;
            case "AP":
                warnings.Add($"All-pass filter at {fc:0} Hz skipped (not supported).");
                return null;
            default:
                warnings.Add($"Filter type \"{type}\" at {fc:0} Hz is not supported; skipped.");
                return null;
        }

        if (slope == "6DB" && cellType is 5 or 6)
        {
            warnings.Add($"{type} 6DB at {fc:0} Hz approximated with a second-order shelf (Q 0.5).");
            q ??= 0.5;
        }

        return new EqBand
        {
            On = on,
            Type = cellType,
            Freq = Math.Clamp(fc, 10, 22000),
            GainDb = Math.Clamp(gain, -30, 30),
            Q = Math.Clamp(q ?? qDefault, 0.1, 30)
        };
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
