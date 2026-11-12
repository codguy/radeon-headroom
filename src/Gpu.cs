using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using Microsoft.Win32;

namespace Tuner
{
    /// <summary>Everything that touches the real machine: finding the GPU, its registry table, its stored reference table, restarting it.</summary>
    public class Gpu
    {
        const string ValueName = "PP_PhmSoftPowerPlayTable";

        public string Name, InstanceId, ClassKey;
        public long AdapterRam;

        public string Key { get { return Hardware.TableKey(InstanceId); } }
        public bool IsTested { get { return Hardware.IsTested(InstanceId); } }

        /// <summary>Every AMD graphics device Windows currently shows.</summary>
        public static List<Gpu> FindAmd()
        {
            List<Gpu> found = new List<Gpu>();
            using (ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, AdapterRAM FROM Win32_VideoController"))
                foreach (ManagementObject mo in q.Get())
                {
                    string id = (mo["PNPDeviceID"] as string) ?? "";
                    if (!Hardware.IsAmd(id)) continue;
                    using (RegistryKey e = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id))
                    {
                        string driver = e == null ? null : e.GetValue("Driver") as string;
                        if (driver == null) continue;
                        Gpu g = new Gpu();
                        g.Name = (mo["Name"] as string) ?? "AMD Radeon";
                        g.InstanceId = id;
                        g.ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\" + driver;
                        try { g.AdapterRam = Convert.ToInt64(mo["AdapterRAM"] ?? 0); } catch (InvalidCastException) { }
                        found.Add(g);
                    }
                }
            return found;
        }

        /// <summary>The GPU to tune: one that already has a table we can read, otherwise the one with the most memory (the discrete one).</summary>
        public static Gpu Choose(List<Gpu> all, string tablesDir)
        {
            Gpu best = null;
            foreach (Gpu g in all)
            {
                bool vbios;
                if (g.LoadReference(tablesDir, out vbios) != null) return g;
                if (best == null || g.AdapterRam > best.AdapterRam) best = g;
            }
            return best;
        }

        // ---- the registry table the driver loads ----

