using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Pure logic only (no registry, no hardware, no UI) so that all of it is covered by src\Tests.cs.
namespace Tuner
{
    public static class App
    {
        public const string Name = "Headroom";
        public const string Version = "0.2.0";
        public const string Tagline = "Set a ceiling. The GPU stays under it.";
    }

    public class TableFormatException : Exception { public TableFormatException(string m) : base(m) { } }

    /// <summary>The four things this tool lets you change.</summary>
    public class Settings
    {
        public int MaxClockMHz, PowerLimitW, MaxVoltageMv, TempLimitC;

        public Settings(int clock, int power, int volt, int temp) { MaxClockMHz = clock; PowerLimitW = power; MaxVoltageMv = volt; TempLimitC = temp; }
        public Settings Clone() { return new Settings(MaxClockMHz, PowerLimitW, MaxVoltageMv, TempLimitC); }

        public override bool Equals(object obj)
        {
            Settings o = obj as Settings;
            return o != null && o.MaxClockMHz == MaxClockMHz && o.PowerLimitW == PowerLimitW && o.MaxVoltageMv == MaxVoltageMv && o.TempLimitC == TempLimitC;
        }
        public override int GetHashCode() { return MaxClockMHz * 31 + PowerLimitW * 17 + MaxVoltageMv * 7 + TempLimitC; }
        public override string ToString() { return MaxClockMHz + " MHz / " + PowerLimitW + " W / " + MaxVoltageMv + " mV / " + TempLimitC + " C"; }
        /// <summary>For the window: "1500 MHz · 70 W · 962 mV · 100 °C".</summary>
        public string Pretty() { return MaxClockMHz + " MHz · " + PowerLimitW + " W · " + MaxVoltageMv + " mV · " + TempLimitC + " °C"; }
    }

    /// <summary>What differs between the settings applied to the GPU and the ones on the sliders.</summary>
    public static class Changes
    {
        public static string[] Describe(Settings applied, Settings pending)
        {
            List<string> list = new List<string>();
            if (applied.MaxClockMHz != pending.MaxClockMHz) list.Add("Top speed " + applied.MaxClockMHz + " → " + pending.MaxClockMHz + " MHz");
            if (applied.PowerLimitW != pending.PowerLimitW) list.Add("Power limit " + applied.PowerLimitW + " → " + pending.PowerLimitW + " W");
            if (applied.MaxVoltageMv != pending.MaxVoltageMv) list.Add("Voltage ceiling " + applied.MaxVoltageMv + " → " + pending.MaxVoltageMv + " mV");
            if (applied.TempLimitC != pending.TempLimitC) list.Add("Hotspot limit " + applied.TempLimitC + " → " + pending.TempLimitC + " °C");
            return list.ToArray();
        }

        /// <summary>One line for the window, or "" when nothing is waiting to be applied.</summary>
        public static string Summary(Settings applied, Settings pending)
        {
            string[] changes = Describe(applied, pending);
            return changes.Length == 0 ? "" : "Not applied yet: " + string.Join("  ·  ", changes);
        }
    }

    /// <summary>The test lengths on offer.</summary>
    public static class Durations
    {
        public static readonly int[] Options = { 20, 60, 180, 600 };
        public const int Default = 20;

        public static int Normalize(int seconds) { return Array.IndexOf(Options, seconds) >= 0 ? seconds : Default; }
        public static string Label(int seconds)
        {
            if (seconds < 60) return seconds + " seconds";
            return seconds == 60 ? "1 minute" : seconds / 60 + " minutes";
        }
        public static string ButtonText(int seconds) { return "Test for " + Label(seconds); }
        /// <summary>"3:05" for 185 seconds.</summary>
        public static string Clock(int seconds) { seconds = Math.Max(0, seconds); return seconds / 60 + ":" + (seconds % 60).ToString("00"); }
    }

    /// <summary>
    /// Reads and edits a "PowerPlay" table in the Navi 1x layout (Radeon RX 5000 series): the block the AMD driver
    /// loads from the registry value PP_PhmSoftPowerPlayTable. Layout follows the public Linux amdgpu headers
    /// (smu_v11_0_pptable.h / smu11_driver_if_navi10.h). Only four fields are ever written; each was measured
    /// on real hardware to change the GPU's behaviour. Anything that is not this exact layout is refused.
    /// </summary>
    public static class PowerTable
    {
        const int MinLength = 1024;
        const int DriverPartSize = 482;        // where the firmware (SMC) part starts in this layout
        const int OffPowerAc = 494;            // SocketPowerLimitAc[0], W
        const int OffPowerDc = 510;            // SocketPowerLimitDc[0], W
        const int OffHotspot = 536;            // ThotspotLimit, C
        const int OffMinVolt = 586;            // MinVoltageGfx, mV x4
        const int OffMaxVolt = 590;            // MaxVoltageGfx, mV x4
        const int OffMinClock = 814;           // FreqTableGfx[0], MHz
        const int OffMaxClock = 816;           // FreqTableGfx[1], MHz

        static int U16(byte[] t, int off) { return BitConverter.ToUInt16(t, off); }
        static void Put(byte[] t, int off, int value) { BitConverter.GetBytes((ushort)value).CopyTo(t, off); }
        static void Require(bool ok, string what) { if (!ok) throw new TableFormatException("This is not a power table " + App.Name + " understands (" + what + ")."); }

