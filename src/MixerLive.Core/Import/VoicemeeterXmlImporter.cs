using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MixerLive.Core.Model;

namespace MixerLive.Core.Import;

/// <summary>
/// Imports Voicemeeter Banana settings files (the XML produced by "Save Settings" and the
/// automatically written VoicemeeterBanana_LastSettings.xml).
///
/// The files are not always well-formed XML (the auto-saved file can contain a duplicated
/// opening tag), so this importer uses a tolerant tag scanner instead of a strict parser.
/// </summary>
public static class VoicemeeterXmlImporter
{
    public sealed class ImportResult
    {
        public required MixerProfile Profile { get; init; }
        public List<string> Notes { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool LooksLikeVoicemeeterFile { get; set; }
    }

    /// <summary>Default location of the auto-saved Banana settings.</summary>
    public static string DefaultSettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Voicemeeter");

    public static string LastSettingsPath => Path.Combine(DefaultSettingsDirectory, "VoicemeeterBanana_LastSettings.xml");

    public static ImportResult ImportFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var result = Parse(text);
        result.Profile.SourceFile = path;
        result.Profile.Name = Path.GetFileNameWithoutExtension(path);
        result.Profile.ImportedAt = DateTime.Now;
        return result;
    }

    public static ImportResult Parse(string xml)
    {
        var profile = MixerProfile.CreateDefault();
        var result = new ImportResult { Profile = profile };

        // Start from a clean slate for things the file always specifies.
        foreach (var s in profile.Strips)
        {
            s.Label = "";
            s.A1 = s.A2 = s.A3 = s.B1 = s.B2 = false;
            if (!s.IsVirtual) s.Device.Clear();
        }
        foreach (var b in profile.Buses)
        {
            b.Label = "";
            if (!b.IsVirtual) b.Device.Clear();
        }

        string? appType = null;

        foreach (var el in XmlTagScanner.Scan(xml))
        {
            switch (el.Name)
            {
                case "VBAudioVoicemeeterSettings":
                    result.LooksLikeVoicemeeterFile = true;
                    break;

                case "VoiceMeeterApp":
                    appType = el.Attr("type");
                    break;

                case "OptionDev":
                    result.Notes.Add($"Voicemeeter buffer sizes were MME {el.Attr("mme")}, WDM {el.Attr("wdm")}, KS {el.Attr("ks")} samples. " +
                                     "MixerLive uses Windows shared-mode WASAPI with its own latency preset instead.");
                    break;

                case "InputDev":
                {
                    int i = el.Int("index");
                    var name = CleanName(el.Attr("name"));
                    if (i is >= 1 and <= MixerProfile.HardwareStripCount && name != null)
                    {
                        profile.Strips[i - 1].Device = new DeviceRef { Name = name };
                        result.Notes.Add($"Hardware input {i}: \"{name}\" ({DeviceTypeName(el.Attr("type"))})");
                    }
                    break;
                }

                case "OutputDev":
                {
                    int i = el.Int("index");
                    var name = CleanName(el.Attr("name"));
                    if (i is >= 1 and <= MixerProfile.HardwareBusCount && name != null)
                    {
                        profile.Buses[i - 1].Device = new DeviceRef { Name = name };
                        result.Notes.Add($"Hardware output A{i}: \"{name}\" ({DeviceTypeName(el.Attr("type"))})");
                    }
                    break;
                }

                case "Strip":
                {
                    int i = el.Int("index");
                    if (i is < 1 or > MixerProfile.StripCount) break;
                    var s = profile.Strips[i - 1];

                    if (el.Has("mute"))
                    {
                        s.Mute = el.Bool("mute");
                        s.Solo = el.Bool("solo");
                        s.Mono = el.Bool("mono");
                        s.A1 = el.Bool("busa");
                        s.A2 = el.Bool("busa2");
                        s.A3 = el.Bool("busa3");
                        s.B1 = el.Bool("busb");
                        s.B2 = el.Bool("busb2");
                        s.GainDb = Math.Clamp(el.Double("dblevel", s.GainDb), -60, 12);
                    }

                    if (el.Has("EQGain1"))
                    {
                        s.EqLowDb = el.Double("EQGain1");
                        s.EqMidDb = el.Double("EQGain2");
                        s.EqHighDb = el.Double("EQGain3");
                    }
                    break;
                }

                case "StripComp":
                {
                    int i = el.Int("index");
                    if (i is < 1 or > MixerProfile.StripCount) break;
                    var c = profile.Strips[i - 1].Comp;
                    c.Ratio = Math.Max(1, el.Double("comprate", 1));
                    c.ThresholdDb = el.Double("threshold", -40);
                    c.AttackMs = el.Double("attack", 10);
                    c.ReleaseMs = el.Double("release", 60);
                    c.Knee = el.Double("knee", 0.5);
                    c.AutoMakeup = el.Bool("automakeup", true);
                    c.GainInDb = el.Double("gainin");
                    c.GainOutDb = el.Double("gainout");
                    c.Enabled = c.Ratio > 1.001;
                    break;
                }

                case "StripGate":
                {
                    int i = el.Int("index");
                    if (i is < 1 or > MixerProfile.StripCount) break;
                    var g = profile.Strips[i - 1].Gate;
                    g.ThresholdDb = el.Double("thresin", -50);
                    g.DampingDb = el.Double("damping", -50);
                    g.AttackMs = el.Double("attack", 20);
                    g.HoldMs = el.Double("hold", 200);
                    g.ReleaseMs = el.Double("release", 1250);
                    // Banana's Gate knob at 0 stores thresin = -50 (gate off).
                    g.Enabled = g.ThresholdDb > -49.99;
                    break;
                }

                case "Bus":
                {
                    int i = el.Int("index");
                    if (i is < 1 or > MixerProfile.BusCount) break;
                    var b = profile.Buses[i - 1];

                    if (el.Has("cell"))
                    {
                        int channel = el.Int("channel");
                        int cell = el.Int("cell");
                        if (cell is >= 1 and <= MixerProfile.EqCellCount && channel is 1 or 2)
                        {
                            var band = (channel == 1 ? b.EqLeft : b.EqRight)[cell - 1];
                            band.On = el.Bool("EQon", true);
                            band.Type = el.Int("EQtype");
                            band.GainDb = el.Double("dblevel");
                            band.Freq = el.Double("freq", band.Freq);
                            band.Q = el.Double("Q", band.Q);
                        }
                    }
                    else if (el.Has("mute"))
                    {
                        b.Mute = el.Bool("mute");
                        b.Mono = el.Bool("mono");
                        b.EqOn = el.Bool("EQon");
                        b.GainDb = Math.Clamp(el.Double("dblevel", b.GainDb), -60, 12);
                    }
                    break;
                }

                default:
                    if (el.Name.StartsWith("Label", StringComparison.Ordinal) && el.Content != null)
                        ApplyLabel(profile, el.Name, el.Content.Trim());
                    break;
            }
        }

        if (!result.LooksLikeVoicemeeterFile)
            result.Warnings.Add("This does not look like a Voicemeeter settings file (missing <VBAudioVoicemeeterSettings> root).");

        if (appType != null && !appType.Contains("Banana", StringComparison.OrdinalIgnoreCase))
            result.Warnings.Add($"File was saved by Voicemeeter \"{appType}\"; only the first 5 strips / 5 buses are imported.");

        // Virtual inputs: Voicemeeter's own virtual cable endpoints keep working as plain Windows
        // devices even when Voicemeeter is closed, so loopback-capture them.
        profile.Strips[3].Device = new DeviceRef { Name = "Voicemeeter Input" };
        profile.Strips[4].Device = new DeviceRef { Name = "Voicemeeter AUX Input" };
        result.Notes.Add("Virtual input 1 = loopback of \"Voicemeeter Input\", virtual input 2 = loopback of \"Voicemeeter AUX Input\".");

        // Virtual outputs need a cable whose render side we can play into (VB-CABLE by default).
        profile.Buses[3].Device = new DeviceRef { Name = "CABLE Input" };
        profile.Buses[4].Device = new DeviceRef();
        result.Notes.Add("Virtual output B1 is set to \"CABLE Input\" (VB-CABLE). Apps can then record from \"CABLE Output\". Change it in the B1 device box if you use another cable.");

        profile.Normalize();
        return result;
    }

