using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Tuner
{
    /// <summary>
    /// Runs the calibration procedure (Calibration in Core.cs) against the real GPU: every measurement is
    /// "write the table, restart the GPU, run the load test". Whatever was set before is always put back.
    /// </summary>
    public static class Calibrator
    {
        const int OverheadSeconds = 15;                      // restart, settle, helper start-up - per step
        const string RunningName = @"Global\HeadroomCalibrationRunning";

        static string FilePath(Gpu gpu, string tablesDir) { return Path.Combine(tablesDir, gpu.Key + ".calibration"); }

        /// <summary>The saved calibration for this GPU, or null if there is none (or it is unreadable).</summary>
        public static CalibrationResult Load(Gpu gpu, string tablesDir)
        {
            try { return CalibrationResult.Parse(File.ReadAllText(FilePath(gpu, tablesDir))); }
            catch (IOException) { return null; }
            catch (FormatException) { return null; }
        }

        public static void Forget(Gpu gpu, string tablesDir)
        {
            try { File.Delete(FilePath(gpu, tablesDir)); } catch (IOException) { }
        }

        /// <summary>The longest a full run can take, in minutes, with tests of the given length.</summary>
        public static int MinutesFor(Limits limits, int testSeconds) { return (Calibration.MaxSteps(limits) * (testSeconds + OverheadSeconds) + 59) / 60; }

        /// <summary>True while a calibration is running in any Headroom process (so nobody "recovers" in the middle of it).</summary>
        public static bool IsRunning()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(RunningName, out m)) return false;
            m.Dispose();
            return true;
        }

        /// <summary>progress receives (step number, most steps possible, what is being measured).</summary>
        /// <param name="limits">The range to search: the one chosen in Settings, or the defaults.</param>
        /// <param name="measuring">Told true once the GPU is back and a test is about to run, false before it is restarted again.</param>
        public static CalibrationResult Run(Gpu gpu, byte[] reference, Limits limits, string tablesDir, int testSeconds,
                                            Action<int, int, string> progress, Action<bool> measuring, Func<bool> cancelled, out string restoreNote)
        {
            int total = Calibration.MaxSteps(limits), step = 0;
            CalibrationResult result;
            restoreNote = "";

            using (Mutex running = new Mutex(false, RunningName))
            {
                gpu.BeginExperiment(tablesDir, Process.GetCurrentProcess().MainModule.FileName);
                try
                {
                    result = Calibration.Run(limits,
                        delegate (Settings s)
                        {
                            string detail;
                            measuring(false);
                            RestartOutcome o = gpu.Apply(reference, s, null, out detail);
                            if (o != RestartOutcome.Restarted) throw new InvalidOperationException("Windows could not restart the GPU (" + detail + ")");
                            measuring(true);
                            Thread.Sleep(3000);   // let it settle and shed some heat before the next test
                            LoadResult r = LoadTest.RunInChild(testSeconds, gpu.Name, cancelled);
                            // a test that was stopped on request says nothing about stability: do not let it count as a failed step
                            if (!r.Ok && r.Error == LoadTest.Stopped) throw new OperationCanceledException();
                            return r;
                        },
                        delegate (string what) { step++; progress(step, total, what); },
                        cancelled);
                }
                catch (OperationCanceledException) { result = CalibrationResult.Failed("Stopped."); }
                catch (Exception ex) { result = CalibrationResult.Failed(ex.Message); }
                finally
                {
                    RestartOutcome outcome; string detail;
                    measuring(false);
                    gpu.EndExperiment(tablesDir, out outcome, out detail);
                    if (outcome != RestartOutcome.Restarted) restoreNote = " Your previous settings were written back, but the GPU could not be restarted - reboot to load them.";
                }
            }

            if (result.Ok)
            {
                Directory.CreateDirectory(tablesDir);
                File.WriteAllText(FilePath(gpu, tablesDir), result.ToText());
            }
            return result;
        }
    }
}
