using System;
using System.Threading;
using Playnite.SDK;

namespace KippixShared
{
    /// <summary>
    /// (1.0.0) Background work that can't take Playnite down. An error left uncaught on a pool thread ends the whole
    /// app (.NET's rule, nothing Playnite can catch), so every piece of Kippix's background work goes through here:
    /// the same as ThreadPool.QueueUserWorkItem, with anything that slips through written to the log instead.
    /// </summary>
    internal static class SafeWork
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static int logged;

        public static bool Queue(WaitCallback work)
        {
            return ThreadPool.QueueUserWorkItem(state => Run(work, state));
        }

        public static bool Queue(WaitCallback work, object state)
        {
            return ThreadPool.QueueUserWorkItem(s => Run(work, s), state);
        }

        private static void Run(WaitCallback work, object state)
        {
            try { work(state); }
            catch (ThreadAbortException) { }
            catch (Exception ex)
            {
                try { if (Interlocked.Increment(ref logged) <= 50) logger.Error(ex, "Kippix: background work failed (caught, Playnite carries on)"); } catch (Exception) { }
            }
        }
    }
}