        public byte[] ReadOverride()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ClassKey))
                return k == null ? null : k.GetValue(ValueName) as byte[];
        }

        public void WriteOverride(byte[] table)
        {
            PowerTable.Check(table);
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ClassKey, true))
            {
                if (k == null) throw new InvalidOperationException("The graphics driver's settings key is missing. Nothing was changed.");
                k.SetValue(ValueName, table, RegistryValueKind.Binary);
            }
        }

        /// <summary>Removes the table so the driver goes back to the GPU's own factory table.</summary>
        public void DeleteOverride()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ClassKey, true))
                if (k != null) k.DeleteValue(ValueName, false);
        }

        // ---- the reference table: this GPU's starting point, kept in the "tables" folder on this PC only ----

        string ReferencePath(string tablesDir, bool fromVbios) { return Path.Combine(tablesDir, Key + (fromVbios ? ".vbios.bin" : ".registry.bin")); }

        /// <summary>
        /// The table every setting is written into. Order of preference: one imported from the vBIOS (true factory),
        /// one saved earlier from the registry, or - first run - the table that is in the registry now, which is then saved.
        /// Returns null when this GPU has none of these yet.
        /// </summary>
        public byte[] LoadReference(string tablesDir, out bool fromVbios)
        {
            fromVbios = false;
            foreach (bool vbios in new bool[] { true, false })
            {
                string path = ReferencePath(tablesDir, vbios);
                if (!File.Exists(path)) continue;
                try { byte[] t = File.ReadAllBytes(path); PowerTable.Check(t); fromVbios = vbios; return t; }
                catch (TableFormatException) { }
                catch (IOException) { }
            }
            byte[] current = ReadOverride();
            if (current == null) return null;
            try { PowerTable.Check(current); } catch (TableFormatException) { return null; }
            StoreReference(tablesDir, current, false);
            return current;
        }

        public void StoreReference(string tablesDir, byte[] table, bool fromVbios)
        {
            PowerTable.Check(table);
            Directory.CreateDirectory(tablesDir);
            File.WriteAllBytes(ReferencePath(tablesDir, fromVbios), table);
        }

        string RangesPath(string tablesDir) { return Path.Combine(tablesDir, Key + ".ranges"); }

        /// <summary>The slider and calibration range chosen in Settings for this GPU, or null for the defaults.</summary>
        public Ranges LoadRanges(string tablesDir)
        {
            try { return Ranges.Parse(File.ReadAllText(RangesPath(tablesDir))); }
            catch (IOException) { return null; }
            catch (FormatException) { return null; }
        }

        /// <summary>null goes back to the defaults.</summary>
        public void SaveRanges(string tablesDir, Ranges ranges)
        {
            Directory.CreateDirectory(tablesDir);
            if (ranges == null) File.Delete(RangesPath(tablesDir));
            else File.WriteAllText(RangesPath(tablesDir), ranges.Format());
        }

        public Limits LimitsFor(byte[] reference, string tablesDir)
        {
            Ranges chosen = LoadRanges(tablesDir);
            return chosen == null ? Limits.For(reference) : Limits.For(reference, chosen);
        }

        /// <summary>The settings in the registry right now (the reference's own values when no table is set).</summary>
        public Settings Current(byte[] reference)
        {
            byte[] o = ReadOverride();
            return o == null ? PowerTable.Read(reference) : PowerTable.Read(o);
        }

        /// <summary>Saves the current registry table to a dated file (keeps the newest 30). Returns the path, or null if none was set.</summary>
        public string Backup(string folder)
        {
            byte[] o = ReadOverride();
            if (o == null || folder == null) return null;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Key + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin");
            File.WriteAllBytes(path, o);
            string[] old = Directory.GetFiles(folder, "*.bin");
            Array.Sort(old, delegate (string a, string b) { return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)); });
            for (int i = 0; i < old.Length - 30; i++) { try { File.Delete(old[i]); } catch (IOException) { } }
            return path;
        }

        /// <summary>
        /// The whole "apply" step: back up what is set, write the new table (or remove it when the settings are
        /// the reference's own, i.e. factory), then restart the GPU so the driver loads it.
        /// </summary>
        public RestartOutcome Apply(byte[] reference, Settings want, string backupFolder, out string detail)
        {
            byte[] table = PowerTable.Apply(reference, want);   // validates before anything is touched
            Backup(backupFolder);
            if (want.Equals(PowerTable.Read(reference))) DeleteOverride();
            else WriteOverride(table);
            return Restart(out detail);
        }

        // ---- experiments (calibration): remember what was set, and put it back even after a crash or reboot ----

        const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", RunOnceName = "HeadroomRecover";
        string MarkerPath(string tablesDir) { return Path.Combine(tablesDir, Key + ".interrupted"); }

        public bool ExperimentPending(string tablesDir) { return File.Exists(MarkerPath(tablesDir)); }

        /// <summary>
        /// Call before trying temporary settings. Saves what is set now, and asks Windows to run "--recover" at the
        /// next sign-in in case the PC goes down while an experimental table is loaded.
        /// </summary>
        public void BeginExperiment(string tablesDir, string exePath)
        {
            Directory.CreateDirectory(tablesDir);
            File.WriteAllBytes(MarkerPath(tablesDir), ReadOverride() ?? new byte[0]);   // an empty file means "no table was set"
            try { using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RunOnceKey)) k.SetValue(RunOnceName, "\"" + exePath + "\" --recover"); }
            catch (Exception) { /* the marker file alone still restores on the next start of the tool */ }
        }

        /// <summary>Puts back what was set before the experiment and restarts the GPU. Returns false if there was nothing to restore.</summary>
        public bool EndExperiment(string tablesDir, out RestartOutcome outcome, out string detail)
        {
            outcome = RestartOutcome.Restarted; detail = "";
            string marker = MarkerPath(tablesDir);
            if (!File.Exists(marker)) return false;
            byte[] before = File.ReadAllBytes(marker);
            if (before.Length == 0) DeleteOverride();
            else using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ClassKey, true)) k.SetValue(ValueName, before, RegistryValueKind.Binary);
            outcome = Restart(out detail);
            File.Delete(marker);
            try { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RunOnceKey, true)) if (k != null) k.DeleteValue(RunOnceName, false); }
            catch (Exception) { }
            return true;
        }

        // ---- restarting the device (the driver only reads the table when it starts) ----

        public RestartOutcome Restart(out string detail)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/restart-device \"" + InstanceId + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                detail = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                RestartOutcome r = Pnp.Interpret(p.ExitCode);
                if (r != RestartOutcome.Restarted) return r;
            }
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                if (IsRunning()) { Thread.Sleep(2000); return RestartOutcome.Restarted; }
            }
            detail = "The GPU did not come back within 30 seconds.";
            return RestartOutcome.Failed;
        }

        public bool IsRunning()
        {
            try
            {
                string wql = "SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID = '" + InstanceId.Replace(@"\", @"\\") + "'";
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(wql))
                    foreach (ManagementObject mo in q.Get())
                        return Convert.ToInt32(mo["ConfigManagerErrorCode"]) == 0;
            }
            catch (ManagementException) { }
            return false;
        }

        /// <summary>Programs holding memory on this GPU - they are likely to crash when it restarts. Best effort.</summary>
        public List<string> AppsUsingIt()
        {
            List<string> names = new List<string>();
            try
            {
                List<long> luids = new List<long>();
                using (RegistryKey dx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
                    foreach (string sub in dx.GetSubKeyNames())
                        using (RegistryKey k = dx.OpenSubKey(sub))
                        {
                            string d = k.GetValue("Description") as string;
                            object l = k.GetValue("AdapterLuid");
                            if (d != null && l != null && string.Equals(d.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)) luids.Add(Convert.ToInt64(l));
                        }
                if (luids.Count == 0) return names;

                int me = Process.GetCurrentProcess().Id;
                foreach (string inst in new PerformanceCounterCategory("GPU Process Memory").GetInstanceNames())
                {
                    bool mine = false;
                    foreach (long luid in luids) if (GpuCounters.IsOnAdapter(inst, luid)) mine = true;
                    int pid = GpuCounters.PidOf(inst);
                    if (!mine || pid <= 4 || pid == me) continue;
                    using (PerformanceCounter c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true))
                        if (c.RawValue <= 0) continue;
                    string n = Process.GetProcessById(pid).ProcessName;
                    if (!names.Contains(n)) names.Add(n);
                }
            }
            catch (Exception) { /* counters unavailable: the caller still shows its general warning */ }
            return names;
        }
    }
}