        /// <summary>Throws unless the table has the exact layout and plausible values this tool was built for.</summary>
        public static void Check(byte[] t)
        {
            Require(t != null, "no table");
            Require(t.Length >= MinLength, "only " + t.Length + " bytes");
            Require(U16(t, 0) == t.Length, "its size field does not match its length");
            Require(t[2] == 12 && t[3] == 0, "format " + t[2] + "." + t[3] + ", only 12.0 is supported");
            Require(U16(t, 5) == DriverPartSize, "unexpected driver part size");
            Require(U16(t, OffMinClock) >= 100 && U16(t, OffMinClock) <= 1000, "minimum clock");
            Require(U16(t, OffMaxClock) >= 350 && U16(t, OffMaxClock) <= 2500, "maximum clock");
            Require(U16(t, OffMaxClock) > U16(t, OffMinClock), "maximum clock below minimum");
            Require(U16(t, OffPowerAc) >= 10 && U16(t, OffPowerAc) <= 300, "power limit");
            Require(U16(t, OffPowerDc) <= 300, "battery power limit");
            Require(U16(t, OffMaxVolt) >= 1700 && U16(t, OffMaxVolt) <= 5200, "maximum voltage");
            Require(U16(t, OffMinVolt) >= 1600 && U16(t, OffMinVolt) <= U16(t, OffMaxVolt), "minimum voltage");
            Require(U16(t, OffHotspot) >= 50 && U16(t, OffHotspot) <= 120, "hotspot limit");
        }

        public static Settings Read(byte[] t)
        {
            Check(t);
            return new Settings(U16(t, OffMaxClock), U16(t, OffPowerAc), U16(t, OffMaxVolt) / 4, U16(t, OffHotspot));
        }

        public static int MinVoltageMv(byte[] t) { Check(t); return U16(t, OffMinVolt) / 4; }
        public static int MinClockMHz(byte[] t) { Check(t); return U16(t, OffMinClock); }

        /// <summary>
        /// Returns a copy of the GPU's reference table with the settings written in. Never touches the input.
        /// The settings must be inside the limits that this same reference table implies.
        /// </summary>
        public static byte[] Apply(byte[] reference, Settings s)
        {
            Limits limits = Limits.Hard(reference);   // the UI narrows this further to the range chosen in Settings
            List<string> problems = limits.Validate(s);
            if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems.ToArray()));

