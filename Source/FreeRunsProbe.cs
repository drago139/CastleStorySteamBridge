using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CastleStorySteam
{
    // GA3 / FreeRunsProbe (C# 5, no ?. / $"" / nameof). READ ONLY, log only, no behaviour change.
    // Purpose: show HOW the free memory of the game's Boehm heap is cut up, so that "collection with 179-318 MB free" can be told apart:
    //   (a) fragmentation  = lots of free bytes but no free run long enough for the failing (large) request,
    //   (b) black-listing  = long free runs exist but they contain black-listed pages (GC_is_black_listed) and the allocator skips them.
    // Evidence for every offset: LIBGC-FRAGMENTATION.md section 2 (all RVAs read from the machine code of mono.dll sha256 5661FA36...295EA, ga3/verify_offsets.py re-checks them).
    // This build of libgc (6.x style, N_HBLK_FLS = 60) has NO GC_free_bytes[] array, so the only way to get the run-length distribution is to walk the 61 free lists
    // GC_hblkfreelist[0..60] (RVA 0x266D70) through the block headers (hb_sz / hb_next in the hdr found via GC_top_index -> bottom_index -> index[]).
    // Safety (same style as GpBoehmInit in GaProbe.cs, plus):
    //   * only when the loaded mono.dll file has exactly the analysed sha256 (GetModuleFileNameW, so a non-ASCII Steam path works);
    //   * every dereference outside the mono.dll image is checked with VirtualQuery first (committed, readable, not guard); regions are cached;
    //     a torn or wild pointer therefore ends the walk with a "problem" string instead of an access violation. Block memory itself is NEVER read (only headers, indexes, bitmaps);
    //   * every node is validated (page aligned, inside a GC_heap_sects section, hdr is a real pointer, hb_map == GC_invalid_map, size is a multiple of 4096 and belongs to this list,
    //     hb_prev matches), node cap, time budget, GC_gc_no must not change during the walk, and sum(sizes) must match GC_large_free_bytes (= the "free in heap" figure of GCPROBE);
    //   * every walk after the one-time self-test in FrpInit runs on its own BelowNormal background thread (CSB-FreeRuns), not on the Unity main thread; three failed plausibility checks switch the probe off for the session;
    //   * no per-walk large allocations (the four 128 KB bitmap buffers and the section arrays are allocated once), so the probe does not feed the fragmentation it measures;
    //   * returns "" (and keeps the reason in frpWhy) on any problem. Nothing is ever written.
    // Integration (PATCHSET-freeze): FrpInit(mono, ...) next to GpBoehmInit (GcProbe.cs), FrpText(freeHint, gcCount) appended to the GCPROBE line, FrpDrain() once per frame (PerfTrace.cs), FrpAllocdNow/FrpLargeObjNow in the GA-SUBMIT line (GaGuard.cs).

    internal interface IFrpMem
    {
        bool Ok(ulong addr, int len);   // true only if [addr, addr+len) is committed, readable memory
        ulong U64(ulong addr);          // caller has checked Ok()
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrpMbi { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, Pad1; public UIntPtr RegionSize; public uint State, Protect, Type, Pad2; }   // 48 bytes on x64

    internal sealed class FrpLiveMem : IFrpMem
    {
        [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")] static extern UIntPtr FrpVirtualQuery(IntPtr address, out FrpMbi buffer, UIntPtr length);
        readonly ulong[] lo = new ulong[24], hi = new ulong[24]; int n, next; readonly object gate = new object();
        public int Queries;

        // The region cache is shared by the worker thread (walks) and the main thread (GCPROBE scalars, GA-SUBMIT getters): one lock so a torn lo/hi pair can never vouch for unverified memory.
        public bool Ok(ulong a, int len)
        {
            lock (gate) { return OkLocked(a, len); }
        }

        bool OkLocked(ulong a, int len)
        {
            if (len <= 0 || a < 0x10000UL) return false;
            ulong end = a + (ulong)len;
            if (end < a || end > 0x00007FFFFFFFFFFFUL) return false;
            for (int i = 0; i < n; i++) if (a >= lo[i] && end <= hi[i]) return true;
            ulong cur = a; int guard = 0;
            while (cur < end && guard++ < 4)
            {
                FrpMbi m;
                if (FrpVirtualQuery(new IntPtr(unchecked((long)cur)), out m, (UIntPtr)(uint)Marshal.SizeOf(typeof(FrpMbi))) == UIntPtr.Zero) return false;
                Queries++;
                if (m.State != 0x1000u || m.Protect == 0u || (m.Protect & 0x101u) != 0u) return false;   // MEM_COMMIT, not PAGE_NOACCESS (1), not PAGE_GUARD (0x100)
                ulong rl = unchecked((ulong)m.BaseAddress.ToInt64()), rh = rl + m.RegionSize.ToUInt64();
                if (rh <= cur) return false;
                int s = next; next = (next + 1) % lo.Length; if (n < lo.Length) n++;
                lo[s] = rl; hi[s] = rh; cur = rh;
            }
            return cur >= end;
        }

        public ulong U64(ulong a) { return unchecked((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)a)))); }
    }

    internal sealed class FrpStats
    {
        public const int Bins = 6;
        public static readonly string[] BinName = { "1", "2-3", "4-15", "16-63", "64-255", "256+" };   // blocks of 4096 bytes per FREE RUN (a run = one coalesced free block = one free-list node)
        public long[] BinBytes = new long[Bins]; public int[] BinRuns = new int[Bins];
        public long Nodes, Bytes, MaxRun, FreePages, StackBlPages, NormalBlPages, MaxClean, AllocdBytes = -1, ThresholdBytes = -1, SpacingBytes = -1;
        public int ListsUsed, GcNo0, GcNo1; public long LargeFree0, LargeFree1; public double WalkMs; public int BlLoaded;
        public string Problem = ""; public bool SumOk;
    }

    internal static class FrpCore
    {
        // ---- RVAs in mono.dll 5661FA36...295EA (offsets are relative to the module base; every one is proven by machine code, see report section 2) ----
        public const long RvaHblkFreeList = 0x266D70;   // struct hblk* GC_hblkfreelist[61] (index = GC_hblk_fl_from_blocks)
        public const long RvaLargeFree = 0x279808;      // GC_large_free_bytes (= GC_get_free_bytes; mono_gc_get_used_size = GC_heapsize - this)
        public const long RvaHeapSize = 0x2797E0;       // GC_heapsize
        public const long RvaNHeapSects = 0x266BC0;     // word GC_n_heap_sects (max 4096)
        public const long RvaHeapSects = 0x27EAC8;      // struct HeapSect { ptr_t hs_start; word hs_bytes; } GC_heap_sects[4096]
        public const long RvaInvalidMap = 0x266F70;     // GC_invalid_map (hb_map of a free block)
        public const long RvaTopIndex = 0x2BACD0;       // bottom_index* GC_top_index[2048]
        public const long RvaAllNils = 0x2BACC8;        // bottom_index* GC_all_nils
        public const long RvaOldNormalBl = 0x269448, RvaIncNormalBl = 0x269450, RvaOldStackBl = 0x269458, RvaIncStackBl = 0x269460;   // word* bit arrays, 2^20 bits = 128 KB each
        public const long RvaAllInterior = 0x266C34;    // int GC_all_interior_pointers (never stored anywhere in mono.dll: 0)
        public const long RvaBlSpacing = 0x262BD8;      // word GC_black_list_spacing (init 256 KB, clamped 12 KB..16 MB by GC_promote_black_lists)
        public const long RvaTotalStackBl = 0x269468;   // word GC_total_stack_black_listed (bytes)
        public const long RvaWordsAllocd = 0x279828;    // GC_words_allocd (WORDS of 8 bytes since the last collection)
        public const long RvaLargeAllocd = 0x279810, RvaMaxLargeAllocd = 0x279818;   // bytes now in multi-block objects / its high-water mark
        public const long RvaWordsWasted = 0x279830;    // GC_words_wasted (words)
        public const long RvaGcNo = 0x266B88;          // GC_gc_no (word/ushort at the same address, low 32 bit read here)
        public const long RvaMaxHeapSize = 0x2797E8, RvaRequestedHeap = 0x2797F0, RvaCollectAtHeap = 0x262960, RvaFailCount = 0x266BD8, RvaWarnSupp = 0x266C38, RvaUnmapped = 0x27BA88;
        public const long RvaRootSize = 0x266C98, RvaDivisor = 0x262950;   // GC_root_size, GC_free_space_divisor (inputs of min_words_allocd @0x156278)
        public const int NFls = 60;                     // N_HBLK_FLS; arrays have 61 entries
        public const int NodeCap = 600000;

        public static int FlFromBlocks(ulong b) { return b <= 32 ? (int)b : (b >= 256 ? 60 : (int)((b - 32) / 8) + 32); }   // 0x15DE3C GC_hblk_fl_from_blocks
        public static int BinOf(long blocks) { return blocks <= 1 ? 0 : blocks <= 3 ? 1 : blocks <= 15 ? 2 : blocks <= 63 ? 3 : blocks <= 255 ? 4 : 5; }

        public static long Rd64(IFrpMem m, ulong b, long rva) { ulong a = b + (ulong)rva; return m.Ok(a, 8) ? unchecked((long)m.U64(a)) : -1; }
        public static int Rd32(IFrpMem m, ulong b, long rva) { ulong a = b + (ulong)rva; return m.Ok(a, 8) ? unchecked((int)(m.U64(a) & 0xFFFFFFFFUL)) : -1; }   // reads 8 bytes (all globals here sit in 8-byte aligned slots inside the image), uses the low dword

        // header of the block at p, 0 = none, 1 = problem (reason in why)
        static ulong Hdr(IFrpMem m, ulong top, ulong allNils, ulong p, ref string why)
        {
            ulong hi = p >> 22; ulong slot = top + (hi & 0x7FFUL) * 8;
            if (!m.Ok(slot, 8)) { why = "top_index slot unreadable"; return 1; }
            ulong ti = m.U64(slot);
            for (int guard = 0; guard < 64; guard++)
            {
                if (!m.Ok(ti, 0x2020)) { why = "bottom_index unreadable"; return 1; }
                ulong key = m.U64(ti + 0x2010);
                if (key == hi) break;
                if (ti == allNils) return 0;
                ti = m.U64(ti + 0x2018);
                if (guard == 63) { why = "bottom_index chain too long"; return 1; }
            }
            return m.U64(ti + ((p >> 12) & 0x3FFUL) * 8);
        }

        // The four 128 KB bit arrays are copied into buffers that are allocated ONCE (a per-walk new long[16384] would be 512 KB of large-object garbage every few seconds in the very heap we study).
        static readonly object walkLock = new object();
        static readonly long[][] bufs = new long[4][];
        static ulong[] ssBuf, seBuf;
        static long[] LoadBitmap(IFrpMem m, ulong b, long rva, int slot)
        {
            ulong ptr = (ulong)Rd64(m, b, rva);
            if (ptr == 0 || ptr == ulong.MaxValue || !m.Ok(ptr, 0x20000)) return null;
            if (bufs[slot] == null) bufs[slot] = new long[16384];
            long[] a = bufs[slot];
            Marshal.Copy(new IntPtr(unchecked((long)ptr)), a, 0, 16384);
            return a;
        }
        static bool Bit(long[] a, int idx) { return a != null && ((a[idx >> 6] >> (idx & 63)) & 1L) != 0; }

        // Walks all 61 free lists. withBl: also counts black-listed pages inside the free runs. Never throws (returns stats.Problem != "").
        public static FrpStats Walk(IFrpMem m, ulong b, int budgetMs, bool withBl)
        {
            lock (walkLock) { return WalkLocked(m, b, budgetMs, withBl); }
        }

        static FrpStats WalkLocked(IFrpMem m, ulong b, int budgetMs, bool withBl)
        {
            FrpStats s = new FrpStats(); long t0 = Stopwatch.GetTimestamp(); double tick = 1000.0 / Stopwatch.Frequency; string why = "";
            try
            {
                s.GcNo0 = Rd32(m, b, RvaGcNo); s.LargeFree0 = Rd64(m, b, RvaLargeFree);
                long nsect = Rd64(m, b, RvaNHeapSects), invalid = Rd64(m, b, RvaInvalidMap);
                if (nsect <= 0 || nsect > 4096 || invalid == -1 || s.LargeFree0 < 0) { s.Problem = "globals implausible (sects " + nsect + ")"; return s; }
                ulong sb = b + (ulong)RvaHeapSects;
                if (!m.Ok(sb, (int)nsect * 16)) { s.Problem = "heap_sects unreadable"; return s; }
                if (ssBuf == null) { ssBuf = new ulong[4096]; seBuf = new ulong[4096]; }
                ulong[] ss = ssBuf, se = seBuf;
                for (int i = (int)nsect; i < 4096; i++) { ss[i] = ulong.MaxValue; se[i] = ulong.MaxValue; }   // padding sorts last and never matches
                for (int i = 0; i < nsect; i++) { ss[i] = m.U64(sb + (ulong)i * 16); se[i] = ss[i] + m.U64(sb + (ulong)i * 16 + 8); if (ss[i] == 0 || (ss[i] & 0xFFF) != 0 || se[i] <= ss[i]) { s.Problem = "heap section " + i + " implausible"; return s; } }
                Array.Sort(ss, se, 0, (int)nsect);
                ulong top = b + (ulong)RvaTopIndex; long nils = Rd64(m, b, RvaAllNils);
                if (nils <= 0 || !m.Ok(top, 2048 * 8)) { s.Problem = "top_index unreadable"; return s; }
                long[] oldS = null, incS = null, oldN = null, incN = null;
                if (withBl)
                {
                    oldS = LoadBitmap(m, b, RvaOldStackBl, 0); incS = LoadBitmap(m, b, RvaIncStackBl, 1);
                    if (Rd32(m, b, RvaAllInterior) == 0) { oldN = LoadBitmap(m, b, RvaOldNormalBl, 2); incN = LoadBitmap(m, b, RvaIncNormalBl, 3); }
                    s.BlLoaded = (oldS != null ? 1 : 0) + (incS != null ? 1 : 0) + (oldN != null ? 1 : 0) + (incN != null ? 1 : 0);
                }
                long nodes = 0;
                for (int li = 0; li <= NFls; li++)
                {
                    ulong p = m.Ok(b + (ulong)RvaHblkFreeList + (ulong)li * 8, 8) ? m.U64(b + (ulong)RvaHblkFreeList + (ulong)li * 8) : 1;
                    ulong prev = 0; bool any = false;
                    while (p != 0)
                    {
                        if (p == 1) { s.Problem = "list head unreadable"; return s; }
                        if (++nodes > NodeCap) { s.Problem = "node cap (cycle?) in list " + li; return s; }
                        if ((nodes & 255) == 0 && (Stopwatch.GetTimestamp() - t0) * tick > budgetMs) { s.Problem = "time budget " + budgetMs + " ms exceeded after " + nodes + " nodes"; return s; }
                        if ((p & 0xFFF) != 0) { s.Problem = "node unaligned in list " + li; return s; }
                        int lo = 0, hiI = (int)nsect - 1, f = -1;
                        while (lo <= hiI) { int mid = (lo + hiI) >> 1; if (ss[mid] <= p) { f = mid; lo = mid + 1; } else hiI = mid - 1; }
                        if (f < 0 || p >= se[f]) { s.Problem = "node outside every heap section (list " + li + ")"; return s; }
                        ulong h = Hdr(m, top, (ulong)nils, p, ref why);
                        if (h == 1) { s.Problem = why; return s; }
                        if (h < 0x1000UL || !m.Ok(h, 0x30)) { s.Problem = "no header for free node (list " + li + ")"; return s; }
                        ulong sz = m.U64(h), nx = m.U64(h + 8), pv = m.U64(h + 0x10), map = m.U64(h + 0x20);
                        if (map != (ulong)invalid) { s.Problem = "node is not marked free (hb_map)"; return s; }
                        if (sz == 0 || (sz & 0xFFF) != 0 || sz > (1UL << 36)) { s.Problem = "bad hb_sz " + sz; return s; }
                        if (FlFromBlocks(sz >> 12) != li) { s.Problem = "size " + (sz >> 12) + " blocks does not belong to list " + li; return s; }
                        if (pv != prev) { s.Problem = "hb_prev mismatch (torn read)"; return s; }
                        long blocks = (long)(sz >> 12); int bin = BinOf(blocks);
                        s.BinBytes[bin] += (long)sz; s.BinRuns[bin]++; s.Bytes += (long)sz; s.Nodes++; if ((long)sz > s.MaxRun) s.MaxRun = (long)sz; any = true;
                        if (withBl && oldS != null)
                        {
                            long clean = 0;
                            for (long i = 0; i < blocks; i++)
                            {
                                int idx = (int)(((p >> 12) + (ulong)i) & 0xFFFFFUL);
                                bool sb1 = Bit(oldS, idx) || Bit(incS, idx), nb1 = Bit(oldN, idx) || Bit(incN, idx);
                                s.FreePages++;
                                if (nb1) s.NormalBlPages++;
                                if (sb1) { s.StackBlPages++; clean = 0; } else { clean++; if (clean > s.MaxClean) s.MaxClean = clean; }
                            }
                        }
                        prev = p; p = nx;
                    }
                    if (any) s.ListsUsed++;
                }
                s.GcNo1 = Rd32(m, b, RvaGcNo); s.LargeFree1 = Rd64(m, b, RvaLargeFree);
                if (s.GcNo1 != s.GcNo0) { s.Problem = "a collection ran during the walk (gc_no " + s.GcNo0 + " -> " + s.GcNo1 + ")"; return s; }
                long lo2 = Math.Min(s.LargeFree0, s.LargeFree1), hi2 = Math.Max(s.LargeFree0, s.LargeFree1), tol = Math.Max(4L << 20, hi2 / 50);
                s.SumOk = s.Bytes >= lo2 - tol && s.Bytes <= hi2 + tol;
                long wa = Rd64(m, b, RvaWordsAllocd), hs = Rd64(m, b, RvaHeapSize), rs = Rd64(m, b, RvaRootSize), dv = Rd64(m, b, RvaDivisor), spc = Rd64(m, b, RvaBlSpacing);
                if (wa >= 0) s.AllocdBytes = wa * 8;
                if (hs > 0 && rs >= 0 && dv >= 2 && dv <= 64) s.ThresholdBytes = (hs - (3 * s.LargeFree1) / 4 + rs + 20000) / dv;   // min_words_allocd * 8 (clause-off collection rule)
                s.SpacingBytes = spc;
                if (!s.SumOk) s.Problem = "sum of runs " + (s.Bytes >> 10) + " KB != GC_large_free_bytes " + (s.LargeFree0 >> 10) + ".." + (s.LargeFree1 >> 10) + " KB";
            }
            catch (Exception e) { s.Problem = "exception " + e.GetType().Name; }
            finally { s.WalkMs = (Stopwatch.GetTimestamp() - t0) * tick; }
            return s;
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture); }

        // "free runs: 1blk 12.4MB/3105 2-3 ... 256+ 83.3MB/12; max 6144KB; sum ok; bl ..."  (MB/number of runs)
        public static string Format(FrpStats s, long freeHint)
        {
            if (s == null) return "";
            if (s.Problem.Length != 0) return "";
            StringBuilder sb = new StringBuilder(220);
            sb.Append("free runs ").Append(Mb(s.Bytes)).Append("MB in ").Append(s.Nodes).Append(" runs:");
            for (int i = 0; i < FrpStats.Bins; i++) sb.Append(' ').Append(FrpStats.BinName[i]).Append("blk ").Append(Mb(s.BinBytes[i])).Append("MB/").Append(s.BinRuns[i]);
            sb.Append("; max ").Append(s.MaxRun >> 10).Append("KB; lf ").Append(Mb(s.LargeFree1));
            if (s.AllocdBytes >= 0 && s.ThresholdBytes > 0) sb.Append("; allocd ").Append(Mb(s.AllocdBytes)).Append('/').Append(Mb(s.ThresholdBytes)).Append("MB");
            if (freeHint >= 0) sb.Append(" hint ").Append(Mb(freeHint));
            if (s.BlLoaded > 0) sb.Append("; bl stackPg ").Append(s.StackBlPages).Append('/').Append(s.FreePages).Append(" normPg ").Append(s.NormalBlPages).Append(" maxClean ").Append(s.MaxClean * 4).Append("KB");
            sb.Append("; ").Append(s.WalkMs.ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
            return sb.ToString();
        }

        // cheap scalar line (a handful of 8-byte reads, no walk): for EVERY GCPROBE line
        public static string Scalars(IFrpMem m, ulong b)
        {
            try
            {
                long wa = Rd64(m, b, RvaWordsAllocd), lg = Rd64(m, b, RvaLargeAllocd), mlg = Rd64(m, b, RvaMaxLargeAllocd), ww = Rd64(m, b, RvaWordsWasted), sp = Rd64(m, b, RvaBlSpacing), sbl = Rd64(m, b, RvaTotalStackBl);
                long un = Rd64(m, b, RvaUnmapped), ns = Rd64(m, b, RvaNHeapSects), mh = Rd64(m, b, RvaMaxHeapSize);
                int ai = Rd32(m, b, RvaAllInterior), fc = Rd32(m, b, RvaFailCount), ws = Rd32(m, b, RvaWarnSupp);
                if (wa < 0 || lg < 0 || sp < 0) return "";
                return "; boehm allocd " + Mb(wa * 8) + "MB largeObj " + Mb(lg) + "/" + Mb(mlg) + "MB wasted " + Mb(ww * 8) + "MB blSpacing " + (sp >> 10) + "KB stackBl " + (sbl >> 10) + "KB allInterior " + ai + " sects " + ns + " unmapped " + Mb(un) + "MB failCnt " + fc + " warnSupp " + ws + " maxHeap " + mh;
            }
            catch (Exception) { return ""; }
        }
    }

    public sealed partial class SteamBridgePlugin
    {
        const string FrpMonoSha256 = "5661FA36BDC08AD91562F609536A3235BDBA113901920F1700C51FB8245295EA";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")] static extern uint FrpGetModuleFileNameW(IntPtr module, StringBuilder name, uint size);

        static bool frpOn, frpDisabled, frpBl, frpStop; static ulong frpBase; static IFrpMem frpMem; static Thread frpThread; static int frpFails, frpIntervalMs = 4000;
        static string frpWhy = ""; static readonly object frpLock = new object();
        static FrpStats frpPre, frpPost; static long frpPreAt, frpPostWantedAt, frpNextAt; static int frpPreGc, frpPostWantedGc, frpPostDoneGc, frpPostLogged;

        // Call once next to GpBoehmInit (mono = GetModuleHandleA("mono.dll")). enabled/interval/withBl come from [Diag] config entries bound by the caller.
        static void FrpInit(IntPtr mono, bool enabled, int intervalMs, bool withBl, ref string note)
        {
            try
            {
                if (!enabled || mono == IntPtr.Zero) return;
                StringBuilder sb = new StringBuilder(1024);
                if (FrpGetModuleFileNameW(mono, sb, 1024) == 0) { note += "; frp: mono.dll path unknown"; return; }
                byte[] hash;
                using (System.IO.FileStream fs = System.IO.File.OpenRead(sb.ToString())) hash = System.Security.Cryptography.SHA256.Create().ComputeHash(fs);
                string hex = BitConverter.ToString(hash).Replace("-", "");
                if (hex != FrpMonoSha256) { note += "; frp off: mono.dll sha256 " + hex.Substring(0, 8) + " is not the analysed build"; return; }
                if (IntPtr.Size != 8) { note += "; frp off: not a 64-bit process"; return; }
                frpBase = unchecked((ulong)mono.ToInt64()); frpMem = new FrpLiveMem(); frpBl = withBl; frpIntervalMs = Math.Max(1000, Math.Min(60000, intervalMs));
                FrpStats t = FrpCore.Walk(frpMem, frpBase, 250, withBl);   // self-test on the calling thread, once; a problem here (empty heap, offsets wrong) just switches the probe off
                if (t.Problem.IndexOf("collection ran") >= 0) t = FrpCore.Walk(frpMem, frpBase, 250, withBl);   // a collection tore the first walk: once more
                if (t.Problem.Length != 0 && FrpCore.Rd64(frpMem, frpBase, FrpCore.RvaNHeapSects) > 0) { note += "; frp off: self-test " + t.Problem; return; }
                frpOn = true; frpNextAt = Stopwatch.GetTimestamp();
                note += "; frp self-test ok (" + FrpCore.Format(t, -1) + ")";
                frpThread = new Thread(new ThreadStart(FrpWorker)); frpThread.IsBackground = true; frpThread.Name = "CSB-FreeRuns"; frpThread.Priority = ThreadPriority.BelowNormal; frpThread.Start();
            }
            catch (Exception e) { frpOn = false; note += "; frp failed: " + e.GetType().Name; }
        }

        static void FrpStopNow() { frpStop = true; }

        static long FrpTicks(int ms) { return (long)ms * Stopwatch.Frequency / 1000L; }

        static void FrpWorker()
        {
            while (!frpStop && !frpDisabled)
            {
                try
                {
                    Thread.Sleep(200);
                    long now = Stopwatch.GetTimestamp(); int want; long wantAt; lock (frpLock) { want = frpPostWantedGc; wantAt = frpPostWantedAt; }
                    bool post = want != 0 && want != frpPostDoneGc && now - wantAt >= FrpTicks(300) && FrpGcNoNow() >= want;   // 300 ms after the collection frame: lists are settled
                    bool pre = now >= frpNextAt;
                    if (!post && !pre) continue;
                    if (pre) frpNextAt = now + FrpTicks(frpIntervalMs);
                    FrpStats s = FrpCore.Walk(frpMem, frpBase, 200, frpBl);
                    if (s.Problem.Length != 0)
                    {
                        lock (frpLock) { frpWhy = s.Problem; if (s.Problem.IndexOf("collection ran") < 0) { frpFails++; if (frpFails >= 3) frpDisabled = true; } }   // a walk torn by a collection is expected, not a failure
                        continue;
                    }
                    lock (frpLock)
                    {
                        if (post) { frpPost = s; frpPostDoneGc = want; } else { frpPre = s; frpPreAt = now; frpPreGc = s.GcNo1; }
                        frpFails = 0;
                    }
                    if (s.WalkMs > 60) frpIntervalMs = Math.Min(60000, frpIntervalMs * 2);   // too slow for this heap: back off
                }
                catch (ThreadAbortException) { return; }
                catch (Exception e) { lock (frpLock) { frpWhy = "worker " + e.GetType().Name; frpDisabled = true; } }
            }
        }

        static int FrpGcNoNow() { return FrpCore.Rd32(frpMem, frpBase, FrpCore.RvaGcNo); }

        // Cheap getters (two 8-byte reads, any thread) for the GA-SUBMIT line: bytes allocated since the last collection (GC_words_allocd * 8, reset by every collection)
        // and bytes currently held by multi-block ("large") objects (GC_large_allocd_bytes). -1 = probe off. Use in GaPre/GaPost: the delta over one ForceSubmit call is the allocation volume of that call
        // when no collection ran inside it (compare GC_gc_no / GC.CollectionCount).
        static long FrpAllocdNow() { if (!frpOn || frpMem == null) return -1; long w = FrpCore.Rd64(frpMem, frpBase, FrpCore.RvaWordsAllocd); return w < 0 ? -1 : w * 8; }
        static long FrpLargeObjNow() { if (!frpOn || frpMem == null) return -1; return FrpCore.Rd64(frpMem, frpBase, FrpCore.RvaLargeAllocd); }

        // Appended to the GCPROBE line (main thread, from GpOnGc): scalar counters + the last PRE-collection snapshot (taken by the worker at most frpIntervalMs before the collection)
        // and a request for the POST snapshot (the worker logs it ~0.3 s later through FrpDrain as "FRP POST ...").
        static string FrpText(long freeHintBytes, int gcCount)
        {
            if (!frpOn) return "";
            if (frpDisabled) return "; frp off (" + frpWhy + ")";
            try
            {
                string sc = FrpCore.Scalars(frpMem, frpBase), pre = ""; FrpStats p; long at; int g;
                lock (frpLock) { p = frpPre; at = frpPreAt; g = frpPreGc; frpPostWantedGc = gcCount; frpPostWantedAt = Stopwatch.GetTimestamp(); }
                if (p != null)
                {
                    double age = (Stopwatch.GetTimestamp() - at) / (double)Stopwatch.Frequency;
                    pre = "; PRE(gc#" + g + ", " + age.ToString("F1", CultureInfo.InvariantCulture) + "s before) " + FrpCore.Format(p, freeHintBytes);
                }
                else if (frpWhy.Length != 0) pre = "; frp no snapshot (" + frpWhy + ")";
                return sc + pre;
            }
            catch (Exception) { return ""; }
        }

        // once per frame (main thread): emits the POST line when the worker has finished it
        static void FrpDrain()
        {
            if (!frpOn) return;
            FrpStats s = null; int g = 0;
            lock (frpLock) { if (frpPost != null && frpPostDoneGc != frpPostLogged) { s = frpPost; g = frpPostDoneGc; frpPostLogged = g; } }
            if (s != null) PtLog("FRP POST gc#" + g + " " + FrpCore.Format(s, -1));
        }
    }
}
