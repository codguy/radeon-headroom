using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tuner
{
    static class Native
    {
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

        /// <summary>No "System Error" pop-ups from this process (AMD's compute library can trip one while unloading).</summary>
        public static void Quiet() { SetErrorMode(0x8003); }
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
        /// <summary>True when our output goes to a pipe or file (a script is reading it) rather than nowhere.</summary>
        public static bool OutputIsPiped() { uint t = GetFileType(GetStdHandle(-11)); return t == 1 || t == 3; }

        /// <summary>Ends the process immediately, skipping library unload (see Quiet).</summary>
        public static void HardExit() { TerminateProcess(GetCurrentProcess(), 0); }
    }

    /// <summary>
    /// A short, steady compute load on the chosen GPU (OpenCL) while its sensors are sampled twice a second.
    /// It also re-checks the GPU's answers: identical work must give identical results, so a mismatch means
    /// the settings are not stable. Runs in a helper process so a GPU restart never leaves stale handles here.
    /// </summary>
    public static class LoadTest
    {
        const string CL = "OpenCL.dll";
        [DllImport(CL)] static extern int clGetPlatformIDs(uint n, [Out] IntPtr[] p, out uint np);
        [DllImport(CL)] static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint n, [Out] IntPtr[] d, out uint nd);
        [DllImport(CL)] static extern int clGetDeviceInfo(IntPtr dev, uint param, UIntPtr size, [Out] byte[] val, out UIntPtr ret);
        [DllImport(CL)] static extern IntPtr clCreateContext(IntPtr props, uint n, IntPtr[] devs, IntPtr notify, IntPtr user, out int err);
        [DllImport(CL)] static extern IntPtr clCreateCommandQueue(IntPtr ctx, IntPtr dev, ulong props, out int err);
        [DllImport(CL, CharSet = CharSet.Ansi)] static extern IntPtr clCreateProgramWithSource(IntPtr ctx, uint count, string[] src, IntPtr lengths, out int err);
        [DllImport(CL, CharSet = CharSet.Ansi)] static extern int clBuildProgram(IntPtr prog, uint n, IntPtr[] devs, string opts, IntPtr notify, IntPtr user);
        [DllImport(CL, CharSet = CharSet.Ansi)] static extern IntPtr clCreateKernel(IntPtr prog, string name, out int err);
        [DllImport(CL)] static extern IntPtr clCreateBuffer(IntPtr ctx, ulong flags, UIntPtr size, IntPtr host, out int err);
        [DllImport(CL, EntryPoint = "clSetKernelArg")] static extern int clSetKernelArgMem(IntPtr k, uint idx, UIntPtr size, ref IntPtr val);
        [DllImport(CL, EntryPoint = "clSetKernelArg")] static extern int clSetKernelArgUInt(IntPtr k, uint idx, UIntPtr size, ref uint val);
        [DllImport(CL)] static extern int clEnqueueNDRangeKernel(IntPtr q, IntPtr k, uint dim, IntPtr off, UIntPtr[] gws, UIntPtr[] lws, uint nev, IntPtr wait, IntPtr ev);
        [DllImport(CL)] static extern int clEnqueueReadBuffer(IntPtr q, IntPtr buf, uint blocking, UIntPtr offset, UIntPtr size, [Out] byte[] dst, uint nev, IntPtr wait, IntPtr ev);
        [DllImport(CL)] static extern int clFinish(IntPtr q);

        const uint DeviceName = 0x102B, BoardNameAmd = 0x4038;
        const ulong DeviceTypeGpu = 4, MemReadWrite = 1;
        const int Elements = 1 << 21;   // 2 million floats = 8 MB of results per batch

        const string Kernel =
            "__kernel void burn(__global float* out, const uint iters) {\n" +
            "  uint i = get_global_id(0);\n" +
            "  float x = (float)(i % 1024u) * 0.001f + 0.5f; float acc = 0.0f;\n" +
            "  for (uint k = 0; k < iters; k++) { x = sin(x * 1.3f + acc); acc += sqrt(fabs(x) + 0.001f) * 0.01f; x += cos(acc); }\n" +
            "  out[i] = acc;\n" +
            "}\n";

        static volatile bool sampling;

        static string DeviceText(IntPtr dev, uint param)
        {
            byte[] b = new byte[512]; UIntPtr ret;
            if (clGetDeviceInfo(dev, param, new UIntPtr(512), b, out ret) != 0) return "";
            int len = Math.Max(0, (int)ret.ToUInt32() - 1);
            return Encoding.ASCII.GetString(b, 0, len).Trim();
        }

        static IntPtr FindDevice(string match)
        {
            IntPtr[] platforms = new IntPtr[8]; uint np;
            if (clGetPlatformIDs(8, platforms, out np) != 0) return IntPtr.Zero;
            for (int p = 0; p < np; p++)
            {
                IntPtr[] devs = new IntPtr[8]; uint nd;
                if (clGetDeviceIDs(platforms[p], DeviceTypeGpu, 8, devs, out nd) != 0) continue;
                for (int d = 0; d < nd; d++)
                    if ((DeviceText(devs[d], BoardNameAmd) + " " + DeviceText(devs[d], DeviceName)).IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                        return devs[d];
            }
            return IntPtr.Zero;
        }

        /// <summary>Runs the load in THIS process. Call only from the helper (it ends with Native.HardExit).</summary>
        public static LoadResult Run(int seconds, string gpuName)
        {
            using (Sensors sensors = Sensors.Open(gpuName))
            {
                if (sensors == null) return LoadResult.Failed("Could not read the GPU's sensors.");
                IntPtr dev;
                try { dev = FindDevice(gpuName); }
                catch (DllNotFoundException) { return LoadResult.Failed("OpenCL is not installed, so the GPU cannot be loaded for a test."); }
                if (dev == IntPtr.Zero) return LoadResult.Failed("This GPU was not offered for compute work, so nothing was run.");

                int err;
                IntPtr[] one = new IntPtr[] { dev };
                IntPtr ctx = clCreateContext(IntPtr.Zero, 1, one, IntPtr.Zero, IntPtr.Zero, out err);
                if (err != 0) return LoadResult.Failed("The GPU refused to start a compute job (error " + err + ").");
                IntPtr queue = clCreateCommandQueue(ctx, dev, 0UL, out err);
                IntPtr prog = clCreateProgramWithSource(ctx, 1, new string[] { Kernel }, IntPtr.Zero, out err);
                if (clBuildProgram(prog, 1, one, "", IntPtr.Zero, IntPtr.Zero) != 0) return LoadResult.Failed("The GPU driver could not prepare the test program.");
                IntPtr kernel = clCreateKernel(prog, "burn", out err);
                IntPtr mem = clCreateBuffer(ctx, MemReadWrite, new UIntPtr((uint)(Elements * 4)), IntPtr.Zero, out err);
                if (err != 0) return LoadResult.Failed("The GPU could not reserve memory for the test (error " + err + ").");
                clSetKernelArgMem(kernel, 0, new UIntPtr((uint)IntPtr.Size), ref mem);

                List<Reading> readings = new List<Reading>();
                sampling = true;
                Thread sampler = new Thread(delegate ()
                {
                    while (sampling)
                    {
                        Reading r = sensors.Read();
                        if (r != null) lock (readings) readings.Add(r);
                        Thread.Sleep(500);
                    }
                });
                sampler.Start();

                string failure = null;
                uint iters = 8;
                bool calibrated = false;
                byte[] reference = null, check = new byte[Elements * 4];
                int batch = 0;
                UIntPtr[] size = new UIntPtr[] { new UIntPtr((uint)Elements) };
                Stopwatch total = Stopwatch.StartNew();
                while (total.ElapsedMilliseconds < seconds * 1000L)
                {
                    clSetKernelArgUInt(kernel, 1, new UIntPtr(4), ref iters);
                    Stopwatch sw = Stopwatch.StartNew();
                    int rc = clEnqueueNDRangeKernel(queue, kernel, 1, IntPtr.Zero, size, null, 0, IntPtr.Zero, IntPtr.Zero);
                    if (rc == 0) rc = clFinish(queue);
                    if (rc != 0) { failure = "The GPU stopped responding during the test (error " + rc + "). These settings are not stable."; break; }
                    batch++;

                    if (!calibrated)
                    {
                        // Grow the batch until it takes ~0.1 s: long enough to keep the GPU busy, short enough for Windows' watchdog.
                        if (sw.ElapsedMilliseconds < 80 && iters < 32768) iters *= 2; else calibrated = true;
                    }
                    else if (reference == null || batch % 8 == 0)
                    {
                        rc = clEnqueueReadBuffer(queue, mem, 1, UIntPtr.Zero, new UIntPtr((uint)check.Length), check, 0, IntPtr.Zero, IntPtr.Zero);
                        if (rc != 0) { failure = "The GPU stopped responding during the test (error " + rc + "). These settings are not stable."; break; }
                        if (reference == null) { reference = (byte[])check.Clone(); }
                        else
                        {
                            for (int i = 0; i < check.Length; i++)
                                if (check[i] != reference[i]) { failure = "The GPU made calculation errors during the test. These settings are not stable."; break; }
                            if (failure != null) break;
                        }
                    }
                }
                sampling = false;
                sampler.Join();
                if (failure != null) return LoadResult.Failed(failure);
                lock (readings) return LoadResult.Summarize(readings);
            }
        }

        /// <summary>Starts this program again as a hidden helper ("--loadtest N GPU-name") and returns what it measured.</summary>
        /// <summary>The error text of a test that was stopped on request (not a failure of the GPU).</summary>
        public const string Stopped = "Stopped.";

        public static LoadResult RunInChild(int seconds, string gpuName) { return RunInChild(seconds, gpuName, null); }

        /// <summary>cancelled is polled five times a second; when it says yes the helper is ended and the result is "Stopped.".</summary>
        public static LoadResult RunInChild(int seconds, string gpuName, Func<bool> cancelled)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--loadtest " + seconds + " \"" + gpuName + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
            StringBuilder output = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                Stopwatch waited = Stopwatch.StartNew();
                while (!p.WaitForExit(200))
                {
                    bool stop = cancelled != null && cancelled();
                    if (!stop && waited.ElapsedMilliseconds < (seconds + 45) * 1000L) continue;
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    return LoadResult.Failed(stop ? Stopped : "The test hung and was stopped. These settings may not be stable.");
                }
                p.WaitForExit();   // lets the last output lines arrive
            }
            lock (output) return LoadResult.Parse(output.ToString());
        }
    }
}
