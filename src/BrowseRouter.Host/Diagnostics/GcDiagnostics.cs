using BrowseRouter.Host.Interop;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using CoreKernel32 = BrowseRouter.Core.Interop.Kernel32;

namespace BrowseRouter.Host.Diagnostics;

/// <summary>
/// Collects a runtime diagnostics snapshot for the Host daemon — managed heap,
/// GC generation counts, OS handles, threads, GDI/USER objects, and the thread
/// pool — and formats it for the log file and the <c>--gc</c> console report.
///
/// <para>
/// The GDI/USER object counts (<see cref="Snapshot.GdiObjects"/> /
/// <see cref="Snapshot.UserObjects"/>) are the headline numbers for the resource-
/// leak audit: a stable count across a forced GC means the toast/tray code is
/// not leaking brushes, fonts, regions, icons, or HWNDs. A count that only ever
/// grows indicates a leak the finalizer backstop is NOT recovering.
/// </para>
/// </summary>
internal static class GcDiagnostics
{
    /// <summary>
    /// One immutable point-in-time snapshot. All counts are best-effort reads;
    /// a field that could not be queried is left at 0 / default.
    /// </summary>
    internal sealed record Snapshot
    {
        // ── Managed heap / GC ────────────────────────────────────────────────
        public long TotalMemoryBytes; // GC.GetTotalMemory
        public long TotalAllocatedBytes; // GC.GetTotalAllocatedBytes (process lifetime)
        public long HeapSizeBytes; // GC.GetGCMemoryInfo().HeapSizeBytes
        public long PinnedObjectsCount; // GC.GetGCMemoryInfo().PinnedObjectsCount
        public long FinalizationPendingCount; // GC.GetGCMemoryInfo().FinalizationPendingCount
        public int Gen0Collections;
        public int Gen1Collections;
        public int Gen2Collections;

        // ── OS process ──────────────────────────────────────────────────────
        public int HandleCount; // kernel HANDLEs (file, event, mutex, …)
        public int ThreadCount;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
        public long PeakWorkingSetBytes;
        public long NonpagedSystemMemoryBytes;

        // ── Win32 GUI objects (the leak-audit headline) ─────────────────────
        public uint GdiObjects; // GetGuiResources(GR_GDIOBJECTS)
        public uint UserObjects; // GetGuiResources(GR_USEROBJECTS)

        // ── Thread pool ─────────────────────────────────────────────────────
        public int WorkerThreadsAvailable;
        public int IoThreadsAvailable;
        public int WorkerThreadsMax;
        public int IoThreadsMax;
    }

    /// <summary>
    /// Read every counter once. Safe to call from any thread; the only mutating
    /// side-effect is <see cref="System.Diagnostics.Process.Refresh"/> on a
    /// freshly-obtained current-process object.
    /// </summary>
    public static Snapshot Capture()
    {
        var s = new Snapshot();

        // ── GC / managed heap ───────────────────────────────────────────────
        try
        {
            s.TotalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        }
        catch
        {
            // ignored
        }

        try
        {
            s.TotalAllocatedBytes = GC.GetTotalAllocatedBytes();
        }
        catch
        {
            // ignored
        }

        try
        {
            var info = GC.GetGCMemoryInfo();
            s.HeapSizeBytes = info.HeapSizeBytes;
            s.PinnedObjectsCount = info.PinnedObjectsCount;
            s.FinalizationPendingCount = info.FinalizationPendingCount;
        }
        catch
        {
            // ignored
        }

        try
        {
            s.Gen0Collections = GC.CollectionCount(0);
        }
        catch
        {
            // ignored
        }

        try
        {
            s.Gen1Collections = GC.CollectionCount(1);
        }
        catch
        {
            // ignored
        }

        try
        {
            s.Gen2Collections = GC.CollectionCount(2);
        }
        catch
        {
            // ignored
        }

        // ── OS process counters ─────────────────────────────────────────────
        // Process.GetCurrentProcess() allocates a SafeProcessHandle; dispose it
        // so the diagnostics call itself doesn't add a lingering handle.
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            s.HandleCount = p.HandleCount;
            s.ThreadCount = p.Threads.Count;
            s.WorkingSetBytes = p.WorkingSet64;
            s.PrivateMemoryBytes = p.PrivateMemorySize64;
            s.PeakWorkingSetBytes = p.PeakWorkingSet64;
            s.NonpagedSystemMemoryBytes = p.NonpagedSystemMemorySize64;
        }
        catch
        {
            // ignored
        }

