using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Threading;

namespace KippixOverlay
{
    /// <summary>Samples CPU, memory, GPU and uptime on a background thread while the overlay is open.</summary>
    internal class SystemStats
    {
        public volatile int Cpu = -1;
        public double MemUsedGb, MemTotalGb;
        public volatile int GpuLoad = -1;
        public volatile int GpuTemp = -1;
        public double GpuPower = -1;
        public string GpuName;
        public TimeSpan Uptime;

        /// <summary>When the PC was last turned on or woken (from Shut down/hibernate or sleep).</summary>
        private DateTime? onSince;
        private DateTime onSinceCheckedAt = DateTime.MinValue;

        private volatile bool running;
        private volatile int generation;
        private long lastIdle, lastKernel, lastUser;
        private string nvidiaSmi;
        private bool nvidiaFailed;
        private readonly Dictionary<string, CounterSample> gpuPrev = new Dictionary<string, CounterSample>();

        public void Start()
        {
            if (running) return;
            running = true;
            nvidiaSmi = FindNvidiaSmi();
            int gen = ++generation;
            new Thread(() => Loop(gen)) { IsBackground = true, Name = "Kippix stats" }.Start();
        }

        public void Stop()
        {
            running = false;
        }

        // Newest of: Windows started, resumed from hibernate, woke from sleep.
        private static DateTime? LastPowerOn()
        {
            try
            {
                const string xpath =
                    "*[System[(Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12) or " +
                    "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) or " +
                    "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107)]]";
                var q = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using (var reader = new EventLogReader(q))
                using (EventRecord r = reader.ReadEvent())
                {
                    return r != null ? r.TimeCreated : null;
                }
            }
            catch (Exception) { return null; }
        }

        private static string FindNvidiaSmi()
        {
            string[] paths =
            {
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
            };
            foreach (string p in paths) if (File.Exists(p)) return p;
            return null;
        }

        private void Loop(int gen)
        {
            while (running && gen == generation)
            {
                try { SampleCpuMem(); } catch (Exception) { }
                try { SampleGpu(); } catch (Exception) { }
                for (int i = 0; i < 15 && running && gen == generation; i++) Thread.Sleep(100);
            }
        }

        private void SampleCpuMem()
        {
            long idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                if (lastKernel != 0)
                {
                    long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
                    long total = dk + du;
                    if (total > 0) Cpu = (int)Math.Round(Math.Max(0, Math.Min(100, (total - di) * 100.0 / total)));
                }
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }

            ulong totalMem, avail;
            if (Native.GetMemory(out totalMem, out avail))
            {
                MemTotalGb = totalMem / 1073741824.0;
                MemUsedGb = (totalMem - avail) / 1073741824.0;
            }

            if ((DateTime.Now - onSinceCheckedAt).TotalMinutes > 5)
            {
                onSinceCheckedAt = DateTime.Now;
                onSince = LastPowerOn();
            }
            TimeSpan sinceBoot = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            Uptime = onSince.HasValue && DateTime.Now - onSince.Value < sinceBoot ? DateTime.Now - onSince.Value : sinceBoot;
        }

        private void SampleGpu()
        {
            if (nvidiaSmi != null && !nvidiaFailed)
            {
                if (SampleNvidia()) return;
                nvidiaFailed = true;
            }
            SampleGpuCounters();
        }

        private bool SampleNvidia()
        {
            var psi = new ProcessStartInfo(nvidiaSmi, "--query-gpu=temperature.gpu,utilization.gpu,power.draw,name --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string line = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);
                if (string.IsNullOrEmpty(line)) return false;
                string[] parts = line.Split(',');
                if (parts.Length < 3) return false;
                int t, u;
                double w;
                if (int.TryParse(parts[0].Trim(), out t)) GpuTemp = t;
                if (int.TryParse(parts[1].Trim(), out u)) GpuLoad = u;
                if (double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w)) GpuPower = w;
                if (parts.Length > 3) GpuName = parts[3].Trim();
                return true;
            }
        }

        // Works on any GPU vendor (Windows 10 1709+), but has no temperature or power.
        private void SampleGpuCounters()
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine")) return;
            var cat = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollectionCollection all = cat.ReadCategory();
            InstanceDataCollection util = all["Utilization Percentage"];
            if (util == null) return;

            // Sum per physical adapter, take the busiest.
            var perAdapter = new Dictionary<string, double>();
            var seen = new HashSet<string>();
            foreach (InstanceData d in util.Values)
            {
                string name = d.InstanceName;
                if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                seen.Add(name);
                CounterSample prev;
                if (gpuPrev.TryGetValue(name, out prev))
                {
                    float v = CounterSample.Calculate(prev, d.Sample);
                    int i = name.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
                    string adapter = i >= 0 ? name.Substring(i, Math.Min(8, name.Length - i)) : "gpu";
                    double sum;
                    perAdapter.TryGetValue(adapter, out sum);
                    perAdapter[adapter] = sum + v;
                }
                gpuPrev[name] = d.Sample;
            }
            var stale = new List<string>();
            foreach (string k in gpuPrev.Keys) if (!seen.Contains(k)) stale.Add(k);
            foreach (string k in stale) gpuPrev.Remove(k);

            double best = -1;
            foreach (double v in perAdapter.Values) best = Math.Max(best, v);
            if (best >= 0) GpuLoad = (int)Math.Round(Math.Min(100, best));
        }
    }
}