    private static void ApplyLabel(MixerProfile p, string tag, string text)
    {
        // LabelStrip1..3, LabelVirtualStrip1..2, LabelBus1..3, LabelVirtualBus1..2
        var m = Regex.Match(tag, @"^Label(Virtual)?(Strip|Bus)(\d+)$");
        if (!m.Success) return;
        bool isVirtual = m.Groups[1].Success;
        int n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (m.Groups[2].Value == "Strip")
        {
            int idx = isVirtual ? MixerProfile.HardwareStripCount + n - 1 : n - 1;
            if (idx >= 0 && idx < MixerProfile.StripCount) p.Strips[idx].Label = text;
        }
        else
        {
            int idx = isVirtual ? MixerProfile.HardwareBusCount + n - 1 : n - 1;
            if (idx >= 0 && idx < MixerProfile.BusCount) p.Buses[idx].Label = text;
        }
    }

    private static string? CleanName(string? raw)
    {
        if (raw == null) return null;
        var t = raw.Trim();
        return t.Length == 0 || t == "-" ? null : t;
    }

    /// <summary>Voicemeeter device type codes as used by its Remote API.</summary>
    private static string DeviceTypeName(string? type) => type switch
    {
        "1" => "MME",
        "3" => "WDM",
        "4" => "KS",
        "5" => "ASIO",
        null or "" => "unknown type",
        _ => $"type {type}"
    };
}

