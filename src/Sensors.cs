using System;
using System.Runtime.InteropServices;

namespace Tuner
{
    /// <summary>Live readings from AMD's sensor log (the same numbers the Adrenalin "Metrics" tab shows). Read-only.</summary>
    public sealed class Sensors : IDisposable
    {
        delegate IntPtr AllocCb(int size);
        static IntPtr Alloc(int size) { return Marshal.AllocCoTaskMem(size); }
        static readonly AllocCb alloc = new AllocCb(Alloc);

        const string Dll = "atiadlxx.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Main_Control_Create(AllocCb cb, int connectedOnly, out IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr ctx, IntPtr info, int size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL2_New_QueryPMLogData_Get(IntPtr ctx, int adapter, IntPtr data);

        const int AdapterInfoSize = 1572, IndexOffset = 4, NameOffset = 280, BufSize = 4096;
        const int SensorClock = 1, SensorEdge = 8, SensorActivity = 19, SensorVoltage = 21, SensorPower = 23, SensorHotspot = 27;

        IntPtr ctx, buf;
        int adapter;
        Sensors() { }

        /// <summary>Returns null when AMD's library or the adapter is not available.</summary>
        public static Sensors Open(string nameContains)
        {
            try
            {
                IntPtr ctx;
                if (ADL2_Main_Control_Create(alloc, 1, out ctx) != 0) return null;
                int n, found = -1;
                ADL2_Adapter_NumberOfAdapters_Get(ctx, out n);
                if (n > 0)
                {
                    IntPtr info = Marshal.AllocHGlobal(AdapterInfoSize * n);
                    try
                    {
                        for (int i = 0; i < AdapterInfoSize * n; i += 4) Marshal.WriteInt32(info, i, 0);
                        ADL2_Adapter_AdapterInfo_Get(ctx, info, AdapterInfoSize * n);
                        for (int i = 0; i < n && found < 0; i++)
                        {
                            IntPtr p = new IntPtr(info.ToInt64() + (long)i * AdapterInfoSize);
                            string name = Marshal.PtrToStringAnsi(new IntPtr(p.ToInt64() + NameOffset));
                            if (name != null && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) found = Marshal.ReadInt32(p, IndexOffset);
                        }
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
                if (found < 0) { ADL2_Main_Control_Destroy(ctx); return null; }
                Sensors s = new Sensors();
                s.ctx = ctx; s.adapter = found; s.buf = Marshal.AllocHGlobal(BufSize);
                return s;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>Returns null if the driver did not answer (for example while the GPU is restarting).</summary>
        public Reading Read()
        {
            if (ctx == IntPtr.Zero) return null;
            for (int i = 0; i < BufSize; i += 4) Marshal.WriteInt32(buf, i, 0);
            if (ADL2_New_QueryPMLogData_Get(ctx, adapter, buf) != 0) return null;
            return new Reading(V(SensorClock), V(SensorVoltage), V(SensorPower), V(SensorEdge), V(SensorHotspot), V(SensorActivity));
        }

        // The log is { int size; { int supported; int value; } sensors[]; }
        int V(int sensor) { return Marshal.ReadInt32(buf, 4 + sensor * 8) != 0 ? Marshal.ReadInt32(buf, 8 + sensor * 8) : 0; }

        public void Dispose()
        {
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
            if (ctx != IntPtr.Zero) { ADL2_Main_Control_Destroy(ctx); ctx = IntPtr.Zero; }
        }
    }
}
