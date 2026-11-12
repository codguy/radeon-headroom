using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tuner
{
    static class Program
    {
        /// <summary>
        /// No arguments: open the window. Anywhere on the line: --theme light|dark.
        ///   --status           print what is set and what the GPU reports (changes nothing)
        ///   --apply NAME       apply a preset (Factory, Cool, Efficient, Boost) or a saved profile, no window
        ///   --reset            remove the table (back to factory) and restart the GPU
        ///   --import FILE      take this GPU's reference table from a vBIOS file
        ///   --calibrate [S]    measure this GPU step by step and save its real limits; S = seconds per test (20, 60, 180, 600)
        ///   --recover          put back the settings from before an interrupted calibration (Windows runs this itself)
        ///   --loadtest N GPU   internal helper: load the GPU for N seconds and print one RESULT line
        ///   --preview FILE     save a picture of the window to FILE and exit
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            Native.Quiet();
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string arg1 = args.Length > 1 ? args[1] : null;

            if (mode == "--loadtest")
            {
                int seconds = 20;
                if (arg1 != null) int.TryParse(arg1, out seconds);
                seconds = Math.Max(5, Math.Min(900, seconds));
                LoadResult r;
                try { r = LoadTest.Run(seconds, args.Length > 2 ? args[2] : "Radeon"); }
                catch (Exception ex) { r = LoadResult.Failed(ex.Message); }
                Console.Out.WriteLine(r.ToLine());
                Console.Out.Flush();
                Native.HardExit();
                return 0;
            }

            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            string tablesDir = Path.Combine(appDir, "tables");
            UseTheme(appDir, args);
            Gpu gpu = Gpu.Choose(Gpu.FindAmd(), tablesDir);

            // A calibration that was cut short (crash, power loss) leaves a marker: put the earlier settings back first.
            string recovered = null;
            if (gpu != null && gpu.ExperimentPending(tablesDir) && !Calibrator.IsRunning())
            {
                RestartOutcome o; string d;
                gpu.EndExperiment(tablesDir, out o, out d);
                recovered = "A calibration was interrupted earlier. The settings from before it were put back"
                          + (o == RestartOutcome.Restarted ? "." : ", but the GPU could not be restarted - reboot to load them.");
            }
            if (mode == "--recover")
            {
                if (recovered != null || Native.OutputIsPiped()) Report(recovered ?? "Nothing to recover.");
                return 0;
            }

            if (mode == "--status" || mode == "--reset" || mode == "--apply" || mode == "--import" || mode == "--calibrate")
            {
                if (gpu == null) { Report("No AMD Radeon graphics device was found."); return 1; }
                string prefix = recovered == null ? "" : recovered + "\r\n";
                if (mode == "--status") Report(prefix + Status(gpu, tablesDir));
                else if (mode == "--import") Report(prefix + Import(gpu, tablesDir, arg1));
                else if (mode == "--calibrate") Report(prefix + Calibrate(gpu, appDir, arg1));
                else Report(prefix + ApplyNamed(gpu, appDir, mode == "--reset" ? "Factory" : arg1));
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (gpu == null)
            {
                MessageBox.Show("No AMD Radeon graphics device was found on this PC, so there is nothing to tune.", App.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            MainForm form = new MainForm(gpu, recovered);
            if (mode == "--preview" && arg1 != null)
            {
                form.Show();
                for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(100); }
                int show = Array.IndexOf(args, "--show");
                if (show >= 0 && show + 1 < args.Length) { form.ShowPreset(args[show + 1]); Application.DoEvents(); }
                if (Array.IndexOf(args, "--test") >= 0)
                {
                    // run a real load test first so the picture shows real results
                    form.StartTest();
                    for (int i = 0; i < 900 && form.Busy; i++) { Application.DoEvents(); Thread.Sleep(100); }
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(100); }
                }
                int wait = Array.IndexOf(args, "--wait");   // let the graphs collect some seconds of readings first
                int waitSeconds = 0;
                if (wait >= 0 && wait + 1 < args.Length) int.TryParse(args[wait + 1], out waitSeconds);
                for (int i = 0; i < Math.Min(120, waitSeconds) * 10; i++) { Application.DoEvents(); Thread.Sleep(100); }
                if (Array.IndexOf(args, "--settings") < 0)
                using (Bitmap whole = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height));
                    // keep the client area only: the title bar is drawn by Windows and looks different in a capture
                    int border = (form.Width - form.ClientSize.Width) / 2;
                    Rectangle client = new Rectangle(border, form.Height - form.ClientSize.Height - border, form.ClientSize.Width, form.ClientSize.Height);
                    using (Bitmap bmp = whole.Clone(client, whole.PixelFormat)) bmp.Save(arg1, ImageFormat.Png);
                }
                if (Array.IndexOf(args, "--settings") >= 0)
                {
                    // picture of the settings dialog instead of the main window
                    using (SettingsDialog d = form.NewSettingsDialog())
                    {
                        d.StartPosition = FormStartPosition.Manual; d.Location = form.Location;
                        d.Show();
                        for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(100); }
                        using (Bitmap whole = new Bitmap(d.Width, d.Height))
                        {
                            d.DrawToBitmap(whole, new Rectangle(0, 0, d.Width, d.Height));
                            int border = (d.Width - d.ClientSize.Width) / 2;
                            Rectangle client = new Rectangle(border, d.Height - d.ClientSize.Height - border, d.ClientSize.Width, d.ClientSize.Height);
                            using (Bitmap bmp = whole.Clone(client, whole.PixelFormat)) bmp.Save(arg1, ImageFormat.Png);
                        }
                    }
                }
                form.Close();
                return 0;
            }

            // Saving Settings closes the window and asks for a fresh one, so new colours and layout take effect at once.
            while (true)
            {
                Application.Run(form);
                if (!form.RestartRequested) break;
                UseTheme(appDir, args);
                form = new MainForm(gpu, null);
            }
            return 0;
        }

        /// <summary>Light or dark and the accent colour: from prefs.txt, unless --theme or --accent says otherwise.</summary>
        static void UseTheme(string appDir, string[] args)
        {
            Prefs prefs = new Prefs();
            try { prefs = Prefs.Parse(File.ReadAllText(Path.Combine(appDir, "prefs.txt"))); } catch (IOException) { }
            bool dark = prefs.Theme == "dark" || (prefs.Theme == "system" && Theme.SystemPrefersDark());
            string accent = prefs.Accent;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].ToLowerInvariant() == "--theme") dark = args[i + 1].ToLowerInvariant() == "dark";
                if (args[i].ToLowerInvariant() == "--accent") accent = args[i + 1];
            }
            Theme.Use(dark, accent);
        }

        /// <summary>Prints when run from a script (output piped), otherwise shows a message box (shortcut or .cmd).</summary>
        static void Report(string text)
        {
            if (Native.OutputIsPiped()) { Console.Out.WriteLine(text); Console.Out.Flush(); }
            else MessageBox.Show(text, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static string Status(Gpu gpu, string tablesDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(App.Name + " " + App.Version);
            sb.AppendLine("GPU:            " + gpu.Name + (gpu.IsTested ? " (tested model)" : " (untested model)"));
            sb.AppendLine("Device:         " + gpu.InstanceId);
            bool vbios;
            byte[] reference = gpu.LoadReference(tablesDir, out vbios);
            sb.AppendLine("Reference:      " + (reference == null ? "none yet - import the vBIOS" : (vbios ? "from vBIOS" : "from the table found on this PC") + ", " + reference.Length + " bytes"));
            byte[] table = gpu.ReadOverride();
            sb.AppendLine("Table set:      " + (table == null ? "no (factory)" : "yes, " + table.Length + " bytes"));
            if (reference != null)
            {
                Limits l = gpu.LimitsFor(reference, tablesDir);
                sb.AppendLine("Factory:        " + l.Factory);
                sb.AppendLine("Ranges:         " + (gpu.LoadRanges(tablesDir) == null ? "(defaults) " : "(from Settings) ") + l.ClockMin + "-" + l.ClockMax + " MHz, " + l.PowerMin + "-" + l.PowerMax + " W, " + l.VoltMin + "-" + l.VoltMax + " mV, " + l.TempMin + "-" + l.TempMax + " C");
                CalibrationResult measured = Calibrator.Load(gpu, tablesDir);
                sb.AppendLine("Calibration:    " + (measured == null ? "not done yet" : measured.Date + " - " + measured.Describe(l.Factory)));
                try { sb.AppendLine("Settings:       " + gpu.Current(reference)); }
                catch (TableFormatException ex) { sb.AppendLine("Settings:       unreadable - " + ex.Message); }
            }
            sb.AppendLine("Device running: " + gpu.IsRunning());
            List<string> apps = gpu.AppsUsingIt();
            sb.AppendLine("Programs on it: " + (apps.Count == 0 ? "none" : string.Join(", ", apps.ToArray())));
            using (Sensors s = Sensors.Open(gpu.Name))
            {
                Reading r = s == null ? null : s.Read();
                sb.AppendLine("Sensors:        " + (r == null ? "not available"
                    : r.ClockMHz + " MHz, " + r.VoltageMv + " mV, " + r.PowerW + " W, hotspot " + r.HotspotC + " C, activity " + r.ActivityPct + "%"));
            }
            return sb.ToString();
        }

        /// <summary>Runs the full calibration without the window, printing each step when a script is listening.</summary>
        static string Calibrate(Gpu gpu, string appDir, string secondsArg)
        {
            string tablesDir = Path.Combine(appDir, "tables");
            // test length per step: the number given, else the one chosen in the window, else 20 seconds
            int testSeconds = Durations.Default, given;
            try { testSeconds = Prefs.Parse(File.ReadAllText(Path.Combine(appDir, "prefs.txt"))).TestSeconds; } catch (IOException) { }
            if (secondsArg != null && int.TryParse(secondsArg, out given)) testSeconds = Durations.Normalize(given);
            bool vbios;
            byte[] reference = gpu.LoadReference(tablesDir, out vbios);
            if (reference == null) return "There is no table for this GPU yet. Open " + App.Name + " and import its vBIOS first. Nothing was changed.";
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) return "Plug the laptop in first: calibration loads the GPU for several minutes. Nothing was changed.";
            List<string> apps = gpu.AppsUsingIt();
            if (apps.Count > 0) return "Nothing was changed: close " + string.Join(", ", apps.ToArray()) + " first (they are using the " + gpu.Name + ").";

            string note;
            Limits range = gpu.LimitsFor(reference, tablesDir);   // the ranges chosen in Settings, or the defaults
            CalibrationResult r = Calibrator.Run(gpu, reference, range, tablesDir, testSeconds,
                delegate (int step, int total, string what) { if (Native.OutputIsPiped()) { Console.Out.WriteLine("step " + step + " of at most " + total + ": " + what); Console.Out.Flush(); } },
                delegate (bool measuring) { }, delegate { return false; }, out note);
            return (r.Ok ? "Calibration finished. " + r.Describe(range.Factory) + " Your previous settings are back in place."
                         : "Calibration did not finish: " + r.Error + " Your previous settings were put back.") + note;
        }

        static string Import(Gpu gpu, string tablesDir, string file)
        {
            if (file == null || !File.Exists(file)) return "Say which vBIOS file to import, for example: --import my-gpu.rom";
            try
            {
                byte[] table = Vbios.ExtractPowerTable(File.ReadAllBytes(file));
                gpu.StoreReference(tablesDir, table, true);
                return "Imported. Factory values for " + gpu.Name + ": " + PowerTable.Read(table) + ".";
            }
            catch (TableFormatException ex) { return "Not imported: " + ex.Message; }
            catch (IOException ex) { return "Not imported: " + ex.Message; }
        }

        /// <summary>Applies a built-in preset or a saved profile by name, without opening the window.</summary>
        static string ApplyNamed(Gpu gpu, string appDir, string name)
        {
            bool vbios;
            byte[] reference = gpu.LoadReference(Path.Combine(appDir, "tables"), out vbios);
            if (reference == null)
            {
                if (string.Equals(name, "Factory", StringComparison.OrdinalIgnoreCase))
                {
                    // even without a reference table, going back to factory is always possible
                    gpu.Backup(Path.Combine(appDir, "backups"));
                    gpu.DeleteOverride();
                    string d; RestartOutcome o = gpu.Restart(out d);
                    return o == RestartOutcome.Restarted ? "Table removed. The GPU is back on its factory settings." : Pnp.Message(o, d);
                }
                return "There is no table for this GPU yet. Open " + App.Name + " and import its vBIOS first. Nothing was changed.";
            }
            Limits limits = gpu.LimitsFor(reference, Path.Combine(appDir, "tables"));
            CalibrationResult measured = Calibrator.Load(gpu, Path.Combine(appDir, "tables"));

            Settings want;
            Preset preset = Presets.ByName(Presets.For(limits, measured), name);
            if (preset != null) want = preset.Settings;
            else
            {
                try
                {
                    string file = Path.Combine(Path.Combine(appDir, "profiles"), ProfileText.SafeName(name) + ".profile");
                    if (!File.Exists(file)) return "There is no preset or saved profile called \"" + name + "\". Nothing was changed.";
                    want = ProfileText.Parse(File.ReadAllText(file), limits);
                }
                catch (ArgumentException) { return "Say which preset or profile to apply, for example: --apply Cool"; }
                catch (FormatException ex) { return "That profile could not be loaded: " + ex.Message + " Nothing was changed."; }
            }

            List<string> apps = gpu.AppsUsingIt();
            if (apps.Count > 0) return "Nothing was changed: close " + string.Join(", ", apps.ToArray()) + " first (they are using the " + gpu.Name + ").";

            try
            {
                string detail;
                RestartOutcome outcome = gpu.Apply(reference, want, Path.Combine(appDir, "backups"), out detail);
                return outcome == RestartOutcome.Restarted ? "Applied: " + want : Pnp.Message(outcome, detail);
            }
            catch (Exception ex) { return "That did not work: " + ex.Message; }
        }
    }
}