internal sealed class ScannedTag
{
    public required string Name { get; init; }
    public required Dictionary<string, string> Attributes { get; init; }
    public string? Content { get; init; }

    public bool Has(string attr) => Attributes.ContainsKey(attr);
    public string? Attr(string attr) => Attributes.TryGetValue(attr, out var v) ? v : null;

    public int Int(string attr, int fallback = 0)
        => int.TryParse(Attr(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public double Double(string attr, double fallback = 0)
        => double.TryParse(Attr(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool Bool(string attr, bool fallback = false)
    {
        var v = Attr(attr);
        if (v == null) return fallback;
        return v.Trim() is "1" or "true" or "True";
    }
}

/// <summary>
/// Forgiving tag scanner: yields every start tag with its attributes, plus the text content
/// when the tag is immediately followed by text and its own end tag (e.g. &lt;LabelStrip1&gt;MIC&lt;/LabelStrip1&gt;).
/// Nesting errors in the source are ignored.
/// </summary>
internal static class XmlTagScanner
{
    private static readonly Regex AttrRegex = new(
        @"([A-Za-z_][\w\-.:]*)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled);

    public static IEnumerable<ScannedTag> Scan(string s)
    {
        int i = 0;
        int len = s.Length;
        while (i < len && (i = s.IndexOf('<', i)) >= 0)
        {
            if (StartsWith(s, i, "<?"))
            {
                int e = s.IndexOf("?>", i, StringComparison.Ordinal);
                if (e < 0) yield break;
                i = e + 2;
                continue;
            }
            if (StartsWith(s, i, "<!--"))
            {
                int e = s.IndexOf("-->", i, StringComparison.Ordinal);
                if (e < 0) yield break;
                i = e + 3;
                continue;
            }
            if (StartsWith(s, i, "</") || StartsWith(s, i, "<!"))
            {
                int e = s.IndexOf('>', i);
                if (e < 0) yield break;
                i = e + 1;
                continue;
            }

            int j = i + 1;
            while (j < len && IsNameChar(s[j])) j++;
            if (j == i + 1)
            {
                i++;
                continue;
            }
            string name = s.Substring(i + 1, j - i - 1);

            // Find the closing '>' while honouring quotes.
            int k = j;
            char quote = '\0';
            while (k < len)
            {
                char c = s[k];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    break;
                }
                k++;
            }
            if (k >= len) yield break;

            bool selfClosing = k > j && s[k - 1] == '/';
            string attrText = s.Substring(j, (selfClosing ? k - 1 : k) - j);
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AttrRegex.Matches(attrText))
            {
                string value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                attrs[m.Groups[1].Value] = Unescape(value);
            }

            string? content = null;
            int next = k + 1;
            if (!selfClosing)
            {
                int lt = s.IndexOf('<', next);
                if (lt >= 0 && StartsWith(s, lt, "</" + name))
                {
                    content = Unescape(s.Substring(next, lt - next));
                    int e = s.IndexOf('>', lt);
                    next = e < 0 ? len : e + 1;
                }
            }

            yield return new ScannedTag { Name = name, Attributes = attrs, Content = content };
            i = next;
        }
    }

    private static bool IsNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':';

    private static bool StartsWith(string s, int index, string token)
        => string.CompareOrdinal(s, index, token, 0, token.Length) == 0;

    private static string Unescape(string v)
    {
        if (v.IndexOf('&') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '&')
            {
                sb.Append(v[i]);
                continue;
            }
            int semi = v.IndexOf(';', i);
            if (semi < 0)
            {
                sb.Append(v, i, v.Length - i);
                break;
            }
            string ent = v.Substring(i + 1, semi - i - 1);
            switch (ent)
            {
                case "amp": sb.Append('&'); break;
                case "lt": sb.Append('<'); break;
                case "gt": sb.Append('>'); break;
                case "quot": sb.Append('"'); break;
                case "apos": sb.Append('\''); break;
                default:
                    if (ent.StartsWith('#'))
                    {
                        bool hex = ent.Length > 1 && (ent[1] == 'x' || ent[1] == 'X');
                        string num = hex ? ent[2..] : ent[1..];
                        if (int.TryParse(num, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int cp))
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                            break;
                        }
                    }
                    sb.Append('&').Append(ent).Append(';');
                    break;
            }
            i = semi;
        }
        return sb.ToString();
    }
}