            byte[] t = (byte[])reference.Clone();
            Put(t, OffMaxClock, s.MaxClockMHz);
            Put(t, OffPowerAc, s.PowerLimitW);
            Put(t, OffPowerDc, Math.Min(U16(t, OffPowerDc), s.PowerLimitW));          // battery limit follows down, never up
            if (s.MaxVoltageMv != limits.Factory.MaxVoltageMv) Put(t, OffMaxVolt, s.MaxVoltageMv * 4);   // else keep the exact quarter-millivolts
            Put(t, OffHotspot, s.TempLimitC);
            return t;
        }
    }

    /// <summary>
    /// Where each slider starts and ends on one GPU, as chosen in Settings. Only the lower ends of power, voltage
    /// and hotspot can be chosen: their upper end is always the factory value.
    /// </summary>
    public class Ranges
    {
        public int ClockMin, ClockMax, PowerMin, VoltMin, TempMin;

        public Ranges(int clockMin, int clockMax, int powerMin, int voltMin, int tempMin)
        { ClockMin = clockMin; ClockMax = clockMax; PowerMin = powerMin; VoltMin = voltMin; TempMin = tempMin; }

        public string Format()
        {
            return "# " + App.Name + " slider and calibration ranges\r\nclock_min=" + ClockMin + "\r\nclock_max=" + ClockMax
                 + "\r\npower_min=" + PowerMin + "\r\nvoltage_min=" + VoltMin + "\r\nhotspot_min=" + TempMin + "\r\n";
        }

        public static Ranges Parse(string text)
        {
            Dictionary<string, int> v = new Dictionary<string, int>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('='), n;
                if (eq > 0 && !line.StartsWith("#") && int.TryParse(line.Substring(eq + 1).Trim(), out n)) v[line.Substring(0, eq).Trim()] = n;
            }
            foreach (string key in new string[] { "clock_min", "clock_max", "power_min", "voltage_min", "hotspot_min" })
                if (!v.ContainsKey(key)) throw new FormatException("Ranges file is missing '" + key + "'.");
            return new Ranges(v["clock_min"], v["clock_max"], v["power_min"], v["voltage_min"], v["hotspot_min"]);
        }
    }

    /// <summary>
    /// The range each setting may move in on one GPU, derived from that GPU's own reference table.
    /// Three layers: Hard (the widest that is ever allowed), the defaults, and a range chosen in Settings, which
    /// is always pulled inside Hard. In every layer power, voltage and hotspot can only go DOWN from the
    /// reference; only the clock may go above it, because the voltage ceiling stays where the maker put it.
    /// </summary>
    public class Limits
    {
        public const int ClockStep = 10, VoltStep = 2;
        public readonly Settings Factory;
        public readonly int ClockMin, ClockMax, PowerMin, PowerMax, VoltMin, VoltMax, TempMin, TempMax;

        Limits(Settings factory, int clockMin, int clockMax, int powerMin, int voltMin, int tempMin)
        {
            Factory = factory.Clone();
            ClockMin = clockMin; ClockMax = clockMax; PowerMin = powerMin; VoltMin = voltMin; TempMin = tempMin;
            PowerMax = factory.PowerLimitW; VoltMax = factory.MaxVoltageMv; TempMax = factory.TempLimitC;
        }

        static int RoundTo(double value, int step) { return (int)Math.Round(value / step) * step; }
        static int Within(int value, int low, int high) { return Math.Max(low, Math.Min(high, value)); }
        /// <summary>Nudges a voltage floor so the slider's 2 mV steps land exactly on the factory value.</summary>
        static int OnGrid(int voltMax, int voltMin) { return (voltMax - voltMin) % VoltStep != 0 ? voltMin + 1 : voltMin; }

        /// <summary>The default range: clock 50% to +8%, power from 35%, voltage down 160 mV, hotspot from 70 C.</summary>
        public static Limits For(byte[] reference)
        {
            Settings f = PowerTable.Read(reference);
            int voltMin = Math.Min(f.MaxVoltageMv, Math.Max(PowerTable.MinVoltageMv(reference) + 50, f.MaxVoltageMv - 160));
            return new Limits(f, f.MaxClockMHz - RoundTo(f.MaxClockMHz * 0.5, ClockStep), f.MaxClockMHz + f.MaxClockMHz * 8 / 100 / ClockStep * ClockStep,
                              (f.PowerLimitW * 35 + 99) / 100, OnGrid(f.MaxVoltageMv, voltMin), Math.Min(70, f.TempLimitC - 10));
        }

        /// <summary>The widest range ever allowed: clock from just above the table's minimum to +20%, the rest down to near the table's own floors.</summary>
        public static Limits Hard(byte[] reference)
        {
            Settings f = PowerTable.Read(reference);
            int clockFloor = Math.Min(f.MaxClockMHz - ClockStep, PowerTable.MinClockMHz(reference) + 100);
            int voltMin = Math.Min(f.MaxVoltageMv, PowerTable.MinVoltageMv(reference) + 25);
            return new Limits(f, f.MaxClockMHz - (f.MaxClockMHz - clockFloor) / ClockStep * ClockStep, f.MaxClockMHz + f.MaxClockMHz * 20 / 100 / ClockStep * ClockStep,
                              Math.Max(10, (f.PowerLimitW * 20 + 99) / 100), OnGrid(f.MaxVoltageMv, voltMin), Math.Min(60, f.TempLimitC - 10));
        }

        /// <summary>A range chosen in Settings: pulled inside Hard, snapped to the slider steps, and always containing factory.</summary>
        public static Limits For(byte[] reference, Ranges r)
        {
            Limits h = Hard(reference);
            Settings f = h.Factory;
            int clockMin = Within(r.ClockMin, h.ClockMin, f.MaxClockMHz - ClockStep), clockMax = Within(r.ClockMax, f.MaxClockMHz, h.ClockMax);
            return new Limits(f, f.MaxClockMHz - (f.MaxClockMHz - clockMin) / ClockStep * ClockStep, f.MaxClockMHz + (clockMax - f.MaxClockMHz) / ClockStep * ClockStep,
                              Within(r.PowerMin, h.PowerMin, f.PowerLimitW - 1), OnGrid(f.MaxVoltageMv, Within(r.VoltMin, h.VoltMin, f.MaxVoltageMv - VoltStep)),
                              Within(r.TempMin, h.TempMin, f.TempLimitC - 1));
        }

        public Ranges ToRanges() { return new Ranges(ClockMin, ClockMax, PowerMin, VoltMin, TempMin); }

        public List<string> Validate(Settings s)
        {
            List<string> problems = new List<string>();
            if (s.MaxClockMHz < ClockMin || s.MaxClockMHz > ClockMax) problems.Add("Top speed must be " + ClockMin + "-" + ClockMax + " MHz (got " + s.MaxClockMHz + ").");
            if (s.PowerLimitW < PowerMin || s.PowerLimitW > PowerMax) problems.Add("Power limit must be " + PowerMin + "-" + PowerMax + " W (got " + s.PowerLimitW + ").");
            if (s.MaxVoltageMv < VoltMin || s.MaxVoltageMv > VoltMax) problems.Add("Voltage ceiling must be " + VoltMin + "-" + VoltMax + " mV (got " + s.MaxVoltageMv + ").");
            if (s.TempLimitC < TempMin || s.TempLimitC > TempMax) problems.Add("Hotspot limit must be " + TempMin + "-" + TempMax + " C (got " + s.TempLimitC + ").");
            return problems;
        }

        public Settings Clamp(Settings s)
        {
            return new Settings(Within(s.MaxClockMHz, ClockMin, ClockMax), Within(s.PowerLimitW, PowerMin, PowerMax),
                                Within(s.MaxVoltageMv, VoltMin, VoltMax), Within(s.TempLimitC, TempMin, TempMax));
        }
    }

    /// <summary>The newest sensor readings, for the graphs.</summary>
    public class History
    {
        readonly Reading[] ring;
        int next, count;

        public History(int capacity) { ring = new Reading[Math.Max(1, capacity)]; }
        public int Count { get { return count; } }
        public void Add(Reading r) { ring[next] = r; next = (next + 1) % ring.Length; if (count < ring.Length) count++; }

        /// <summary>One value per reading, oldest first, for at most the last N readings.</summary>
        public int[] Series(Func<Reading, int> pick, int lastN)
        {
            int n = Math.Max(0, Math.Min(lastN, count));
            int[] values = new int[n];
            for (int i = 0; i < n; i++) values[i] = pick(ring[((next - n + i) % ring.Length + ring.Length) % ring.Length]);
            return values;
        }
    }

    /// <summary>The numbers printed on a graph, and the scale it is drawn on.</summary>
    public struct SeriesStats
    {
        public int Min, Max, Avg, Last;
        public bool Any;

        public static SeriesStats Of(int[] v)
        {
            SeriesStats s = new SeriesStats();
            if (v == null || v.Length == 0) return s;
            long sum = 0; s.Min = int.MaxValue; s.Max = int.MinValue;
            foreach (int x in v) { sum += x; if (x < s.Min) s.Min = x; if (x > s.Max) s.Max = x; }
            s.Avg = (int)Math.Round(sum / (double)v.Length); s.Last = v[v.Length - 1]; s.Any = true;
            return s;
        }

        /// <summary>Lowest and highest value of the vertical axis: both series and the ceiling line, plus a tenth of air.</summary>
        public static void Scale(int[] a, int[] b, int ceiling, out int lo, out int hi)
        {
            lo = int.MaxValue; hi = int.MinValue;
            foreach (int[] series in new int[][] { a, b })
                if (series != null) foreach (int x in series) { if (x < lo) lo = x; if (x > hi) hi = x; }
            if (ceiling >= 0) { if (ceiling < lo) lo = ceiling; if (ceiling > hi) hi = ceiling; }
            if (lo > hi) { lo = 0; hi = 1; }
            int air = Math.Max(2, (hi - lo) / 10);
            lo = Math.Max(0, lo - air); hi = hi + air;
        }
    }

    /// <summary>The accent colours on offer. Copper is the original: heat pipes are what limit a laptop GPU.</summary>
    public static class Accents
    {
        public static readonly string[] Names = { "Copper", "Cobalt", "Moss", "Violet", "Crimson", "Graphite" };
        static readonly int[] Light = { 0xB5532A, 0x2F5FD0, 0x3F7D3A, 0x7446B8, 0xB8324A, 0x4A5563 };
        static readonly int[] Dark = { 0xDC7A49, 0x6C96F2, 0x7CC06F, 0xA98AE8, 0xE86A80, 0xA9B4C2 };

        public static string Normalize(string name)
        {
            foreach (string n in Names) if (string.Equals(n, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return n;
            return Names[0];
        }
        /// <summary>0xRRGGBB for the accent in the light or the dark theme.</summary>
        public static int Rgb(string name, bool dark)
        {
            int i = Array.IndexOf(Names, Normalize(name));
            return dark ? Dark[i] : Light[i];
        }
    }

    /// <summary>What is remembered between runs (prefs.txt next to the exe). Anything unreadable falls back to its default.</summary>
    public class Prefs
    {
        public static readonly int[] HistoryOptions = { 60, 300, 900 };

        public int TestSeconds = Durations.Default;
        public string Theme = "system";          // system, light or dark
        public string Accent = Accents.Names[0];
        public bool Monitor = true;              // show the graphs beside the controls
        public int HistorySeconds = 300;         // how far back the graphs look
        public bool GraphSpeed = true, GraphVoltage = true, GraphPower = true, GraphTemp = true;

        public string Format()
        {
            return "# " + App.Name + " preferences\r\ntest_seconds=" + TestSeconds + "\r\ntheme=" + Theme + "\r\naccent=" + Accent
                 + "\r\nmonitor=" + (Monitor ? 1 : 0) + "\r\nhistory_seconds=" + HistorySeconds
                 + "\r\ngraph_speed=" + (GraphSpeed ? 1 : 0) + "\r\ngraph_voltage=" + (GraphVoltage ? 1 : 0)
                 + "\r\ngraph_power=" + (GraphPower ? 1 : 0) + "\r\ngraph_temp=" + (GraphTemp ? 1 : 0) + "\r\n";
        }

        public static Prefs Parse(string text)
        {
            Prefs p = new Prefs();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 1 || line.StartsWith("#")) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                int n; bool isNumber = int.TryParse(value, out n);
                if (key == "test_seconds" && isNumber) p.TestSeconds = Durations.Normalize(n);
                else if (key == "theme" && (value == "system" || value == "light" || value == "dark")) p.Theme = value;
                else if (key == "accent") p.Accent = Accents.Normalize(value);
                else if (key == "monitor" && isNumber) p.Monitor = n != 0;
                else if (key == "history_seconds" && isNumber && Array.IndexOf(HistoryOptions, n) >= 0) p.HistorySeconds = n;
                else if (key == "graph_speed" && isNumber) p.GraphSpeed = n != 0;
                else if (key == "graph_voltage" && isNumber) p.GraphVoltage = n != 0;
                else if (key == "graph_power" && isNumber) p.GraphPower = n != 0;
                else if (key == "graph_temp" && isNumber) p.GraphTemp = n != 0;
            }
            return p;
        }
    }

    /// <summary>Finds the power table inside a vBIOS file (for example one saved with GPU-Z).</summary>
    public static class Vbios
    {
        const int PowerPlayIndex = 15;   // "powerplayinfo" in atom_master_list_of_data_tables_v2_1

        static void Require(bool ok, string what) { if (!ok) throw new TableFormatException("This file is not a vBIOS " + App.Name + " can read (" + what + ")."); }

        public static byte[] ExtractPowerTable(byte[] file)
        {
            Require(file != null && file.Length >= 64, "too small");
            try { PowerTable.Check(file); return (byte[])file.Clone(); }   // already a bare table
            catch (TableFormatException) { }

            Require(file.Length >= 0x4A, "too small");
            int header = BitConverter.ToUInt16(file, 0x48);
            Require(header + 34 <= file.Length, "header pointer");
            Require(file[header + 4] == 'A' && file[header + 5] == 'T' && file[header + 6] == 'O' && file[header + 7] == 'M', "no ATOM signature");
            int master = BitConverter.ToUInt16(file, header + 32);
            Require(master + 4 + PowerPlayIndex * 2 + 2 <= file.Length, "data table pointer");
            int start = BitConverter.ToUInt16(file, master + 4 + PowerPlayIndex * 2);
            Require(start > 0 && start + 2 <= file.Length, "no power table");
            int size = BitConverter.ToUInt16(file, start);
            Require(size > 0 && start + size <= file.Length, "the power table runs past the end of the file");
            byte[] table = new byte[size];
            Array.Copy(file, start, table, 0, size);
            PowerTable.Check(table);
            return table;
        }
    }

    /// <summary>Pure helpers about device identity (Windows instance IDs like PCI\VEN_1002&amp;DEV_7340&amp;SUBSYS_130E1462...).</summary>
    public static class Hardware
    {
        static readonly Regex Id = new Regex(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{8})", RegexOptions.IgnoreCase);
        /// <summary>Models this tool has been measured on. Everything else with the same table format is "untested".</summary>
        static readonly string[] Tested = { "VEN_1002_DEV_7340_SUBSYS_130E1462" };   // RX 5500M in MSI Bravo 15 B5DD

        public static string TableKey(string instanceId)
        {
            Match m = Id.Match(instanceId ?? "");
            if (!m.Success) throw new ArgumentException("Not a PCI graphics device: " + instanceId);
            return ("VEN_" + m.Groups[1].Value + "_DEV_" + m.Groups[2].Value + "_SUBSYS_" + m.Groups[3].Value).ToUpperInvariant();
        }
        public static bool IsTested(string instanceId)
        {
            Match m = Id.Match(instanceId ?? "");
            return m.Success && Array.IndexOf(Tested, TableKey(instanceId)) >= 0;
        }
        public static bool IsAmd(string instanceId) { return (instanceId ?? "").ToUpperInvariant().Contains("VEN_1002"); }
    }

    public class Preset
    {
        public readonly string Name, Summary;
        public readonly Settings Settings;
        public Preset(string name, string summary, Settings settings) { Name = name; Summary = summary; Settings = settings; }
    }

    /// <summary>Starting points, scaled from each GPU's own factory values.</summary>
    public static class Presets
    {
        static int RoundTo(double value, int step) { return (int)Math.Round(value / step) * step; }

        public static Preset[] For(Limits l) { return For(l, null); }

        /// <summary>With a calibration, Efficient and Boost use what was measured on this GPU instead of rules of thumb.</summary>
        public static Preset[] For(Limits l, CalibrationResult measured)
        {
            Settings f = l.Factory;
            Settings cool = new Settings(f.MaxClockMHz - RoundTo(f.MaxClockMHz * 0.2, Limits.ClockStep), (int)Math.Round(f.PowerLimitW * 0.64), f.MaxVoltageMv, f.TempLimitC - 15);
            Settings efficient = new Settings(f.MaxClockMHz, (int)Math.Round(f.PowerLimitW * 0.86), f.MaxVoltageMv - 62, f.TempLimitC - 5);
            Settings boost = new Settings(f.MaxClockMHz + RoundTo(f.MaxClockMHz * 0.0533, Limits.ClockStep), f.PowerLimitW, f.MaxVoltageMv, f.TempLimitC);
            string efficientText = "Nearly full speed on a lower voltage ceiling.", boostText = "About 5% faster. Runs hotter - test it.";
            if (measured != null && measured.Ok)
            {
                efficient.MaxVoltageMv = measured.EfficientVoltage;
                boost.MaxClockMHz = measured.RecommendedClock;
                efficientText = measured.EfficientVoltage < f.MaxVoltageMv
                    ? "Lowest voltage ceiling that kept full speed in calibration."
                    : "Calibration found no lower voltage ceiling that keeps full speed.";
                int gain = (int)Math.Round((measured.RecommendedClock - f.MaxClockMHz) * 100.0 / f.MaxClockMHz);
                boostText = measured.RecommendedClock > f.MaxClockMHz
                    ? "Fastest speed calibration trusts on this GPU (+" + gain + "%). Runs hotter."
                    : "Calibration found no stable speed above factory on this GPU.";
            }
            return new Preset[]
            {
                new Preset("Factory", "How the GPU shipped. Removes every tweak.", f.Clone()),
                new Preset("Cool", "About 20% slower, far less heat and fan noise.", l.Clamp(cool)),
                new Preset("Efficient", efficientText, l.Clamp(efficient)),
                new Preset("Boost", boostText, l.Clamp(boost)),
            };
        }

        public static Preset Find(Preset[] presets, Settings s)
        {
            foreach (Preset p in presets) if (p.Settings.Equals(s)) return p;
            return null;
        }

        public static Preset ByName(Preset[] presets, string name)
        {
            if (name == null) return null;
            foreach (Preset p in presets) if (string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }

    /// <summary>Saved profiles are tiny text files: clock= / power= / voltage= / temp=.</summary>
    public static class ProfileText
    {
        public static string Format(Settings s)
        {
            return "# " + App.Name + " profile\r\nclock=" + s.MaxClockMHz + "\r\npower=" + s.PowerLimitW + "\r\nvoltage=" + s.MaxVoltageMv + "\r\ntemp=" + s.TempLimitC + "\r\n";
        }

        public static Settings Parse(string text, Limits limits)
        {
            Dictionary<string, int> v = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 1) throw new FormatException("Unreadable line in profile: " + line);
                int n;
                if (!int.TryParse(line.Substring(eq + 1).Trim(), out n)) throw new FormatException("Not a number in profile: " + line);
                v[line.Substring(0, eq).Trim()] = n;
            }
            foreach (string key in new string[] { "clock", "power", "voltage", "temp" })
                if (!v.ContainsKey(key)) throw new FormatException("Profile is missing '" + key + "'.");
            Settings s = new Settings(v["clock"], v["power"], v["voltage"], v["temp"]);
            List<string> problems = limits.Validate(s);
            if (problems.Count > 0) throw new FormatException("Profile is outside this GPU's limits. " + string.Join(" ", problems.ToArray()));
            return s;
        }

        public static string SafeName(string name)
        {
            string n = (name ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            if (n.Length > 40) n = n.Substring(0, 40).Trim();
            if (n.Length == 0) throw new ArgumentException("Give the profile a name.");
            return n;
        }
    }

    /// <summary>One plain-language line per control, shown under each slider.</summary>
    public static class Advice
    {
        static int Pct(int value, int factory) { return (int)Math.Round(Math.Abs(value - factory) * 100.0 / factory); }

        public static string Clock(int mhz, Limits l)
        {
            int f = l.Factory.MaxClockMHz;
            if (mhz == f) return "Factory speed.";
            if (mhz < f) return Pct(mhz, f) + "% slower than factory - cooler and quieter.";
            return Pct(mhz, f) + "% overclock - runs hotter and can crash games. Test it.";
        }
        public static string Power(int w, Limits l)
        {
            if (w >= l.Factory.PowerLimitW) return "Factory limit.";
            return "The GPU slows itself to stay under " + w + " W (factory " + l.Factory.PowerLimitW + " W).";
        }
        public static string Voltage(int mv, Limits l)
        {
            int f = l.Factory.MaxVoltageMv;
            if (mv >= f) return "Factory ceiling.";
            return (f - mv) + " mV below factory - less heat, slightly slower. Too low can crash.";
        }
        public static string Temp(int c, Limits l)
        {
            if (c >= l.Factory.TempLimitC) return "Factory limit.";
            return "The GPU slows itself to stay under " + c + " °C.";
        }
        public static bool IsOverclock(Settings s, Limits l) { return s.MaxClockMHz > l.Factory.MaxClockMHz; }
    }

    /// <summary>One snapshot of the GPU's sensors.</summary>
    public class Reading
    {
        public readonly int ClockMHz, VoltageMv, PowerW, EdgeC, HotspotC, ActivityPct;
        public Reading(int clock, int volt, int power, int edge, int hotspot, int activity)
        { ClockMHz = clock; VoltageMv = volt; PowerW = power; EdgeC = edge; HotspotC = hotspot; ActivityPct = activity; }
    }

    /// <summary>Outcome of a load test. Travels from the helper process to the window as one "RESULT ..." line.</summary>
    public class LoadResult
    {
        public bool Ok;
        public string Error = "";
        public int AvgClock, PeakVoltage, AvgPower, PeakPower, PeakHotspot, PeakEdge, Samples;

        static string Clean(string s) { return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim(); }

        public static LoadResult Failed(string error)
        {
            LoadResult r = new LoadResult(); r.Ok = false; r.Error = Clean(error); return r;
        }

        /// <summary>Averages skip the first quarter (ramp-up); the peak temperatures count every reading.</summary>
        public static LoadResult Summarize(IList<Reading> readings)
        {
            if (readings == null || readings.Count < 4) return Failed("Not enough sensor readings were collected.");
            LoadResult r = new LoadResult(); r.Ok = true; r.Samples = readings.Count;
            int start = readings.Count / 4, n = 0; long clock = 0, power = 0;
            for (int i = 0; i < readings.Count; i++)
            {
                Reading x = readings[i];
                r.PeakHotspot = Math.Max(r.PeakHotspot, x.HotspotC);
                r.PeakEdge = Math.Max(r.PeakEdge, x.EdgeC);
                if (i < start) continue;
                clock += x.ClockMHz; power += x.PowerW; n++;
                r.PeakVoltage = Math.Max(r.PeakVoltage, x.VoltageMv);
                r.PeakPower = Math.Max(r.PeakPower, x.PowerW);
            }
            r.AvgClock = (int)(clock / n); r.AvgPower = (int)(power / n);
            return r;
        }

        public string ToLine()
        {
            if (!Ok) return "RESULT ok=0;error=" + Clean(Error);
            return "RESULT ok=1;clock=" + AvgClock + ";volt=" + PeakVoltage + ";power=" + AvgPower + ";peakpower=" + PeakPower
                 + ";hot=" + PeakHotspot + ";edge=" + PeakEdge + ";samples=" + Samples;
        }

        public static LoadResult Parse(string output)
        {
            string line = null;
            foreach (string raw in (output ?? "").Split('\n')) { string l = raw.Trim(); if (l.StartsWith("RESULT ")) line = l; }
            if (line == null)
            {
                string tail = Clean(output);
                if (tail.Length > 200) tail = tail.Substring(tail.Length - 200);
                return Failed("The test stopped before reporting a result." + (tail.Length > 0 ? " Last output: " + tail : ""));
            }
            Dictionary<string, string> kv = new Dictionary<string, string>();
            foreach (string part in line.Substring(7).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) kv[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            string ok; kv.TryGetValue("ok", out ok);
            if (ok != "1")
            {
                string e; kv.TryGetValue("error", out e);
                return Failed(string.IsNullOrEmpty(e) ? "The test failed." : e);
            }
            LoadResult r = new LoadResult(); r.Ok = true;
            r.AvgClock = Num(kv, "clock"); r.PeakVoltage = Num(kv, "volt"); r.AvgPower = Num(kv, "power"); r.PeakPower = Num(kv, "peakpower");
            r.PeakHotspot = Num(kv, "hot"); r.PeakEdge = Num(kv, "edge"); r.Samples = Num(kv, "samples");
            return r;
        }

        static int Num(Dictionary<string, string> kv, string key)
        {
            string v; int n;
            return kv.TryGetValue(key, out v) && int.TryParse(v, out n) ? n : 0;
        }

        public string Describe()
        {
            return AvgClock + " MHz · " + PeakVoltage + " mV · " + AvgPower + " W · peak " + PeakHotspot + " °C";
        }

        /// <summary>"+80 MHz", "-18 W" or "no change": how a number moved since the previous test.</summary>
        public static string Delta(int now, int before, string unit)
        {
            if (now == before) return "no change";
            return (now > before ? "+" : "-") + Math.Abs(now - before) + " " + unit;
        }
    }

    /// <summary>What a calibration run found out about one GPU. Stored next to its reference table.</summary>
    public class CalibrationResult
    {
        public bool Ok;
        public string Error = "";
        public LoadResult Baseline = new LoadResult();   // the GPU under load on factory settings
        public int MaxStableClock;        // highest top speed that passed and was worth having
        public int RecommendedClock;      // one step lower when a higher speed failed
        public int LowestStableVoltage;   // lowest voltage ceiling that passed
        public int EfficientVoltage;      // lowest voltage ceiling that also kept speed within 3% of factory
        public string Date = "";
        public string ClockStop = "", VoltageStop = "";   // why each search ended, e.g. "1560 MHz reached the hotspot limit"

        public static CalibrationResult Failed(string error) { CalibrationResult r = new CalibrationResult(); r.Error = error; return r; }

        public string ToText()
        {
            return "# " + App.Name + " calibration\r\ndate=" + Date + "\r\nmax_stable_clock=" + MaxStableClock + "\r\nrecommended_clock=" + RecommendedClock
                 + "\r\nlowest_stable_voltage=" + LowestStableVoltage + "\r\nefficient_voltage=" + EfficientVoltage
                 + "\r\nclock_stop=" + ClockStop + "\r\nvoltage_stop=" + VoltageStop + "\r\nbaseline=" + Baseline.ToLine() + "\r\n";
        }

        public static CalibrationResult Parse(string text)
        {
            Dictionary<string, string> kv = new Dictionary<string, string>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith("#") || eq < 1) continue;
                kv[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            CalibrationResult r = new CalibrationResult();
            r.MaxStableClock = Need(kv, "max_stable_clock"); r.RecommendedClock = Need(kv, "recommended_clock");
            r.LowestStableVoltage = Need(kv, "lowest_stable_voltage"); r.EfficientVoltage = Need(kv, "efficient_voltage");
            string b; if (!kv.TryGetValue("baseline", out b)) throw new FormatException("Calibration file is missing 'baseline'.");
            r.Baseline = LoadResult.Parse(b);
            string optional;
            if (kv.TryGetValue("date", out optional)) r.Date = optional;
            if (kv.TryGetValue("clock_stop", out optional)) r.ClockStop = optional;
            if (kv.TryGetValue("voltage_stop", out optional)) r.VoltageStop = optional;
            r.Ok = true;
            return r;
        }

        static int Need(Dictionary<string, string> kv, string key)
        {
            string v; int n;
            if (!kv.TryGetValue(key, out v) || !int.TryParse(v, out n)) throw new FormatException("Calibration file is missing '" + key + "'.");
            return n;
        }

        public string Describe(Settings factory)
        {
            return "Speed: good up to " + MaxStableClock + " MHz, factory " + factory.MaxClockMHz + (ClockStop.Length > 0 ? " (" + ClockStop + ")" : "") + "; Boost is " + RecommendedClock + " MHz. "
                 + "Voltage ceiling: passed down to " + LowestStableVoltage + " mV" + (VoltageStop.Length > 0 ? " (" + VoltageStop + ")" : "") + "; full speed held down to " + EfficientVoltage + " mV.";
        }
    }

    /// <summary>
    /// The calibration procedure, independent of any hardware: it is handed a "measure these settings" function
    /// (apply, restart, load test) and decides what to try next. One setting is changed at a time, everything
    /// stays inside the GPU's built-in limits, and the search stops at the first failure.
    /// </summary>
    public static class Calibration
    {
        public const int VoltageStepMv = 24;
        public const int SpeedLossAllowedPct = 3;

        public static int ClockStepFor(Limits l) { return Math.Max(Limits.ClockStep, (int)Math.Round(l.Factory.MaxClockMHz * 0.02 / Limits.ClockStep) * Limits.ClockStep); }

        /// <summary>How many measurements a full run can take at most (for progress and time estimates).</summary>
        public static int MaxSteps(Limits l)
        {
            return 1 + (l.VoltMax - l.VoltMin) / VoltageStepMv + (l.ClockMax - l.Factory.MaxClockMHz) / ClockStepFor(l);
        }

        public static CalibrationResult Run(Limits limits, Func<Settings, LoadResult> measure, Action<string> progress, Func<bool> cancelled)
        {
            Settings f = limits.Factory;
            CalibrationResult result = new CalibrationResult();
            result.Date = DateTime.Now.ToString("yyyy-MM-dd");

            if (cancelled()) return CalibrationResult.Failed("Stopped.");
            progress("Measuring factory settings");
            result.Baseline = measure(f.Clone());
            if (!result.Baseline.Ok) return CalibrationResult.Failed("The GPU did not pass the test on its factory settings (" + result.Baseline.Error + "), so nothing else was tried.");
            int baseSpeed = result.Baseline.AvgClock;

            // 1. voltage ceiling, downwards
            result.LowestStableVoltage = result.EfficientVoltage = f.MaxVoltageMv;
            result.VoltageStop = result.ClockStop = "the built-in limit was reached";
            for (int v = f.MaxVoltageMv - VoltageStepMv; v >= limits.VoltMin; v -= VoltageStepMv)
            {
                if (cancelled()) return CalibrationResult.Failed("Stopped.");
                progress("Voltage ceiling " + v + " mV");
                LoadResult r = measure(new Settings(f.MaxClockMHz, f.PowerLimitW, v, f.TempLimitC));
                if (!r.Ok) { result.VoltageStop = v + " mV failed the test"; break; }
                result.LowestStableVoltage = v;
                if (r.AvgClock * 100 < baseSpeed * (100 - SpeedLossAllowedPct))          // passes, but too slow to be "efficient"
                { result.VoltageStop = v + " mV was more than " + SpeedLossAllowedPct + "% slower"; break; }
                result.EfficientVoltage = v;
            }

            // 2. top speed, upwards
            int step = ClockStepFor(limits), bestSpeed = baseSpeed;
            bool sawFailure = false;
            result.MaxStableClock = f.MaxClockMHz;
            for (int c = f.MaxClockMHz + step; c <= limits.ClockMax; c += step)
            {
                if (cancelled()) return CalibrationResult.Failed("Stopped.");
                progress("Top speed " + c + " MHz");
                LoadResult r = measure(new Settings(c, f.PowerLimitW, f.MaxVoltageMv, f.TempLimitC));
                if (!r.Ok) { sawFailure = true; result.ClockStop = c + " MHz failed the test"; break; }
                if (r.PeakHotspot >= f.TempLimitC - 2) { result.ClockStop = c + " MHz reached the hotspot limit"; break; }   // it only throttles from here
                if (r.AvgClock <= bestSpeed + step / 4) { result.ClockStop = c + " MHz was no faster"; break; }                  // more clock, no more speed
                result.MaxStableClock = c; bestSpeed = r.AvgClock;
            }
            result.RecommendedClock = sawFailure ? Math.Max(f.MaxClockMHz, result.MaxStableClock - step) : result.MaxStableClock;
            result.Ok = true;
            return result;
        }
    }

    /// <summary>Windows names its per-process GPU counters "pid_1234_luid_0x00000000_0x0000FA28_phys_0".</summary>
    public static class GpuCounters
    {
        public static string LuidTag(long luid)
        {
            return string.Format("luid_0x{0:x8}_0x{1:x8}", (uint)((ulong)luid >> 32), (uint)((ulong)luid & 0xFFFFFFFF));
        }
        public static int PidOf(string instance)
        {
            if (instance == null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return -1;
            int end = instance.IndexOf('_', 4);
            int pid;
            return end > 4 && int.TryParse(instance.Substring(4, end - 4), out pid) ? pid : -1;
        }
        public static bool IsOnAdapter(string instance, long luid)
        {
            return instance != null && instance.ToLowerInvariant().Contains(LuidTag(luid) + "_");
        }
    }

    public enum RestartOutcome { Restarted, RebootNeeded, Failed }

    /// <summary>Meaning of pnputil's exit code after "/restart-device".</summary>
    public static class Pnp
    {
        public static RestartOutcome Interpret(int exitCode)
        {
            if (exitCode == 0) return RestartOutcome.Restarted;
            if (exitCode == 3010) return RestartOutcome.RebootNeeded;   // ERROR_SUCCESS_REBOOT_REQUIRED
            return RestartOutcome.Failed;
        }

        public static string Message(RestartOutcome outcome, string detail)
        {
            if (outcome == RestartOutcome.Restarted) return "Applied. Press \"Test for 20 seconds\" to see what the GPU does under load.";
            if (outcome == RestartOutcome.RebootNeeded) return "Saved, but Windows could not restart the GPU right now. The new settings start after you reboot.";
            return "Saved, but restarting the GPU failed (" + detail + "). Reboot to load the new settings.";
        }
    }
}
