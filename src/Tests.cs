using System;
using System.Collections.Generic;
using Tuner;

// Plain console test runner (no test framework needed). Exit code 0 = all passed.
// Every table used here is synthetic: no real firmware data lives in this repository.
static class Tests
{
    static int passed, failed;

    static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception e) { failed++; Console.WriteLine("FAIL  " + name + "\r\n      " + e.GetType().Name + ": " + e.Message); }
    }
    static void Eq(object expected, object actual, string what)
    {
        if (!object.Equals(expected, actual)) throw new Exception(what + ": expected <" + expected + "> but got <" + actual + ">");
    }
    static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
    static void Throws<T>(Action a, string what) where T : Exception
    {
        try { a(); }
        catch (T) { return; }
        catch (Exception e) { throw new Exception(what + ": expected " + typeof(T).Name + " but got " + e.GetType().Name); }
        throw new Exception(what + ": expected " + typeof(T).Name + " but nothing was thrown");
    }
    static Settings S(int clock, int power, int volt, int temp) { return new Settings(clock, power, volt, temp); }
    static ushort U16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }
    static void Put(byte[] b, int o, int v) { BitConverter.GetBytes((ushort)v).CopyTo(b, o); }

    /// <summary>A made-up table in the Navi 1x layout: 1500 MHz, 70 W, 962.5 mV (min 700), 100 C.</summary>
    static byte[] Table(int length, int clock, int power, int voltRaw, int temp)
    {
        byte[] t = new byte[length];
        Put(t, 0, length); t[2] = 12; t[3] = 0; t[4] = 1; Put(t, 5, 482);
        Put(t, 494, power); Put(t, 510, power); Put(t, 536, temp);
        Put(t, 586, 2800); Put(t, 590, voltRaw);
        Put(t, 814, 300); Put(t, 816, clock);
        for (int i = 900; i < length; i++) t[i] = (byte)(i * 7);   // filler that must survive untouched
        return t;
    }
    static byte[] Laptop() { return Table(1674, 1500, 70, 3850, 100); }
    static byte[] Desktop() { return Table(1674, 1900, 180, 4800, 110); }

    static int Main()
    {
        TableTests();
        LimitTests();
        VbiosTests();
        SupportTests();
        CalibrationTests();
        UxTests();
        RangeTests();
        MonitorTests();
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    static void TableTests()
    {
        byte[] reference = Laptop();

        Test("reading a table gives its four settings", delegate
        {
            Eq(S(1500, 70, 962, 100), PowerTable.Read(reference), "settings");
        });

        Test("applying settings changes only the bytes of those settings", delegate
        {
            byte[] t = PowerTable.Apply(reference, S(1200, 45, 900, 85));
            HashSet<int> allowed = new HashSet<int>(new int[] { 494, 495, 510, 511, 536, 537, 590, 591, 816, 817 });
            Eq(reference.Length, t.Length, "length");
            for (int i = 0; i < t.Length; i++)
                if (t[i] != reference[i] && !allowed.Contains(i)) throw new Exception("unexpected change at byte " + i);
        });

        Test("applied settings read back the same", delegate
        {
            Settings s = S(1200, 45, 900, 85);
            Eq(s, PowerTable.Read(PowerTable.Apply(reference, s)), "round trip");
        });

        Test("applied values land in the fields the driver was measured to obey", delegate
        {
            byte[] t = PowerTable.Apply(reference, S(1200, 45, 900, 85));
            Eq((ushort)1200, U16(t, 816), "max GPU clock (MHz)");
            Eq((ushort)45, U16(t, 494), "power limit on mains (W)");
            Eq((ushort)3600, U16(t, 590), "max voltage (mV x4)");
            Eq((ushort)85, U16(t, 536), "hotspot limit (C)");
        });

        Test("applying does not modify the table it was given", delegate
        {
            byte[] copy = (byte[])reference.Clone();
            PowerTable.Apply(reference, S(1200, 45, 900, 85));
            for (int i = 0; i < copy.Length; i++) if (copy[i] != reference[i]) throw new Exception("input changed at byte " + i);
        });

        Test("applying a table's own settings reproduces it exactly (the half-millivolt is kept)", delegate
        {
            byte[] t = PowerTable.Apply(reference, PowerTable.Read(reference));
            for (int i = 0; i < t.Length; i++) if (t[i] != reference[i]) throw new Exception("differs at byte " + i);
        });

        Test("lowering the power limit also lowers the on-battery limit", delegate
        {
            Eq((ushort)45, U16(PowerTable.Apply(reference, S(1500, 45, 962, 100)), 510), "battery power limit");
        });

        Test("the on-battery power limit is never raised", delegate
        {
            byte[] lowDc = Laptop(); Put(lowDc, 510, 50);
            Eq((ushort)50, U16(PowerTable.Apply(lowDc, S(1500, 60, 962, 100)), 510), "battery power limit");
        });

        Test("settings outside the GPU's hard limits are refused", delegate
        {
            Limits l = Limits.Hard(reference);
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(l.ClockMax + 10, 70, 962, 100)); }, "clock too high");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(l.ClockMin - 10, 70, 962, 100)); }, "clock too low");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, l.PowerMax + 1, 962, 100)); }, "power above factory");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, l.PowerMin - 1, 962, 100)); }, "power too low");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, 70, l.VoltMax + 1, 100)); }, "voltage above factory");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, 70, l.VoltMin - 1, 100)); }, "voltage too low");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, 70, 962, l.TempMax + 1)); }, "temperature above factory");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1500, 70, 962, l.TempMin - 1)); }, "temperature too low");
        });

        Test("a table that is missing, too short or mis-sized is refused", delegate
        {
            Throws<TableFormatException>(delegate { PowerTable.Check(null); }, "missing");
            Throws<TableFormatException>(delegate { PowerTable.Check(new byte[100]); }, "short");
            byte[] lying = Laptop(); Put(lying, 0, 1000);
            Throws<TableFormatException>(delegate { PowerTable.Check(lying); }, "size field disagrees with length");
        });

        Test("a table in another format is refused (for example RX 6000 or newer)", delegate
        {
            byte[] other = Laptop(); other[2] = 15;
            Throws<TableFormatException>(delegate { PowerTable.Check(other); }, "format revision");
            byte[] other2 = Laptop(); Put(other2, 5, 520);
            Throws<TableFormatException>(delegate { PowerTable.Check(other2); }, "driver part size");
        });

        Test("a table whose values are implausible is refused rather than edited", delegate
        {
            Throws<TableFormatException>(delegate { PowerTable.Check(new byte[1674]); }, "blank");
            byte[] bad = Laptop(); Put(bad, 816, 9000);
            Throws<TableFormatException>(delegate { PowerTable.Apply(bad, S(1500, 70, 962, 100)); }, "9000 MHz clock");
            byte[] bad2 = Laptop(); Put(bad2, 586, 4000);
            Throws<TableFormatException>(delegate { PowerTable.Check(bad2); }, "minimum voltage above maximum");
        });

        Test("a same-format table of a different length is accepted", delegate
        {
            Eq(S(1500, 70, 962, 100), PowerTable.Read(Table(1700, 1500, 70, 3850, 100)), "settings");
        });
    }

    static void LimitTests()
    {
        Test("limits are derived from the GPU's own factory values", delegate
        {
            Limits l = Limits.For(Laptop());
            Eq(S(1500, 70, 962, 100), l.Factory, "factory");
            Eq(750, l.ClockMin, "clock floor: half of factory"); Eq(1620, l.ClockMax, "clock ceiling: 8% above factory");
            Eq(25, l.PowerMin, "power floor"); Eq(70, l.PowerMax, "power ceiling");
            Eq(802, l.VoltMin, "voltage floor: 160 mV under factory"); Eq(962, l.VoltMax, "voltage ceiling");
            Eq(70, l.TempMin, "temperature floor"); Eq(100, l.TempMax, "temperature ceiling");
        });

        Test("power, voltage and temperature can never go above factory on any GPU", delegate
        {
            foreach (byte[] t in new byte[][] { Laptop(), Desktop() })
            {
                Limits l = Limits.For(t);
                Eq(l.Factory.PowerLimitW, l.PowerMax, "power"); Eq(l.Factory.MaxVoltageMv, l.VoltMax, "voltage"); Eq(l.Factory.TempLimitC, l.TempMax, "temperature");
                True(l.ClockMax <= l.Factory.MaxClockMHz * 1.08, "clock at most 8% above factory");
            }
        });

        Test("a bigger GPU gets proportionally bigger limits", delegate
        {
            Limits l = Limits.For(Desktop());
            Eq(950, l.ClockMin, "clock floor"); Eq(2050, l.ClockMax, "clock ceiling");
            Eq(63, l.PowerMin, "power floor"); Eq(1040, l.VoltMin, "voltage floor");
        });

        Test("the voltage floor stays clear of the table's own minimum voltage", delegate
        {
            byte[] t = Laptop(); Put(t, 586, 3400);   // minimum 850 mV
            Eq(900, Limits.For(t).VoltMin, "floor is 50 mV above the minimum");
        });

        Test("the slider steps reach both factory and the ends of each range", delegate
        {
            foreach (byte[] t in new byte[][] { Laptop(), Desktop() })
            {
                Limits l = Limits.For(t);
                Eq(0, (l.Factory.MaxClockMHz - l.ClockMin) % Limits.ClockStep, "clock factory"); Eq(0, (l.ClockMax - l.ClockMin) % Limits.ClockStep, "clock top");
                Eq(0, (l.VoltMax - l.VoltMin) % Limits.VoltStep, "voltage top");
            }
        });

        Test("validation names every setting that is out of range", delegate
        {
            Limits l = Limits.For(Laptop());
            Eq(0, l.Validate(l.Factory).Count, "factory is valid");
            Eq(4, l.Validate(S(5000, 500, 2000, 200)).Count, "four problems");
        });

        Test("clamping brings settings inside the limits", delegate
        {
            Limits l = Limits.For(Laptop());
            Eq(S(1620, 25, 962, 70), l.Clamp(S(1900, 5, 1200, 40)), "clamped");
        });

        Test("presets are the factory settings scaled for this GPU", delegate
        {
            Preset[] p = Presets.For(Limits.For(Laptop()));
            Eq(4, p.Length, "count");
            Eq("Factory", p[0].Name, "first"); Eq(S(1500, 70, 962, 100), p[0].Settings, "factory");
            Eq("Cool", p[1].Name, "second"); Eq(S(1200, 45, 962, 85), p[1].Settings, "cool");
            Eq("Efficient", p[2].Name, "third"); Eq(S(1500, 60, 900, 95), p[2].Settings, "efficient");
            Eq("Boost", p[3].Name, "fourth"); Eq(S(1580, 70, 962, 100), p[3].Settings, "boost");
        });

        Test("every preset is inside the limits on any GPU", delegate
        {
            foreach (byte[] t in new byte[][] { Laptop(), Desktop(), Table(1674, 800, 30, 3200, 85) })
            {
                Limits l = Limits.For(t);
                foreach (Preset p in Presets.For(l)) Eq(0, l.Validate(p.Settings).Count, p.Name + " on " + l.Factory);
            }
        });

        Test("a preset is recognised by its values and by its name in any letter case", delegate
        {
            Preset[] p = Presets.For(Limits.For(Laptop()));
            Eq("Cool", Presets.Find(p, S(1200, 45, 962, 85)).Name, "by values");
            Eq(null, Presets.Find(p, S(1234, 56, 912, 91)), "custom settings match no preset");
            Eq("Boost", Presets.ByName(p, " BOOST ").Name, "by name");
            Eq(null, Presets.ByName(p, "turbo"), "unknown name");
            Eq(null, Presets.ByName(p, null), "no name");
        });
    }

    static void VbiosTests()
    {
        byte[] table = Laptop();

        Test("the power table is found inside a vBIOS file", delegate
        {
            byte[] rom = new byte[8192];
            Put(rom, 0x48, 0x100);                                        // pointer to the ATOM header
            rom[0x104] = (byte)'A'; rom[0x105] = (byte)'T'; rom[0x106] = (byte)'O'; rom[0x107] = (byte)'M';
            Put(rom, 0x100 + 32, 0x200);                                  // master data table
            Put(rom, 0x200 + 4 + 15 * 2, 0x300);                          // entry 15: powerplay info
            table.CopyTo(rom, 0x300);
            byte[] found = Vbios.ExtractPowerTable(rom);
            Eq(table.Length, found.Length, "length");
            for (int i = 0; i < table.Length; i++) if (found[i] != table[i]) throw new Exception("differs at byte " + i);
        });

        Test("a file that already is a bare power table is accepted as it is", delegate
        {
            Eq(table.Length, Vbios.ExtractPowerTable(table).Length, "length");
        });

        Test("a file that is not a vBIOS is refused", delegate
        {
            Throws<TableFormatException>(delegate { Vbios.ExtractPowerTable(new byte[8192]); }, "blank file");
            Throws<TableFormatException>(delegate { Vbios.ExtractPowerTable(new byte[10]); }, "tiny file");
            Throws<TableFormatException>(delegate { Vbios.ExtractPowerTable(null); }, "no file");
        });

        Test("a vBIOS whose table runs past the end of the file is refused", delegate
        {
            byte[] rom = new byte[0x300 + 100];
            Put(rom, 0x48, 0x100);
            rom[0x104] = (byte)'A'; rom[0x105] = (byte)'T'; rom[0x106] = (byte)'O'; rom[0x107] = (byte)'M';
            Put(rom, 0x100 + 32, 0x200); Put(rom, 0x200 + 4 + 15 * 2, 0x300); Put(rom, 0x300, 1674);
            Throws<TableFormatException>(delegate { Vbios.ExtractPowerTable(rom); }, "truncated");
        });
    }

    /// <summary>A pretend GPU for calibration tests: you say when it fails, how fast it runs and how hot it gets.</summary>
    class FakeGpu
    {
        public int FailsAboveClock = int.MaxValue, FailsBelowVolt = 0, SpeedPlateau = int.MaxValue, HotFromClock = int.MaxValue, SlowBelowVolt = 0;
        public bool FactoryFails;
        public readonly List<Settings> Asked = new List<Settings>();

        public LoadResult Measure(Settings s)
        {
            Asked.Add(s.Clone());
            if (FactoryFails || s.MaxClockMHz > FailsAboveClock || s.MaxVoltageMv < FailsBelowVolt) return LoadResult.Failed("calculation errors");
            LoadResult r = new LoadResult(); r.Ok = true; r.Samples = 40;
            r.AvgClock = Math.Min(s.MaxClockMHz, SpeedPlateau) * 96 / 100 - Math.Max(0, SlowBelowVolt - s.MaxVoltageMv) * 2;
            r.PeakVoltage = Math.Min(s.MaxVoltageMv, 925); r.AvgPower = 55; r.PeakPower = 56;
            r.PeakHotspot = s.MaxClockMHz >= HotFromClock ? 98 : 85; r.PeakEdge = 70;
            return r;
        }
    }

    static CalibrationResult Calibrate(FakeGpu gpu, Limits limits)
    {
        return Calibration.Run(limits, gpu.Measure, delegate (string s) { }, delegate { return false; });
    }

    static void CalibrationTests()
    {
        Limits limits = Limits.For(Laptop());

        Test("calibration measures factory first and stops there if factory fails", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.FactoryFails = true;
            CalibrationResult r = Calibrate(gpu, limits);
            True(!r.Ok, "not ok");
            Eq(1, gpu.Asked.Count, "settings tried");
            Eq(limits.Factory, gpu.Asked[0], "the first thing tried is factory");
        });

        Test("the highest speed that passes is found, and the recommendation backs off one step from a failure", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.FailsAboveClock = 1580;
            CalibrationResult r = Calibrate(gpu, limits);
            True(r.Ok, "ok");
            Eq(1560, r.MaxStableClock, "1530 and 1560 pass, 1590 fails");
            Eq(1530, r.RecommendedClock, "one step below the last pass");
        });

        Test("speed is not raised further once the hotspot reaches its limit", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.HotFromClock = 1560;
            CalibrationResult r = Calibrate(gpu, limits);
            Eq(1530, r.MaxStableClock, "1560 ran into the hotspot limit");
            Eq(1530, r.RecommendedClock, "no failure was seen, so no back-off");
        });

        Test("speed is not raised further once more clock stops giving more speed", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.SpeedPlateau = 1530;
            Eq(1530, Calibrate(gpu, limits).MaxStableClock, "1560 was no faster than 1530");
        });

        Test("a GPU that passes everything is capped at the limit, never beyond", delegate
        {
            FakeGpu gpu = new FakeGpu();
            CalibrationResult r = Calibrate(gpu, limits);
            Eq(1620, r.MaxStableClock, "top of the allowed range");
            Eq(1620, r.RecommendedClock, "no failure, no back-off");
            foreach (Settings s in gpu.Asked) Eq(0, limits.Validate(s).Count, "tried " + s);
        });

        Test("the voltage ceiling is lowered until speed drops more than 3 percent", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.SlowBelowVolt = 920;      // 938 full speed, 914 -12 MHz, 890 -60 MHz
            CalibrationResult r = Calibrate(gpu, limits);
            Eq(914, r.EfficientVoltage, "lowest ceiling that kept speed within 3%");
            Eq(890, r.LowestStableVoltage, "890 still passed, it was just slower");
        });

        Test("the voltage search stops at the first failure", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.FailsBelowVolt = 900;     // 938 and 914 pass, 890 fails
            CalibrationResult r = Calibrate(gpu, limits);
            Eq(914, r.LowestStableVoltage, "lowest pass");
            Eq(914, r.EfficientVoltage, "efficient");
            foreach (Settings s in gpu.Asked) True(s.MaxVoltageMv >= 890, "nothing below the failure was tried");
        });

        Test("only one setting is changed at a time", delegate
        {
            FakeGpu gpu = new FakeGpu();
            Calibrate(gpu, limits);
            foreach (Settings s in gpu.Asked)
            {
                True(s.MaxClockMHz == 1500 || s.MaxVoltageMv == 962, "clock and voltage changed together in " + s);
                Eq(70, s.PowerLimitW, "power stays factory"); Eq(100, s.TempLimitC, "temperature stays factory");
            }
        });

        Test("calibration can be stopped between steps", delegate
        {
            FakeGpu gpu = new FakeGpu(); int calls = 0;
            CalibrationResult r = Calibration.Run(limits, gpu.Measure, delegate (string s) { }, delegate { return ++calls > 2; });
            True(!r.Ok, "not ok");
            True(gpu.Asked.Count <= 3, "stopped after " + gpu.Asked.Count + " steps");
        });

        Test("steps scale with the GPU", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.FailsAboveClock = 1950;
            CalibrationResult r = Calibrate(gpu, Limits.For(Desktop()));   // 1900 MHz factory: steps of 40
            Eq(1940, r.MaxStableClock, "1940 passes, 1980 fails");
        });

        Test("a calibration reads back the same from its file", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.FailsAboveClock = 1580; gpu.SlowBelowVolt = 920;
            CalibrationResult a = Calibrate(gpu, limits);
            CalibrationResult b = CalibrationResult.Parse(a.ToText());
            True(b.Ok, "ok");
            Eq(a.MaxStableClock, b.MaxStableClock, "max clock"); Eq(a.RecommendedClock, b.RecommendedClock, "recommended clock");
            Eq(a.EfficientVoltage, b.EfficientVoltage, "efficient voltage"); Eq(a.LowestStableVoltage, b.LowestStableVoltage, "lowest voltage");
            Eq(a.Baseline.AvgClock, b.Baseline.AvgClock, "baseline speed"); Eq(a.Baseline.PeakHotspot, b.Baseline.PeakHotspot, "baseline temperature");
        });

        Test("a calibration records why each search stopped", delegate
        {
            FakeGpu failing = new FakeGpu(); failing.FailsAboveClock = 1580; failing.FailsBelowVolt = 900;
            CalibrationResult a = Calibrate(failing, limits);
            Eq("1590 MHz failed the test", a.ClockStop, "speed"); Eq("890 mV failed the test", a.VoltageStop, "voltage");

            FakeGpu hot = new FakeGpu(); hot.HotFromClock = 1560; hot.SlowBelowVolt = 920;
            CalibrationResult b = Calibrate(hot, limits);
            Eq("1560 MHz reached the hotspot limit", b.ClockStop, "speed"); Eq("890 mV was more than 3% slower", b.VoltageStop, "voltage");

            FakeGpu flat = new FakeGpu(); flat.SpeedPlateau = 1530;
            Eq("1560 MHz was no faster", Calibrate(flat, limits).ClockStop, "plateau");

            CalibrationResult c = Calibrate(new FakeGpu(), limits);
            Eq("the built-in limit was reached", c.ClockStop, "speed limit"); Eq("the built-in limit was reached", c.VoltageStop, "voltage limit");
        });

        Test("the reasons survive the file, and an older file without them still loads", delegate
        {
            FakeGpu gpu = new FakeGpu(); gpu.HotFromClock = 1560;
            CalibrationResult a = Calibrate(gpu, limits);
            Eq(a.ClockStop, CalibrationResult.Parse(a.ToText()).ClockStop, "speed reason");
            Eq(a.VoltageStop, CalibrationResult.Parse(a.ToText()).VoltageStop, "voltage reason");
            CalibrationResult old = CalibrationResult.Parse("max_stable_clock=1530\r\nrecommended_clock=1530\r\nlowest_stable_voltage=866\r\nefficient_voltage=890\r\nbaseline=" + a.Baseline.ToLine());
            Eq("", old.ClockStop, "no reason recorded");
            True(old.Describe(limits.Factory).Contains("1530 MHz"), old.Describe(limits.Factory));
        });

        Test("a damaged calibration file is refused", delegate
        {
            Throws<FormatException>(delegate { CalibrationResult.Parse("max_stable_clock=1560\r\n"); }, "missing values");
            Throws<FormatException>(delegate { CalibrationResult.Parse(""); }, "empty");
        });

        Test("after calibration, Boost and Efficient use the measured values", delegate
        {
            CalibrationResult c = new CalibrationResult(); c.Ok = true;
            c.MaxStableClock = 1560; c.RecommendedClock = 1530; c.LowestStableVoltage = 890; c.EfficientVoltage = 914;
            Preset[] p = Presets.For(limits, c);
            Eq(1530, p[3].Settings.MaxClockMHz, "Boost speed"); Eq(914, p[2].Settings.MaxVoltageMv, "Efficient voltage");
            Eq(S(1200, 45, 962, 85), p[1].Settings, "Cool is unchanged");
            foreach (Preset x in p) Eq(0, limits.Validate(x.Settings).Count, x.Name);
        });
    }

    static void RangeTests()
    {
        byte[] reference = Laptop();

        Test("the hard limits are the widest a GPU may ever be set to", delegate
        {
            Limits h = Limits.Hard(reference);
            Eq(400, h.ClockMin, "clock floor: 100 MHz above the table's minimum clock"); Eq(1800, h.ClockMax, "clock ceiling: 20% above factory");
            Eq(14, h.PowerMin, "power floor"); Eq(726, h.VoltMin, "voltage floor: just above the table's minimum voltage"); Eq(60, h.TempMin, "hotspot floor");
        });

        Test("even the hard limits never allow power, voltage or hotspot above factory", delegate
        {
            foreach (byte[] t in new byte[][] { Laptop(), Desktop() })
            {
                Limits h = Limits.Hard(t);
                Eq(h.Factory.PowerLimitW, h.PowerMax, "power"); Eq(h.Factory.MaxVoltageMv, h.VoltMax, "voltage"); Eq(h.Factory.TempLimitC, h.TempMax, "hotspot");
                True(h.ClockMax <= h.Factory.MaxClockMHz * 1.2, "clock at most 20% above factory");
            }
        });

        Test("a range chosen in the settings becomes the slider range", delegate
        {
            Limits l = Limits.For(reference, new Ranges(500, 1800, 20, 760, 65));
            Eq(500, l.ClockMin, "clock from"); Eq(1800, l.ClockMax, "clock to");
            Eq(20, l.PowerMin, "power from"); Eq(760, l.VoltMin, "voltage from"); Eq(65, l.TempMin, "hotspot from");
            Eq(70, l.PowerMax, "power to stays factory"); Eq(962, l.VoltMax, "voltage to stays factory"); Eq(100, l.TempMax, "hotspot to stays factory");
        });

        Test("a chosen range is pulled inside the hard limits", delegate
        {
            Limits l = Limits.For(reference, new Ranges(100, 2500, 1, 500, 30));
            Eq(400, l.ClockMin, "clock from"); Eq(1800, l.ClockMax, "clock to"); Eq(14, l.PowerMin, "power"); Eq(726, l.VoltMin, "voltage"); Eq(60, l.TempMin, "hotspot");
        });

        Test("a chosen range always keeps the factory value inside it", delegate
        {
            Limits l = Limits.For(reference, new Ranges(1600, 1400, 80, 1000, 120));
            Eq(1490, l.ClockMin, "clock from"); Eq(1500, l.ClockMax, "clock to"); Eq(69, l.PowerMin, "power"); Eq(960, l.VoltMin, "voltage"); Eq(99, l.TempMin, "hotspot");
        });

        Test("a chosen range is snapped to the slider steps so factory stays reachable", delegate
        {
            Limits l = Limits.For(reference, new Ranges(505, 1795, 20, 761, 65));
            Eq(510, l.ClockMin, "clock from"); Eq(1790, l.ClockMax, "clock to"); Eq(762, l.VoltMin, "voltage from");
        });

        Test("with no range chosen, the defaults are used and can be read back as a range", delegate
        {
            Ranges d = Limits.For(reference).ToRanges();
            Eq(750, d.ClockMin, "clock from"); Eq(1620, d.ClockMax, "clock to"); Eq(25, d.PowerMin, "power"); Eq(802, d.VoltMin, "voltage"); Eq(70, d.TempMin, "hotspot");
            Eq(1620, Limits.For(reference, d).ClockMax, "round trip");
        });

        Test("a range reads back the same from its file, and a damaged file is refused", delegate
        {
            Ranges r = Ranges.Parse(new Ranges(500, 1800, 20, 760, 65).Format());
            Eq(500, r.ClockMin, "clock from"); Eq(1800, r.ClockMax, "clock to"); Eq(20, r.PowerMin, "power"); Eq(760, r.VoltMin, "voltage"); Eq(65, r.TempMin, "hotspot");
            Throws<FormatException>(delegate { Ranges.Parse("clock_min=500\r\n"); }, "missing values");
        });

        Test("settings inside a widened range can be applied, beyond the hard limit they cannot", delegate
        {
            Eq((ushort)1800, U16(PowerTable.Apply(reference, S(1800, 70, 962, 100)), 816), "1800 MHz is inside the hard limit");
            Throws<ArgumentException>(delegate { PowerTable.Apply(reference, S(1810, 70, 962, 100)); }, "1810 MHz is not");
        });

        Test("calibration searches the whole chosen range and nothing outside it", delegate
        {
            Limits wide = Limits.For(reference, new Ranges(500, 1800, 20, 760, 65));
            FakeGpu gpu = new FakeGpu(); gpu.FailsAboveClock = 1700;
            CalibrationResult r = Calibrate(gpu, wide);
            Eq(1680, r.MaxStableClock, "1680 passes, 1710 fails"); Eq(1650, r.RecommendedClock, "one step back");
            Eq(770, r.LowestStableVoltage, "lowest step inside the range");
            foreach (Settings s in gpu.Asked) Eq(0, wide.Validate(s).Count, "tried " + s);
        });
    }

    static void MonitorTests()
    {
        Test("history keeps the newest readings in order", delegate
        {
            History h = new History(3);
            for (int i = 1; i <= 5; i++) h.Add(new Reading(i * 100, 900, 50, 60, 70, 99));
            Eq(3, h.Count, "count");
            int[] clock = h.Series(delegate (Reading r) { return r.ClockMHz; }, 10);
            Eq(3, clock.Length, "length"); Eq(300, clock[0], "oldest kept"); Eq(500, clock[2], "newest");
            Eq(2, h.Series(delegate (Reading r) { return r.ClockMHz; }, 2).Length, "only the last two");
            Eq(400, h.Series(delegate (Reading r) { return r.ClockMHz; }, 2)[0], "the last two start at 400");
        });

        Test("a graph's numbers are the minimum, average, maximum and latest value", delegate
        {
            SeriesStats s = SeriesStats.Of(new int[] { 10, 30, 20 });
            Eq(10, s.Min, "min"); Eq(30, s.Max, "max"); Eq(20, s.Avg, "avg"); Eq(20, s.Last, "last");
            SeriesStats none = SeriesStats.Of(new int[0]);
            Eq(0, none.Max, "nothing recorded yet"); True(!none.Any, "flagged as empty");
        });

        Test("a graph's scale always shows the data and the ceiling with some air around them", delegate
        {
            int lo, hi;
            SeriesStats.Scale(new int[] { 1400, 1440 }, null, 1500, out lo, out hi);
            True(lo < 1400 && hi > 1500, lo + ".." + hi);
            SeriesStats.Scale(new int[] { 50, 50 }, null, -1, out lo, out hi);
            True(hi > lo, "a flat line still has a scale");
            SeriesStats.Scale(new int[] { 80 }, new int[] { 60 }, 100, out lo, out hi);
            True(lo < 60 && hi > 100, "second series included: " + lo + ".." + hi);
        });

        Test("preferences read back the same", delegate
        {
            Prefs p = new Prefs(); p.TestSeconds = 180; p.Theme = "dark"; p.Accent = "Cobalt"; p.Monitor = false; p.HistorySeconds = 900; p.GraphPower = false;
            Prefs q = Prefs.Parse(p.Format());
            Eq(180, q.TestSeconds, "test length"); Eq("dark", q.Theme, "theme"); Eq("Cobalt", q.Accent, "accent");
            Eq(false, q.Monitor, "monitor"); Eq(900, q.HistorySeconds, "history"); Eq(false, q.GraphPower, "power graph"); Eq(true, q.GraphTemp, "temperature graph");
        });

        Test("missing or damaged preferences fall back to the defaults", delegate
        {
            Prefs d = Prefs.Parse("");
            Eq(20, d.TestSeconds, "test length"); Eq("system", d.Theme, "theme"); Eq("Copper", d.Accent, "accent"); Eq(true, d.Monitor, "monitor"); Eq(300, d.HistorySeconds, "history");
            Prefs bad = Prefs.Parse("test_seconds=banana\r\ntheme=neon\r\naccent=Mauve\r\nhistory_seconds=7\r\n");
            Eq(20, bad.TestSeconds, "test length"); Eq("system", bad.Theme, "theme"); Eq("Copper", bad.Accent, "accent"); Eq(300, bad.HistorySeconds, "history");
            Eq(60, Prefs.Parse("# prefs\r\n test_seconds = 60 \r\n").TestSeconds, "spaces and comments");
        });

        Test("every accent colour has a light and a dark shade, and Copper is the default", delegate
        {
            Eq("Copper", Accents.Names[0], "default first");
            True(Accents.Names.Length >= 5, "choices");
            foreach (string n in Accents.Names) True(Accents.Rgb(n, false) != Accents.Rgb(n, true), n);
            Eq(Accents.Rgb("Copper", true), Accents.Rgb("no such colour", true), "unknown names fall back to Copper");
        });
    }

    static void UxTests()
    {
        Test("test durations are offered from 20 seconds to 10 minutes and named plainly", delegate
        {
            Eq(4, Durations.Options.Length, "choices");
            Eq("20 seconds", Durations.Label(20), "20"); Eq("1 minute", Durations.Label(60), "60");
            Eq("3 minutes", Durations.Label(180), "180"); Eq("10 minutes", Durations.Label(600), "600");
            Eq("Test for 3 minutes", Durations.ButtonText(180), "button");
        });

        Test("a saved test duration that is not one of the choices falls back to 20 seconds", delegate
        {
            Eq(180, Durations.Normalize(180), "valid"); Eq(20, Durations.Normalize(45), "not offered");
            Eq(20, Durations.Normalize(0), "zero"); Eq(20, Durations.Normalize(-5), "negative");
        });

        Test("elapsed time is shown as minutes and seconds", delegate
        {
            Eq("0:00", Durations.Clock(0), "start"); Eq("0:34", Durations.Clock(34), "seconds");
            Eq("3:05", Durations.Clock(185), "minutes"); Eq("0:00", Durations.Clock(-3), "never negative");
        });

        Test("unapplied changes are listed as applied value, arrow, new value", delegate
        {
            string[] none = Changes.Describe(S(1500, 70, 962, 100), S(1500, 70, 962, 100));
            Eq(0, none.Length, "no changes");
            string[] two = Changes.Describe(S(1500, 70, 962, 100), S(1200, 70, 962, 85));
            Eq(2, two.Length, "two changes");
            Eq("Top speed 1500 → 1200 MHz", two[0], "speed"); Eq("Hotspot limit 100 → 85 °C", two[1], "hotspot");
            string[] all = Changes.Describe(S(1500, 70, 962, 100), S(1510, 60, 900, 90));
            Eq("Power limit 70 → 60 W", all[1], "power"); Eq("Voltage ceiling 962 → 900 mV", all[2], "voltage");
        });

        Test("the change summary fits on one line however many settings changed", delegate
        {
            Eq("", Changes.Summary(S(1500, 70, 962, 100), S(1500, 70, 962, 100)), "nothing to say");
            string one = Changes.Summary(S(1500, 70, 962, 100), S(1200, 70, 962, 100));
            True(one.StartsWith("Not applied yet") && one.Contains("1500 → 1200 MHz"), one);
            string four = Changes.Summary(S(1500, 70, 962, 100), S(1510, 60, 900, 90));
            True(four.Contains("Top speed") && four.Contains("Hotspot limit"), four);
        });

        Test("settings are written out for people with units and separators", delegate
        {
            Eq("1500 MHz · 70 W · 962 mV · 100 °C", S(1500, 70, 962, 100).Pretty(), "pretty");
        });
    }

    static List<Reading> FourReadings(int warmupHotspot)
    {
        List<Reading> r = new List<Reading>();
        r.Add(new Reading(100, 700, 5, 40, warmupHotspot, 0));   // warm-up quarter
        r.Add(new Reading(1400, 925, 50, 60, 70, 99));
        r.Add(new Reading(1440, 925, 54, 62, 78, 99));
        r.Add(new Reading(1442, 930, 56, 64, 80, 99));
        return r;
    }

    static void SupportTests()
    {
        Limits limits = Limits.For(Laptop());
        const string MyGpu = @"PCI\VEN_1002&DEV_7340&SUBSYS_130E1462&REV_C1\4&1";

        Test("each GPU model gets its own file name for its stored table", delegate
        {
            Eq("VEN_1002_DEV_7340_SUBSYS_130E1462", Hardware.TableKey(MyGpu), "key");
            Eq("VEN_1002_DEV_731F_SUBSYS_E4111DA2", Hardware.TableKey(@"pci\ven_1002&dev_731f&subsys_e4111da2&rev_c1\4&1"), "any letter case");
            Throws<ArgumentException>(delegate { Hardware.TableKey(@"USB\VID_046D"); }, "not a PCI graphics device");
        });

        Test("only hardware that was really tested is called tested", delegate
        {
            True(Hardware.IsTested(MyGpu), "the RX 5500M this was developed on");
            True(!Hardware.IsTested(@"PCI\VEN_1002&DEV_731F&SUBSYS_E4111DA2&REV_C1\4&1"), "another card");
            True(!Hardware.IsTested(@"PCI\VEN_1002&DEV_7340&SUBSYS_00000000&REV_C1\4&1"), "same chip in another laptop");
        });

        Test("only AMD graphics devices are considered", delegate
        {
            True(Hardware.IsAmd(MyGpu), "AMD");
            True(!Hardware.IsAmd(@"PCI\VEN_10DE&DEV_2520&SUBSYS_12341462"), "Nvidia");
        });

        Test("a saved profile reads back the same", delegate
        {
            Settings s = S(1350, 55, 910, 88);
            Eq(s, ProfileText.Parse(ProfileText.Format(s), limits), "round trip");
        });

        Test("a profile may contain blank lines, comments and spaces", delegate
        {
            Eq(S(1350, 55, 910, 88), ProfileText.Parse("# my profile\r\n\r\n clock = 1350\r\npower=55\r\nvoltage = 910 \r\ntemp=88\r\n", limits), "parsed");
        });

        Test("a damaged profile is refused instead of half-loaded", delegate
        {
            Throws<FormatException>(delegate { ProfileText.Parse("clock=1350\r\npower=55\r\nvoltage=910\r\n", limits); }, "missing temp");
            Throws<FormatException>(delegate { ProfileText.Parse("clock=fast\r\npower=55\r\nvoltage=910\r\ntemp=88", limits); }, "not a number");
            Throws<FormatException>(delegate { ProfileText.Parse("", limits); }, "empty");
        });

        Test("a profile made for a different GPU is refused when it is outside this GPU's limits", delegate
        {
            string fromDesktop = ProfileText.Format(S(1900, 180, 1200, 110));
            Throws<FormatException>(delegate { ProfileText.Parse(fromDesktop, limits); }, "desktop profile on the laptop");
        });

        Test("profile names become safe file names", delegate
        {
            Eq("Night_ quiet", ProfileText.SafeName("  Night: quiet  "), "colon replaced, trimmed");
            Throws<ArgumentException>(delegate { ProfileText.SafeName("   "); }, "blank name");
        });

        Test("clock advice states how far from factory", delegate
        {
            True(Advice.Clock(1500, limits).Contains("Factory"), Advice.Clock(1500, limits));
            True(Advice.Clock(1200, limits).Contains("20%") && Advice.Clock(1200, limits).Contains("slower"), Advice.Clock(1200, limits));
            True(Advice.Clock(1575, limits).Contains("5%") && Advice.Clock(1575, limits).Contains("overclock"), Advice.Clock(1575, limits));
        });

        Test("voltage, power and temperature advice name the change", delegate
        {
            True(Advice.Voltage(962, limits).Contains("Factory"), Advice.Voltage(962, limits));
            True(Advice.Voltage(900, limits).Contains("62 mV"), Advice.Voltage(900, limits));
            True(Advice.Power(70, limits).Contains("Factory"), Advice.Power(70, limits));
            True(Advice.Power(45, limits).Contains("45 W") && Advice.Power(45, limits).Contains("70 W"), Advice.Power(45, limits));
            True(Advice.Temp(100, limits).Contains("Factory"), Advice.Temp(100, limits));
            True(Advice.Temp(85, limits).Contains("85"), Advice.Temp(85, limits));
        });

        Test("only a clock above factory counts as an overclock", delegate
        {
            True(Advice.IsOverclock(S(1510, 70, 962, 100), limits), "1510 MHz");
            True(!Advice.IsOverclock(S(1500, 70, 962, 100), limits), "1500 MHz");
        });

        Test("a load test summary ignores the warm-up quarter for averages", delegate
        {
            LoadResult s = LoadResult.Summarize(FourReadings(45));
            True(s.Ok, "ok");
            Eq(1427, s.AvgClock, "average clock of the last three");
            Eq(930, s.PeakVoltage, "peak voltage");
            Eq(53, s.AvgPower, "average power");
            Eq(56, s.PeakPower, "peak power");
            Eq(80, s.PeakHotspot, "peak hotspot");
            Eq(4, s.Samples, "samples");
        });

        Test("the hottest moment counts even if it was during warm-up", delegate
        {
            Eq(95, LoadResult.Summarize(FourReadings(95)).PeakHotspot, "peak hotspot");
        });

        Test("too few readings is reported as a failed test", delegate
        {
            List<Reading> r = new List<Reading>();
            r.Add(new Reading(1400, 925, 50, 60, 70, 99));
            True(!LoadResult.Summarize(r).Ok, "not ok");
        });

        Test("a load test result survives being passed between processes", delegate
        {
            LoadResult a = LoadResult.Summarize(FourReadings(45));
            LoadResult b = LoadResult.Parse("some driver noise\r\n" + a.ToLine() + "\r\n");
            True(b.Ok, "ok");
            Eq(a.AvgClock, b.AvgClock, "clock"); Eq(a.PeakVoltage, b.PeakVoltage, "voltage");
            Eq(a.AvgPower, b.AvgPower, "power"); Eq(a.PeakPower, b.PeakPower, "peak power");
            Eq(a.PeakHotspot, b.PeakHotspot, "hotspot"); Eq(a.PeakEdge, b.PeakEdge, "edge"); Eq(a.Samples, b.Samples, "samples");
        });

        Test("a failed load test carries its reason across", delegate
        {
            LoadResult b = LoadResult.Parse(LoadResult.Failed("no OpenCL device; \r\n try again").ToLine());
            True(!b.Ok, "not ok");
            True(b.Error.Contains("no OpenCL device") && b.Error.Contains("try again"), b.Error);
        });

        Test("a helper that printed no result is reported as failed", delegate
        {
            LoadResult b = LoadResult.Parse("crashed\r\n");
            True(!b.Ok, "not ok");
            True(b.Error != null && b.Error.Length > 0, "has a reason");
        });

        Test("the change since the previous test is worded as a signed difference", delegate
        {
            Eq("-18 W", LoadResult.Delta(37, 55, "W"), "lower");
            Eq("+80 MHz", LoadResult.Delta(1520, 1440, "MHz"), "higher");
            Eq("no change", LoadResult.Delta(55, 55, "W"), "same");
        });

        Test("GPU usage counters are matched to the right adapter", delegate
        {
            Eq("luid_0x00000000_0x0000fa28", GpuCounters.LuidTag(0xFA28L), "tag");
            Eq(1234, GpuCounters.PidOf("pid_1234_luid_0x00000000_0x0000FA28_phys_0"), "pid");
            Eq(-1, GpuCounters.PidOf("nonsense"), "bad name");
            True(GpuCounters.IsOnAdapter("pid_1234_luid_0x00000000_0x0000FA28_phys_0", 0xFA28L), "same adapter, any letter case");
            True(!GpuCounters.IsOnAdapter("pid_1234_luid_0x00000000_0x0000E9F2_phys_0", 0xFA28L), "other adapter");
        });

        Test("only a clean exit of the Windows restart command counts as restarted", delegate
        {
            Eq(RestartOutcome.Restarted, Pnp.Interpret(0), "exit 0");
            Eq(RestartOutcome.RebootNeeded, Pnp.Interpret(3010), "exit 3010");
            Eq(RestartOutcome.Failed, Pnp.Interpret(1), "exit 1");
        });

        Test("the message after applying says whether a reboot is still needed", delegate
        {
            True(Pnp.Message(RestartOutcome.Restarted, "").StartsWith("Applied"), Pnp.Message(RestartOutcome.Restarted, ""));
            True(Pnp.Message(RestartOutcome.RebootNeeded, "").Contains("reboot"), Pnp.Message(RestartOutcome.RebootNeeded, ""));
            True(Pnp.Message(RestartOutcome.Failed, "device busy").Contains("device busy") && Pnp.Message(RestartOutcome.Failed, "device busy").Contains("Reboot"), "failure keeps the detail");
        });
    }
}