        // ── GDI / USER object counts via the current-process pseudo-handle ──
        // (no CloseHandle needed for a pseudo-handle).
        try
        {
            var h = CoreKernel32.GetCurrentProcess();
            s.GdiObjects = User32.GetGuiResources(h, User32.GrGdiObjects);
            s.UserObjects = User32.GetGuiResources(h, User32.GrUserObjects);
        }
        catch
        {
            // ignored
        }

        // ── Thread pool ─────────────────────────────────────────────────────
        try
        {
            ThreadPool.GetAvailableThreads(out s.WorkerThreadsAvailable, out s.IoThreadsAvailable);
            ThreadPool.GetMaxThreads(out s.WorkerThreadsMax, out s.IoThreadsMax);
        }
        catch
        {
            // ignored
        }

        return s;
    }

    /// <summary>
    /// Run a full, blocking, compacting GC (the most aggressive collection the
    /// runtime offers — also drains the finalizer queue so any finalizer-backed
    /// resource like <see cref="Notify.ToastWindow"/>'s backstop gets a chance
    /// to release), then capture the post-collection snapshot. Returns the
    /// snapshot plus the formatted report.
    /// </summary>
    public static (Snapshot Before, Snapshot After, string Report) RunForcedGc()
    {
        var before = Capture();

        // WaitForPendingFinalizers BEFORE collect lets pending finalizers from
        // prior allocations run first, so the "after" snapshot reflects what the
        // finalizer backstop actually freed. Then collect + finalizers again.
        try
        {
            GC.WaitForPendingFinalizers();
        }
        catch
        {
            // ignored
        }

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // A second collect reclaims objects whose finalizer just promoted
            // them to the next generation on the first pass.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        catch
        {
            // ignored
        }

        var after = Capture();
        return (before, after, Format(before, after));
    }

    /// <summary>
    /// Render a two-column before/after block with deltas. The GDI/USER/handle
    /// deltas are the leak-audit signal: a negative or zero delta after a forced
    /// GC means the finalizer recovered leaked handles; a growing delta across
    /// repeated <c>--gc</c> calls means a real leak.
    /// </summary>
    public static string Format(Snapshot before, Snapshot after)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("=== BrowseRouter (AOT) GC diagnostics ===");

        AppendCounters(sb, "Managed heap / GC", ("GC.GetTotalMemory", before.TotalMemoryBytes, after.TotalMemoryBytes),
            ("TotalAllocatedBytes (lifetime)", before.TotalAllocatedBytes, after.TotalAllocatedBytes),
            ("HeapSizeBytes", before.HeapSizeBytes, after.HeapSizeBytes),
            ("PinnedObjectsCount", before.PinnedObjectsCount, after.PinnedObjectsCount),
            ("FinalizationPendingCount", before.FinalizationPendingCount, after.FinalizationPendingCount),
            ("Gen0 collections", before.Gen0Collections, after.Gen0Collections),
            ("Gen1 collections", before.Gen1Collections, after.Gen1Collections),
            ("Gen2 collections", before.Gen2Collections, after.Gen2Collections));

        AppendCounters(sb, "OS process", ("Handles", before.HandleCount, after.HandleCount),
            ("Threads", before.ThreadCount, after.ThreadCount),
            ("WorkingSet", before.WorkingSetBytes, after.WorkingSetBytes),
            ("PrivateMemory", before.PrivateMemoryBytes, after.PrivateMemoryBytes),
            ("PeakWorkingSet", before.PeakWorkingSetBytes, after.PeakWorkingSetBytes),
            ("NonpagedSystemMemory", before.NonpagedSystemMemoryBytes, after.NonpagedSystemMemoryBytes));

        AppendCounters(sb, "Win32 GUI objects (leak audit)", ("GDI objects", before.GdiObjects, after.GdiObjects),
            ("USER objects", before.UserObjects, after.UserObjects));

        AppendCounters(sb, "Thread pool",
            ("Worker available", before.WorkerThreadsAvailable, after.WorkerThreadsAvailable),
            ("IO available", before.IoThreadsAvailable, after.IoThreadsAvailable),
            ("Worker max", before.WorkerThreadsMax, after.WorkerThreadsMax),
            ("IO max", before.IoThreadsMax, after.IoThreadsMax));

        return sb.ToString();
    }

    private static void AppendCounters(
        StringBuilder sb,
        string heading,
        params (string Label, long Before, long After)[] rows
    )
    {
        sb.AppendLine($"--- {heading} ---");
        sb.AppendLine("  metric                            before          after           delta");
        foreach (var (label, b, a) in rows)
        {
            var delta = a - b;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {label,-34} {b,14} {a,14} {delta,+14}");
        }

        sb.AppendLine();
    }
}