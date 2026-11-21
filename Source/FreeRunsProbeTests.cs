using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CastleStorySteam;

// GA3 offline self-test of FrpCore.Walk (runs on the MS .NET 4 framework, compiled with /langversion:5).
// It builds a FAKE mono.dll image in real native memory (same RVAs as the real module), fake heap sections (never dereferenced), bottom_index/hdr structs, free lists and black-list bitmaps,
// then runs the production walker through the production FrpLiveMem (VirtualQuery gate) and checks counters, bins, black-list counts and every negative path.
// It does NOT load the game's mono.dll and does not touch any process other than itself.
namespace CastleStorySteam
{
    public sealed partial class SteamBridgePlugin { internal static void PtLog(string s) { Console.WriteLine("PtLog: " + s); } }
}

static class FreeRunsProbeTests
{
    static int fails, checks;
    static void Check(bool ok, string what) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } else Console.WriteLine("ok:   " + what); }

    static IntPtr Alloc(int n) { IntPtr p = Marshal.AllocHGlobal(n); byte[] z = new byte[n]; Marshal.Copy(z, 0, p, n); return p; }
    static void W64(ulong a, ulong v) { Marshal.WriteInt64(new IntPtr((long)a), (long)v); }
    static ulong R64(ulong a) { return (ulong)Marshal.ReadInt64(new IntPtr((long)a)); }

    // builder
    static ulong imgBase; static ulong nils; static Dictionary<ulong, ulong> bis = new Dictionary<ulong, ulong>(); static ulong invalid = 0xABCD0000UL;
    static List<ulong> secStart = new List<ulong>(); static ulong[] heads = new ulong[61]; static Dictionary<ulong, ulong> hdrOf = new Dictionary<ulong, ulong>();

    static ulong BottomFor(ulong p)
    {
        ulong hi = p >> 22; ulong bi;
        if (bis.TryGetValue(hi, out bi)) return bi;
        bi = (ulong)Alloc(0x2020).ToInt64();
        ulong slot = imgBase + (ulong)FrpCore.RvaTopIndex + (hi & 0x7FF) * 8;
        W64(bi + 0x2010, hi); W64(bi + 0x2018, R64(slot)); W64(slot, bi);   // push on the hash chain like GC_install_header
        bis[hi] = bi; return bi;
    }

    // adds a free block of 'blocks' 4 KB blocks at address p to its list (at the head, like GC_add_to_fl)
    static ulong AddFree(ulong p, long blocks)
    {
        ulong bi = BottomFor(p); ulong h = (ulong)Alloc(0x80).ToInt64(); ulong sz = (ulong)blocks << 12;
        W64(h, sz); W64(h + 0x20, invalid);
        int li = FrpCore.FlFromBlocks((ulong)blocks);
        W64(h + 8, heads[li]); W64(h + 0x10, 0);
        if (heads[li] != 0) { ulong oh = hdrOf[heads[li]]; W64(oh + 0x10, p); }
        heads[li] = p; hdrOf[p] = h;
        W64(bi + ((p >> 12) & 0x3FF) * 8, h);
        W64(imgBase + (ulong)FrpCore.RvaHblkFreeList + (ulong)li * 8, p);
        return h;
    }

    static ulong lfSum;
    static void Build(out long expectBytes)
    {
        int size = 0x2DC000; IntPtr img = Alloc(size); imgBase = (ulong)img.ToInt64();
        nils = (ulong)Alloc(0x2020).ToInt64(); W64(nils + 0x2010, ulong.MaxValue);
        W64(imgBase + (ulong)FrpCore.RvaAllNils, nils);
        for (int i = 0; i < 2048; i++) W64(imgBase + (ulong)FrpCore.RvaTopIndex + (ulong)i * 8, nils);
        W64(imgBase + (ulong)FrpCore.RvaInvalidMap, invalid);
        // 3 fake heap sections of 16 MB (addresses are only numbers)
        ulong s0 = 0x0000020000000000UL;
        for (int i = 0; i < 3; i++) { ulong st = s0 + (ulong)i * 0x1001000UL; secStart.Add(st); W64(imgBase + (ulong)FrpCore.RvaHeapSects + (ulong)i * 16, st); W64(imgBase + (ulong)FrpCore.RvaHeapSects + (ulong)i * 16 + 8, 0x1000000UL); }
        W64(imgBase + (ulong)FrpCore.RvaNHeapSects, 3);
        W64(imgBase + (ulong)FrpCore.RvaGcNo, 245);
        // free runs: 1 blk x3, 2 blk x2, 5 blk x2, 40 blk, 100 blk x2, 300 blk, 4096 blk
        long[] runs = { 1, 1, 1, 2, 2, 5, 5, 40, 100, 100, 300, 4096 };
        ulong cur = secStart[0]; long total = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            ulong sect = secStart[i % 3]; ulong p = sect + (ulong)(i / 3) * 0x400000UL;   // different 4 MB slots so the bottom indexes differ
            AddFree(p, runs[i]); total += runs[i] * 4096;
        }
        expectBytes = total; lfSum = (ulong)total;
        W64(imgBase + (ulong)FrpCore.RvaLargeFree, lfSum);
        // black-list bitmaps: 4 x 128 KB. Mark page 3 of the 40-block run and page 0 and 50 of the 100-block run in old_stack_bl, page 7 of the 300-run in incomplete_stack_bl, page 0 of the 5-run in old_normal_bl
        ulong[] bl = new ulong[4]; for (int i = 0; i < 4; i++) bl[i] = (ulong)Alloc(0x20000).ToInt64();
        W64(imgBase + (ulong)FrpCore.RvaOldNormalBl, bl[0]); W64(imgBase + (ulong)FrpCore.RvaIncNormalBl, bl[1]); W64(imgBase + (ulong)FrpCore.RvaOldStackBl, bl[2]); W64(imgBase + (ulong)FrpCore.RvaIncStackBl, bl[3]);
    }

    static void SetBit(ulong arr, ulong pageAddr, int page)
    {
        int idx = (int)(((pageAddr >> 12) + (ulong)page) & 0xFFFFF); ulong a = arr + (ulong)(idx >> 6) * 8; W64(a, R64(a) | (1UL << (idx & 63)));
    }

    static ulong PageOfRun(long blocks, int nth) { int k = 0; foreach (KeyValuePair<ulong, ulong> kv in hdrOf) { if ((long)(R64(kv.Value) >> 12) == blocks) { if (k++ == nth) return kv.Key; } } return 0; }

    // performance scenario: n small free runs (1..8 blocks) at 64 KB spacing inside two huge fake sections, to time the walker on real memory through the VirtualQuery gate
    static void Perf(int n)
    {
        bis.Clear(); secStart.Clear(); hdrOf.Clear(); heads = new ulong[61];
        IntPtr img = Alloc(0x2DC000); imgBase = (ulong)img.ToInt64();
        nils = (ulong)Alloc(0x2020).ToInt64(); W64(nils + 0x2010, ulong.MaxValue); W64(imgBase + (ulong)FrpCore.RvaAllNils, nils);
        for (int i = 0; i < 2048; i++) W64(imgBase + (ulong)FrpCore.RvaTopIndex + (ulong)i * 8, nils);
        W64(imgBase + (ulong)FrpCore.RvaInvalidMap, invalid);
        ulong s0 = 0x0000010000000000UL; ulong span = (ulong)n * 0x10000UL + 0x100000UL;
        W64(imgBase + (ulong)FrpCore.RvaHeapSects, s0); W64(imgBase + (ulong)FrpCore.RvaHeapSects + 8, span); W64(imgBase + (ulong)FrpCore.RvaNHeapSects, 1); W64(imgBase + (ulong)FrpCore.RvaGcNo, 9);
        long total = 0; Random rnd = new Random(5);
        for (int i = 0; i < n; i++) { long b = 1 + rnd.Next(8); AddFree(s0 + (ulong)i * 0x10000UL, b); total += b * 4096; }
        W64(imgBase + (ulong)FrpCore.RvaLargeFree, (ulong)total);
        FrpLiveMem live = new FrpLiveMem(); FrpStats s = FrpCore.Walk(live, imgBase, 5000, false);
        Console.WriteLine("perf n=" + n + ": problem='" + s.Problem + "' nodes=" + s.Nodes + " walk=" + s.WalkMs.ToString("F1") + " ms VirtualQuery calls=" + live.Queries);
        s = FrpCore.Walk(live, imgBase, 5000, true);
        Console.WriteLine("perf n=" + n + " with bl bitmaps (not loaded in this scenario): walk=" + s.WalkMs.ToString("F1") + " ms problem='" + s.Problem + "'");
        s = FrpCore.Walk(live, imgBase, 3, false);
        Console.WriteLine("perf n=" + n + " budget 3 ms: problem='" + s.Problem + "'");
    }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "perf") { Perf(60000); return; }
        long expect; Build(out expect);
        FrpLiveMem live = new FrpLiveMem();
        FrpStats s = FrpCore.Walk(live, imgBase, 200, false);
        Check(s.Problem.Length == 0, "walk without bl: no problem (" + s.Problem + ")");
        Check(s.Bytes == expect, "sum of runs == expected " + expect + " got " + s.Bytes);
        Check(s.Nodes == 12 && s.SumOk, "12 nodes, sum plausibility ok");
        Check(s.BinRuns[0] == 3 && s.BinRuns[1] == 2 && s.BinRuns[2] == 2 && s.BinRuns[3] == 1 && s.BinRuns[4] == 2 && s.BinRuns[5] == 2, "bins 1/2-3/4-15/16-63/64-255/256+ = 3/2/2/1/2/2 runs");
        Check(s.MaxRun == 4096L * 4096, "max run 16 MB");
        Check(s.ListsUsed >= 6, "lists used " + s.ListsUsed);
        Console.WriteLine("text: " + FrpCore.Format(s, 1234567));

        // bl bits
        ulong r40 = PageOfRun(40, 0), r100a = PageOfRun(100, 0), r300 = PageOfRun(300, 0), r5 = PageOfRun(5, 0);
        ulong oldS = R64(imgBase + (ulong)FrpCore.RvaOldStackBl), incS = R64(imgBase + (ulong)FrpCore.RvaIncStackBl), oldN = R64(imgBase + (ulong)FrpCore.RvaOldNormalBl);
        SetBit(oldS, r40, 3); SetBit(oldS, r100a, 0); SetBit(oldS, r100a, 50); SetBit(incS, r300, 7); SetBit(oldN, r5, 0);
        s = FrpCore.Walk(live, imgBase, 200, true);
        Check(s.Problem.Length == 0, "walk with bl: no problem (" + s.Problem + ")");
        Check(s.StackBlPages == 4, "stack-black-listed free pages == 4, got " + s.StackBlPages);
        Check(s.NormalBlPages == 1, "normal-black-listed free pages == 1, got " + s.NormalBlPages);
        Check(s.FreePages == expect / 4096, "free pages " + s.FreePages);
        // largest clean run: 16 MB run has no bl page -> 4096
        Check(s.MaxClean == 4096, "max clean run 4096 pages, got " + s.MaxClean);
        Console.WriteLine("text: " + FrpCore.Format(s, -1));
        Console.WriteLine("scalars: " + FrpCore.Scalars(live, imgBase));

        // ---- negative paths ----
        // 1. sum mismatch
        W64(imgBase + (ulong)FrpCore.RvaLargeFree, lfSum + (200UL << 20));
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("sum of runs") >= 0 && FrpCore.Format(s, -1) == "", "sum mismatch -> problem, empty text: " + s.Problem);
        W64(imgBase + (ulong)FrpCore.RvaLargeFree, lfSum);
        // 2. node outside heap sections (corrupt hb_next of the head of list 60)
        ulong head60 = R64(imgBase + (ulong)FrpCore.RvaHblkFreeList + 60 * 8); ulong h60 = hdrOf[head60]; ulong keepNext = R64(h60 + 8);
        W64(h60 + 8, 0x00007FF000000000UL);
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("outside every heap section") >= 0, "wild next pointer -> rejected before any dereference: " + s.Problem);
        W64(h60 + 8, keepNext);
        // 3. cycle
        ulong n2 = keepNext; if (n2 != 0) { ulong h2 = hdrOf[n2]; ulong keep2 = R64(h2 + 8); W64(h2 + 8, head60); s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.Length != 0, "cycle -> problem: " + s.Problem); W64(h2 + 8, keep2); }
        // 4. top_index slot pointing to unmapped memory
        ulong slot = imgBase + (ulong)FrpCore.RvaTopIndex + ((head60 >> 22) & 0x7FF) * 8; ulong keepSlot = R64(slot);
        W64(slot, 0x00007FF0DEAD0000UL);
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("unreadable") >= 0, "top_index slot -> unmapped address: VirtualQuery gate rejects, no crash: " + s.Problem);
        W64(slot, keepSlot);
        // 5. hdr not free
        ulong keepMap = R64(h60 + 0x20); W64(h60 + 0x20, 7);
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("not marked free") >= 0, "hb_map != GC_invalid_map -> rejected: " + s.Problem);
        W64(h60 + 0x20, keepMap);
        // 6. size does not belong to list
        ulong keepSz = R64(h60); W64(h60, 7UL * 4096);
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("does not belong") >= 0, "size/list mismatch -> rejected: " + s.Problem);
        W64(h60, keepSz);
        // 7. gc_no changing mid-walk can't be simulated here; check the clean state is restored
        s = FrpCore.Walk(live, imgBase, 200, true); Check(s.Problem.Length == 0, "state restored, walk ok again");
        // 8. heap_sects count implausible
        W64(imgBase + (ulong)FrpCore.RvaNHeapSects, 99999);
        s = FrpCore.Walk(live, imgBase, 200, false); Check(s.Problem.IndexOf("implausible") >= 0, "n_heap_sects 99999 -> rejected: " + s.Problem);
        W64(imgBase + (ulong)FrpCore.RvaNHeapSects, 3);
        // 9. time budget: 0 ms budget aborts after the first 256 nodes only if there are that many; fake a long list
        Console.WriteLine("VirtualQuery calls so far: " + live.Queries);
        Console.WriteLine(fails == 0 ? "ALL " + checks + " CHECKS PASSED" : fails + " of " + checks + " CHECKS FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
