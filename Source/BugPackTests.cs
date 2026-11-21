using System;
using System.Collections.Generic;
using CastleStorySteam;

// BugPackRules, OrderMarks and TokenBucket (PATCHSET-bug-pack) without the game: the log rules of PickerGuard, the dead-entry prune of the catapult target list, the damaged-block fallback decision,
// the own-order marks and the click-sound mute of CatapultClickLocal, the rate limits of ClickLog and the catapult log, and the name filter of ClickLog. The glue in the hooks (Harmony, Unity) is NOT
// covered here: HookTest\Test-BugPackHooks.ps1 checks that against the real game assemblies.
static class BugPackTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    sealed class Item { internal bool Dead; internal string Name; internal Item(string n, bool dead) { Name = n; Dead = dead; } }

    static int Main()
    {
        try
        {
            // PickerGuard log throttle: first 20, then every 1000th
            int logged = 0;
            for (int n = 1; n <= 5000; n++) if (BugPackRules.LogSwallowed(n)) logged++;
            Check(logged == 20 + 5, "20 first + 5 of the 1000th = " + logged);
            Check(!BugPackRules.LogSwallowed(0) && BugPackRules.LogSwallowed(1) && BugPackRules.LogSwallowed(20) && !BugPackRules.LogSwallowed(21) && !BugPackRules.LogSwallowed(999) && BugPackRules.LogSwallowed(1000) && BugPackRules.LogSwallowed(2000), "throttle edges");

            // PickerGuard per-signature lines: first sight, the 100th repeat, every 1000th; never for 0
            int sigLines = 0;
            for (int n = 1; n <= 5000; n++) if (BugPackRules.LogSignature(n)) sigLines++;
            Check(sigLines == 1 + 1 + 5, "signature lines: first + 100th + 5 of the 1000th = " + sigLines);
            Check(BugPackRules.LogSignature(1) && !BugPackRules.LogSignature(2) && !BugPackRules.LogSignature(99) && BugPackRules.LogSignature(100) && !BugPackRules.LogSignature(101) && BugPackRules.LogSignature(1000) && !BugPackRules.LogSignature(0), "signature edges");

            // catapult list prune
            List<Item> list = new List<Item> { new Item("a", false), new Item("dead1", true), new Item("b", false), new Item("dead2", true), new Item("dead3", true) };
            Check(BugPackRules.RemoveDead(list, delegate(Item i) { return i.Dead; }) == 3, "prune removes 3");
            Check(list.Count == 2 && list[0].Name == "a" && list[1].Name == "b", "prune keeps the live ones in order");
            Check(BugPackRules.RemoveDead(list, delegate(Item i) { return i.Dead; }) == 0 && list.Count == 2, "second prune removes nothing");
            Check(BugPackRules.RemoveDead<Item>(null, delegate(Item i) { return true; }) == 0, "null list");
            Check(BugPackRules.RemoveDead(list, null) == 0 && list.Count == 2, "null predicate");
            List<Item> empty = new List<Item>();
            Check(BugPackRules.RemoveDead(empty, delegate(Item i) { return true; }) == 0, "empty list");
            List<Item> allDead = new List<Item> { new Item("x", true), new Item("y", true) };
            Check(BugPackRules.RemoveDead(allDead, delegate(Item i) { return i.Dead; }) == 2 && allDead.Count == 0, "all dead");
            List<Item> withNull = new List<Item> { null, new Item("k", false), null };
            Check(BugPackRules.RemoveDead(withNull, delegate(Item i) { return i == null; }) == 2 && withNull.Count == 1 && withNull[0].Name == "k", "null entries (non-catapult selection) removed");
            // the split into "no component" and "destroyed" that PickerGuard counts inside its predicate
            List<Item> mixed = new List<Item> { null, new Item("d", true), new Item("ok", false), null };
            int none = 0, gone = 0;
            int removedMixed = BugPackRules.RemoveDead(mixed, delegate(Item i) { if (i == null) { none++; return true; } if (i.Dead) { gone++; return true; } return false; });
            Check(removedMixed == 3 && none == 2 && gone == 1 && mixed.Count == 1, "split counts of the prune");

            // damaged block fallback
            Check(BugPackRules.UnusableDamageMaterials(0, false), "empty array is unusable");
            Check(BugPackRules.UnusableDamageMaterials(1, true), "array holding a missing material is unusable");
            Check(!BugPackRules.UnusableDamageMaterials(1, false), "a real crack material is kept");
            Check(!BugPackRules.UnusableDamageMaterials(4, false), "several real materials are kept");

            // sound mute: only the one click, only while muted
            Check(BugPackRules.SoundAllowed(false, "Catapult_Set_Target"), "not muted: the click plays");
            Check(!BugPackRules.SoundAllowed(true, "Catapult_Set_Target"), "muted reaction: the click is skipped");
            Check(BugPackRules.SoundAllowed(true, "Catapult_Move"), "muted reaction: other sounds play");
            Check(BugPackRules.SoundAllowed(true, null), "muted reaction: unnamed sound plays");
            Check(BugPackRules.SoundAllowed(true, "catapult_set_target"), "name match is exact (case)");

            // own-order marks
            OrderMarks marks = new OrderMarks();
            Check(!marks.IsFresh(7, 0f), "no mark yet");
            marks.Mark(7, 100f);
            Check(marks.IsFresh(7, 100f) && marks.IsFresh(7, 105.9f) && marks.IsFresh(7, 106f), "fresh up to 6 s");
            Check(!marks.IsFresh(7, 106.01f), "expired after 6 s");
            Check(!marks.IsFresh(8, 100f), "other catapult has no mark");
            marks.Mark(7, 200f);
            Check(marks.IsFresh(7, 205f) && !marks.IsFresh(7, 207f), "a new order renews the mark");
            marks.Clear();
            Check(marks.Count == 0 && !marks.IsFresh(7, 200f), "clear");
            // table size stays bounded; expired marks go first, live ones survive
            OrderMarks big = new OrderMarks();
            for (int i = 0; i < BugPackRules.MaxOwnMarks; i++) big.Mark(i, 0f);
            Check(big.Count == BugPackRules.MaxOwnMarks, "table filled");
            big.Mark(100000, 50f);
            Check(big.Count == 1 && big.IsFresh(100000, 50f), "expired marks trimmed, the new one kept");
            OrderMarks live = new OrderMarks();
            for (int i = 0; i < BugPackRules.MaxOwnMarks; i++) live.Mark(i, 10f);
            live.Mark(100000, 11f);
            Check(live.Count <= BugPackRules.MaxOwnMarks && live.IsFresh(100000, 11f), "all live: table cleared, the newest kept");

            // a typical host-local / guest sequence: order, then the reaction within a frame; remote reaction without an order is muted
            OrderMarks seq = new OrderMarks();
            seq.Mark(42, 10f);
            bool reactMine = seq.IsFresh(42, 10.02f), reactRemote = seq.IsFresh(43, 10.02f);
            Check(reactMine && !reactRemote, "own order allowed, remote order muted");
            Check(!BugPackRules.SoundAllowed(!reactRemote, BugPackRules.CatapultClickSound), "remote reaction mutes the click");
            Check(BugPackRules.SoundAllowed(!reactMine, BugPackRules.CatapultClickSound), "own reaction plays the click");

            // token bucket (ClickLog settings): burst, sustained rate, refill, skipped counter, reset
            TokenBucket budget = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond);
            int accepted = 0;
            for (int i = 0; i < 200; i++) if (budget.Allow(1000.0)) accepted++;
            Check(accepted == BugPackRules.ClickBurst, "burst of exactly " + BugPackRules.ClickBurst + " lines, got " + accepted);
            Check(budget.TakeDropped() == 200 - BugPackRules.ClickBurst, "dropped counted");
            Check(budget.TakeDropped() == 0, "dropped counter is reset by TakeDropped");
            Check(!budget.Allow(1000.2), "0.2 s later still no token");
            Check(budget.Allow(1001.1), "1.1 s later one token is back");
            Check(!budget.Allow(1001.1), "and only one");
            // a steady rate just below the limit is never refused
            TokenBucket steady = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond); bool refused = false;
            for (int i = 0; i < 20000; i++) if (!steady.Allow(i * 1.0)) refused = true;
            Check(!refused, "1 click/s for 20000 s is never refused");
            // a storm of 10 clicks/s for 100 s: bounded by burst + rate
            TokenBucket storm = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond); int stormOk = 0;
            for (int i = 0; i < 1000; i++) if (storm.Allow(i * 0.1)) stormOk++;
            Check(stormOk <= BugPackRules.ClickBurst + (int)(100 * BugPackRules.ClickPerSecond) + 1 && stormOk >= (int)(100 * BugPackRules.ClickPerSecond), "storm bounded by burst + rate, got " + stormOk);
            // the bucket never holds more than the burst, however long it idled
            TokenBucket idle = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond); idle.Allow(0.0);
            int afterIdle = 0;
            for (int i = 0; i < 500; i++) if (idle.Allow(100000.0)) afterIdle++;
            Check(afterIdle == BugPackRules.ClickBurst, "after a long idle only one burst, got " + afterIdle);
            budget.Reset();
            Check(budget.Allow(5.0) && budget.TakeDropped() == 0, "reset gives a full bucket and clears the counter");
            // time going backwards (a clock hiccup) must not mint tokens
            TokenBucket back = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond); int backOk = 0;
            for (int i = 0; i < 300; i++) if (back.Allow(500.0 - i * 0.001)) backOk++;
            Check(backOk == BugPackRules.ClickBurst, "no tokens from a backwards clock, got " + backOk);

            // the two catapult log buckets are independent: a muted storm cannot starve the own-order lines
            TokenBucket allowedLog = new TokenBucket(BugPackRules.CatapultAllowedBurst, BugPackRules.CatapultAllowedPerSecond);
            TokenBucket mutedLog = new TokenBucket(BugPackRules.CatapultMutedBurst, BugPackRules.CatapultMutedPerSecond);
            int mutedOk = 0;
            for (int i = 0; i < 600; i++) if (mutedLog.Allow(i * 0.1)) mutedOk++;      // 10 muted decisions per second for a minute
            Check(mutedOk >= BugPackRules.CatapultMutedBurst && mutedOk <= BugPackRules.CatapultMutedBurst + 7, "muted log lines in a minute of hover noise: " + mutedOk);
            Check(allowedLog.Allow(30.0) && allowedLog.Allow(30.0), "own-order lines are still written during the muted storm");

            // name filter of ClickLog
            Check(BugPackRules.SafeName("Panel_Left") == "Panel_Left", "plain name kept");
            Check(BugPackRules.SafeName("Catapulte (0)") == "Catapulte (0)", "name with brackets kept");
            Check(BugPackRules.SafeName("Wood_Beam(Clone)") == "Wood_Beam", "(Clone) removed");
            Check(BugPackRules.SafeName(null) == "" && BugPackRules.SafeName("") == "", "null / empty");
            Check(BugPackRules.SafeName("Ivan's castle") == "?13", "typed text with an apostrophe becomes ?length");
            Check(BugPackRules.SafeName("Іван") == "?4", "non-ASCII text becomes ?length");
            Check(BugPackRules.SafeName("a@b.c") == "?5", "e-mail like text becomes ?length");
            Check(BugPackRules.SafeName(new string('x', 100)).Length == BugPackRules.NameMaxLength, "long names are cut to " + BugPackRules.NameMaxLength);
            Check(BugPackRules.SafeName("(Clone)") == "", "a name that is only (Clone)");

            // alchemist boost: mode / host / debug gating, ammo floor, cooldown factor, log rule
            Check(!BugPackRules.AlchemistActive(0, true, true) && !BugPackRules.AlchemistActive(0, true, false), "mode 0 is off everywhere");
            Check(BugPackRules.AlchemistActive(1, true, true) && !BugPackRules.AlchemistActive(1, true, false), "mode 1 needs the debug / editor mode");
            Check(BugPackRules.AlchemistActive(2, true, false) && BugPackRules.AlchemistActive(2, true, true) && BugPackRules.AlchemistActive(3, true, false), "mode 2 (and above) is always on");
            Check(!BugPackRules.AlchemistActive(1, false, true) && !BugPackRules.AlchemistActive(2, false, true) && !BugPackRules.AlchemistActive(-1, true, true), "never on a guest; a negative mode is off");
            Check(BugPackRules.AlchemistTopUp(0, 10) == 10 && BugPackRules.AlchemistTopUp(9, 10) == 10 && BugPackRules.AlchemistTopUp(10, 10) == 10, "top-up to the floor");
            Check(BugPackRules.AlchemistTopUp(25, 10) == 25, "a unit with more than the floor keeps its ammo");
            Check(BugPackRules.AlchemistTopUp(0, 0) == 1 && BugPackRules.AlchemistTopUp(0, -5) == 1, "a floor below 1 means 1");
            Check(BugPackRules.AlchemistTopUp(0, 5000) == 999, "a floor above 999 means 999");
            Check(Math.Abs(BugPackRules.AlchemistCooldown(0.8f, 0.5f) - 0.4f) < 1e-5f, "cooldown 0.8 x 0.5 = 0.4");
            Check(BugPackRules.AlchemistCooldown(0.8f, 1f) == 0.8f && BugPackRules.AlchemistCooldown(0.8f, 2f) == 0.8f, "factor 1 or more changes nothing");
            Check(BugPackRules.AlchemistCooldown(0.8f, 0f) == 0.8f && BugPackRules.AlchemistCooldown(0.8f, -1f) == 0.8f && BugPackRules.AlchemistCooldown(0.8f, float.NaN) == 0.8f, "factor 0, negative or NaN changes nothing");
            Check(Math.Abs(BugPackRules.AlchemistCooldown(0.8f, 0.01f) - 0.1f) < 1e-5f, "the cooldown never drops below 0.1 s");
            Check(BugPackRules.AlchemistCooldown(0.05f, 0.5f) == 0.05f, "a vanilla value below 0.1 s is not raised");
            int boostLines = 0;
            for (int n = 1; n <= 1000; n++) if (BugPackRules.AlchemistLogTopUp(n)) boostLines++;
            Check(boostLines == 20 + 5 && !BugPackRules.AlchemistLogTopUp(0), "top-up log: first 20 + every 200th = " + boostLines);

            // loose item cleanup: classification and the time limits
            Check(BugPackRules.ClassifyLoose("RawResources", "RawStone") == LooseKind.Raw && BugPackRules.ClassifyLoose("RawResources", "RawTronStone") == LooseKind.Raw && BugPackRules.ClassifyLoose("RawResources", "RawIron") == LooseKind.Raw, "raw resources are raw");
            Check(BugPackRules.ClassifyLoose("Blocks", "Wood_SingleLog") == LooseKind.Raw && BugPackRules.ClassifyLoose("Blocks", "Wood_log") == LooseKind.Raw, "log blocks are raw");
            Check(BugPackRules.ClassifyLoose("Blocks", "Stone_Brick") == LooseKind.Finished && BugPackRules.ClassifyLoose("Blocks", "Wood_SinglePlank") == LooseKind.Finished, "bricks and planks are finished");
            Check(BugPackRules.ClassifyLoose("BuildResources", "IronIngot") == LooseKind.Finished && BugPackRules.ClassifyLoose("BuildResources", "Bomb") == LooseKind.Finished, "crafted resources are finished");
            Check(BugPackRules.ClassifyLoose("Accessories", "Sword") == LooseKind.Finished && BugPackRules.ClassifyLoose("Accessories", "Backpack") == LooseKind.Finished, "dropped tools and weapons are finished");
            Check(BugPackRules.ClassifyLoose("Accessories", "HeldLog") == LooseKind.Keep && BugPackRules.ClassifyLoose("Accessories", "HeldBarrel") == LooseKind.Keep, "held props are never removed");
            Check(BugPackRules.ClassifyLoose("ObjetsDynamiques", "Baril") == LooseKind.Keep && BugPackRules.ClassifyLoose("ObjetsDynamiques", "Palette") == LooseKind.Keep && BugPackRules.ClassifyLoose("ObjetsDynamiques", "Catapulte") == LooseKind.Keep, "barrels, pallets and structures are never removed");
            Check(BugPackRules.ClassifyLoose("Test", "Bricktron") == LooseKind.Keep && BugPackRules.ClassifyLoose("Test", "Corruptron") == LooseKind.Keep, "units are never removed");
            Check(BugPackRules.ClassifyLoose(null, "RawStone") == LooseKind.Keep && BugPackRules.ClassifyLoose("RawResources", "") == LooseKind.Keep && BugPackRules.ClassifyLoose("", "") == LooseKind.Keep, "an unknown key is kept");
            Check(!BugPackRules.LooseExpired(LooseKind.Raw, 599, 10, 15) && BugPackRules.LooseExpired(LooseKind.Raw, 600, 10, 15), "raw: 10 minutes");
            Check(!BugPackRules.LooseExpired(LooseKind.Finished, 600, 10, 15) && !BugPackRules.LooseExpired(LooseKind.Finished, 899, 10, 15) && BugPackRules.LooseExpired(LooseKind.Finished, 900, 10, 15), "finished: 15 minutes, not 10");
            Check(!BugPackRules.LooseExpired(LooseKind.Keep, 1e9, 10, 15), "keep is never expired");
            Check(!BugPackRules.LooseExpired(LooseKind.Raw, 1e9, 0, 15) && !BugPackRules.LooseExpired(LooseKind.Finished, 1e9, 10, -1) && !BugPackRules.LooseExpired(LooseKind.Raw, 1e9, double.NaN, 15), "a limit of 0, negative or NaN means never");
            Check(BugPackRules.LooseExpired(LooseKind.Raw, 31, 0.5, 15), "fractional minutes work (0.5 min = 30 s)");
            int sweepLines = 0;
            for (int n = 1; n <= 200; n++) if (BugPackRules.LooseLogSweep(n)) sweepLines++;
            Check(sweepLines == 100 + 10 && !BugPackRules.LooseLogSweep(0), "sweep log: first 100, then every 10th = " + sweepLines);

            // strike cooldown guard: the rules
            Check(BugPackRules.StrikeFactor(1f) == 1.0 && BugPackRules.StrikeFactor(0.5f) == 0.5 && BugPackRules.StrikeFactor(0f) == 0.0, "strike factor 1, 0.5, 0");
            Check(BugPackRules.StrikeFactor(2f) == 1.0 && BugPackRules.StrikeFactor(-1f) == 0.0 && BugPackRules.StrikeFactor(float.NaN) == 1.0, "strike factor: above 1 is 1, negative is 0, NaN is 1");
            const double dt = 0.02, margin = 2 * dt;
            Check(Math.Abs(BugPackRules.StrikeWait(100.3, 100.0, 1.5, 1.0, margin) - 1.16) < 1e-9, "wait = 100 + 1.5 - 0.04 - 100.3 = 1.16 s");
            Check(BugPackRules.StrikeWait(101.5, 100.0, 1.5, 1.0, margin) == 0.0 && BugPackRules.StrikeWait(101.47, 100.0, 1.5, 1.0, margin) == 0.0, "no wait once the cooldown (less the 2-tick margin) passed");
            Check(Math.Abs(BugPackRules.StrikeWait(100.3, 100.0, 1.5, 0.5, margin) - 0.41) < 1e-9, "factor 0.5 halves the cooldown");
            Check(BugPackRules.StrikeWait(100.3, 100.0, 1.5, 0.0, margin) == 0.0, "factor 0: no wait");
            Check(Math.Abs(BugPackRules.StrikeWait(100.0, 100.0, 1.5, 5.0, 0.0) - 1.5) < 1e-9, "a factor above 1 never waits longer than the cooldown");
            Check(BugPackRules.StrikeWait(99.0, 100.0, 1.5, 1.0, margin) == 0.0, "a clock that went back gives no wait");
            Check(BugPackRules.StrikeWait(double.NaN, 100.0, 1.5, 1.0, margin) == 0.0 && BugPackRules.StrikeWait(100.3, 100.0, double.NaN, 1.0, margin) == 0.0 && BugPackRules.StrikeWait(100.3, 100.0, 1.5, double.NaN, margin) == 0.0, "NaN gives no wait");
            Check(BugPackRules.StrikeMinCooldownTicks(75, 10) == 65 && BugPackRules.StrikeMinCooldownTicks(75, 0) == 75 && BugPackRules.StrikeMinCooldownTicks(5, 10) == 0 && BugPackRules.StrikeMinCooldownTicks(75, -3) == 75, "shortest cooldown the game can roll");
            Check(BugPackRules.StrikeWaitTicks(1.16, dt) == 58 && BugPackRules.StrikeWaitTicks(0.001, dt) == 1 && BugPackRules.StrikeWaitTicks(0.02, dt) == 1 && BugPackRules.StrikeWaitTicks(0.0201, dt) == 2, "a wait in whole ticks, rounded up");
            Check(BugPackRules.StrikeWaitTicks(0.0, dt) == 0 && BugPackRules.StrikeWaitTicks(-1.0, dt) == 0 && BugPackRules.StrikeWaitTicks(1.0, 0.0) == 0 && BugPackRules.StrikeWaitTicks(double.NaN, dt) == 0, "nothing to wait");
            Check(BugPackRules.StrikeWaitTicks(1e9, dt) == BugPackRules.StrikeMaxWaitTicks, "a wait is capped");
            int holdLines = 0;
            for (int n = 1; n <= 1000; n++) if (BugPackRules.StrikeLogDelay(n)) holdLines++;
            Check(holdLines == 20 + 10 && !BugPackRules.StrikeLogDelay(0), "hold log: first 20 + every 100th = " + holdLines);
            Check(BugPackRules.StrikeWindowAction(5.0, -1.0) == -1 && BugPackRules.StrikeWindowAction(5.0, 10.0) == -1 && BugPackRules.StrikeWindowAction(5.0, double.NaN) == -1, "summary window (re)starts: none yet, clock went back");
            Check(BugPackRules.StrikeWindowAction(69.9, 10.0) == 0 && BugPackRules.StrikeWindowAction(70.0, 10.0) == 1, "summary line after 60 s of game time");
            Check(BugPackRules.StrikeWindowAction(9.5, 10.0) == 0, "a clock a little behind (Update vs FixedUpdate) keeps the window");

            // strike cooldown guard: the stamp table
            StrikeStamps st = new StrikeStamps();
            Check(st.Remaining(7, 100.0, 1.0, margin) == 0.0 && st.Count == 0, "no stamp, no wait");
            st.Stamp(7, 100.0, 1.5);
            Check(Math.Abs(st.Remaining(7, 100.3, 1.0, margin) - 1.16) < 1e-9 && st.Remaining(8, 100.3, 1.0, margin) == 0.0, "a stamp holds its own unit only");
            st.Stamp(7, 101.0, -4.0);
            double anchor, cooldown;
            Check(st.Remaining(7, 101.0, 1.0, 0.0) == 0.0 && st.TryGet(7, out anchor, out cooldown) && anchor == 101.0 && cooldown == 0.0, "a new stamp replaces the old one; a negative cooldown counts as 0");
            Check(!st.TryGet(9, out anchor, out cooldown) && anchor == 0.0 && cooldown == 0.0, "TryGet of an unknown unit");
            st.Clear();
            Check(st.Count == 0, "clear");
            for (int i = 0; i < BugPackRules.MaxStrikeStamps; i++) st.Stamp(i, 200.0, i < 10 ? 100.0 : 0.5);
            st.Stamp(-1, 201.0, 1.0);
            Check(st.Count == 1, "a full table of units active a second ago starts over (nothing is quiet yet): " + st.Count);
            st.Clear();
            for (int i = 0; i < BugPackRules.MaxStrikeStamps; i++) st.Stamp(i, 200.0, i < 10 ? 100.0 : 0.5);
            st.Stamp(-1, 231.0, 1.0);
            Check(st.Count == 10 + 1, "a full table drops the units whose cooldown is over and that were quiet for 30 s: " + st.Count);
            st.Clear();
            for (int i = 0; i < BugPackRules.MaxStrikeStamps; i++) st.Stamp(i, 300.0, 100.0);
            st.Stamp(-1, 301.0, 1.0);
            Check(st.Count == 1, "a full table of running cooldowns starts over: " + st.Count);

            // combat anomaly log: the shared per-unit record (strike start / end, launch) next to the guard stamp
            st.Clear();
            StrikeEnd prevEnd;
            double prev = st.NoteStrikeStart(5, 10.0, out prevEnd);
            Check(double.IsNaN(prev) && prevEnd == StrikeEnd.None, "a first strike has no previous strike");
            Check(st.Remaining(5, 10.0, 1.0, 0.0) == 0.0 && !st.TryGet(5, out anchor, out cooldown) && st.StampedCount == 0, "a telemetry-only record holds no guard stamp");
            st.NoteStrikeEnd(5, 10.5, StrikeEnd.Hit);
            st.NoteStrikeEnd(5, 10.6, StrikeEnd.Aborted);
            prev = st.NoteStrikeStart(5, 10.9, out prevEnd);
            Check(prev == 10.0 && prevEnd == StrikeEnd.Hit, "the next start sees the previous start and its FIRST end (hit, not the abort after it)");
            prev = st.NoteStrikeStart(5, 12.0, out prevEnd);
            Check(prev == 10.9 && prevEnd == StrikeEnd.None, "a strike without an end callback reads as 'no end' (cut or still running)");
            st.NoteStrikeEnd(6, 12.0, StrikeEnd.Hit);
            Check(st.Count == 1, "an end without a strike start records nothing");
            st.Stamp(5, 12.0, 1.5);
            double prevCd;
            Check(Math.Abs(st.Remaining(5, 12.3, 1.0, margin) - 1.16) < 1e-9 && double.IsNaN(st.NoteLaunch(5, 12.4, 0.6, out prevCd)) && prevCd == 0.0 && st.Count == 1 && st.StampedCount == 1, "guard stamp and telemetry live in the same record");
            Check(st.NoteLaunch(5, 13.0, 1.0, out prevCd) == 12.4 && prevCd == 0.6 && double.IsNaN(st.NoteLaunch(7, 13.0, 0.8, out prevCd)) && st.Count == 2 && st.StampedCount == 1, "a launch returns the previous launch of that unit and the interval set after it");
            Check(BugPackRules.StrikeEndText(StrikeEnd.Hit) == "hit" && BugPackRules.StrikeEndText(StrikeEnd.Aborted) == "aborted" && BugPackRules.StrikeEndText(StrikeEnd.NoHit) == "finished-no-hit" && BugPackRules.StrikeEndText(StrikeEnd.None).StartsWith("no-end"), "strike end words");
            Check(BugPackRules.StrikeEndShort(StrikeEnd.Hit) == "hit" && BugPackRules.StrikeEndShort(StrikeEnd.NoHit) == "nohit" && BugPackRules.StrikeEndShort(StrikeEnd.Aborted) == "abort" && BugPackRules.StrikeEndShort(StrikeEnd.None) == "none", "short strike end words");
            st.Clear();

            // combat anomaly log: thresholds
            Check(BugPackRules.MeleeFastThresholdTicks(75, 10) == 60.0 && BugPackRules.MeleeFastThresholdTicks(75, 0) == 60.0, "melee threshold 0.8 x 75 = 60 ticks (randomness up to 15 does not lower it)");
            Check(BugPackRules.MeleeFastThresholdTicks(75, 20) == 55.0 && BugPackRules.MeleeFastThresholdTicks(0, 5) == 0.0 && BugPackRules.MeleeFastThresholdTicks(-3, 0) == 0.0, "the threshold never exceeds the shortest legal roll (75 - 20 = 55); no cooldown = no check");
            Check(BugPackRules.MeleeFast(59.4, 60.0) && !BugPackRules.MeleeFast(59.6, 60.0) && !BugPackRules.MeleeFast(60.0, 60.0) && !BugPackRules.MeleeFast(double.NaN, 60.0) && !BugPackRules.MeleeFast(-1.0, 60.0) && !BugPackRules.MeleeFast(5.0, 0.0), "melee fast edges (whole ticks)");
            Check(!BugPackRules.MeleeFast(36.99, 37.0) && BugPackRules.MeleeFast(46.0, 46.4), "a float 36.99 is the 37th tick (legal at a threshold of 37); 46 ticks are below 46.4");
            Check(BugPackRules.RangedFar(10.6, 10.0) && !BugPackRules.RangedFar(10.5, 10.0) && !BugPackRules.RangedFar(99.0, 0.0) && !BugPackRules.RangedFar(double.NaN, 10.0), "ranged far: over range x 1.05 only");
            Check(BugPackRules.RangedFast(0.79, 1.0) && !BugPackRules.RangedFast(0.8, 1.0) && !BugPackRules.RangedFast(0.1, 0.0) && !BugPackRules.RangedFast(double.NaN, 1.0) && !BugPackRules.RangedFast(-0.1, 1.0), "ranged fast: under 0.8 x the interval only");
            Check(BugPackRules.AnomAlwaysKeep("12:00:00.000 t=1.00 COMBAT-ANOM minute 60.0s: melee 3") && BugPackRules.AnomAlwaysKeep("COMBAT-ANOM installed: melee ok") && BugPackRules.AnomAlwaysKeep("COMBAT-ANOM error in x"), "report: summary, install and error lines are always kept");
            Check(!BugPackRules.AnomAlwaysKeep("t=1.00 COMBAT-ANOM MELEE-FAST Knight#1->Knight#2") && BugPackRules.AnomAlwaysKeep("HITCH 900 ms") && !BugPackRules.AnomAlwaysKeep(null), "report: detail lines are capped, other tags are not touched");

            // combat anomaly log: budget = first 50 in full, then one per unit per 10 s and at most 10 per summary window
            AnomLimiter lim = new AnomLimiter();
            int allowed = 0;
            for (int n = 0; n < 50; n++) if (lim.Allow(1, 100.0)) allowed++;
            Check(allowed == 50 && lim.Written == 50, "the first 50 events are all written, even from one unit");
            Check(!lim.Allow(1, 105.0) && !lim.Allow(1, 109.9) && lim.Allow(2, 109.9) && lim.Allow(1, 110.0), "after 50: one line per unit per 10 s; another unit is not held back");
            Check(lim.TakeSuppressed() == 2 && lim.TakeSuppressed() == 0, "suppressed events are counted for the summary");
            allowed = 0;
            for (int u = 10; u < 30; u++) if (lim.Allow(u, 120.0)) allowed++;
            Check(allowed == 8 && lim.TakeSuppressed() == 12, "at most 10 detail lines per summary window after the first 50 (2 + 8 here)");
            lim.NewWindow();
            Check(lim.Allow(40, 130.0) && lim.Allow(1, 50.0), "a new window allows lines again; a clock that went back is not held back");
            for (int u = 1000; u < 1000 + BugPackRules.MaxHitUnits + 10; u++) { lim.NewWindow(); lim.Allow(u, 200.0); }
            lim.NewWindow();
            Check(lim.Allow(3, 200.0), "the per-unit table stays bounded");
            lim.Clear();
            Check(lim.Written == 0 && lim.Allow(1, 100.0), "clear");

            // strike cooldown guard: 30 s (1500 ticks) of one unit, tick by tick, the way the hooked game code runs (see SimUnit / SimObject)
            int holds, heldTicks;
            int aPlain = SimUnit(false, false, 1.0, out holds), aPlainGuard = SimUnit(true, false, 1.0, out holds);
            Check(aPlain == aPlainGuard && holds == 0, "unit fight without new orders: the guard never holds a strike (" + aPlain + " = " + aPlainGuard + ")");
            int aSpam = SimUnit(false, true, 1.0, out holds), aSpamGuard = SimUnit(true, true, 1.0, out holds);
            Check(aSpam >= 2 * aPlain, "unit: 'strike, new order' spam without the guard hits much faster (" + aSpam + " vs " + aPlain + ")");
            Check(holds > 0 && aSpamGuard <= aPlain * 11 / 10 + 1, "unit: with the guard the spam is back to the normal rate (" + aSpamGuard + " vs " + aPlain + ", holds " + holds + ")");
            int aSpamHalf = SimUnit(true, true, 0.5, out holds);
            Check(aSpamHalf > aSpamGuard && aSpamHalf < aSpam, "unit: factor 0.5 lies in between (" + aSpamHalf + ")");
            int bPlain = SimObject(false, false, out heldTicks), bPlainGuard = SimObject(true, false, out heldTicks);
            Check(bPlain == bPlainGuard && heldTicks == 0, "object (door) loop without new orders: the guard never waits (" + bPlain + " = " + bPlainGuard + ")");
            int bSpam = SimObject(false, true, out heldTicks), bSpamGuard = SimObject(true, true, out heldTicks);
            Check(bSpam >= 2 * bPlain, "object: new-order spam without the guard hits much faster (" + bSpam + " vs " + bPlain + ")");
            Check(heldTicks > 0 && bSpamGuard <= bPlain * 5 / 4 + 1, "object: with the guard the spam is near the normal rate (" + bSpamGuard + " vs " + bPlain + ")");

            Console.WriteLine("PASS: " + checks + " bug pack checks (picker-guard log rules, dead-entry prune, damaged-block fallback, catapult click mute and own-order marks, token buckets, name filter, alchemist boost rules, loose item cleanup rules, strike cooldown guard rules and tick simulations, combat anomaly rules and log budget).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }

    // Path A (AttackInfo, unit against unit), one unit, tick by tick: an order zeroes CurrentFrame and NextAttackFrame (AttackInfo.ResetObject); the AI strikes when ready and
    // CurrentFrame >= NextAttackFrame; a strike sets NextAttackFrame = CurrentFrame + RandomizedAttackCooldown (75 +- 10 here). The swing keeps the unit busy 40 ticks unless a new
    // order cuts it (ready again 10 ticks after the order). With the guard: the same steps as StrikeGuard.HoldFirstStrike / StampStrikeA (that code itself runs in HookTest).
    // Returns the strikes made in `ticks` ticks.
    static int SimUnit(bool guard, bool spam, double factor, out int holds)
    {
        const double dt = 0.02;
        StrikeStamps st = new StrikeStamps();
        Random rnd = new Random(7);
        int currentFrame = 0, nextAttackFrame = 0, strikes = 0, readyAt = 10, orderAt = -1;
        holds = 0;
        for (int t = 0; t < 1500; t++)
        {
            double now = t * dt;
            if (t == orderAt) { currentFrame = 0; nextAttackFrame = 0; readyAt = t + 10; orderAt = -1; }
            if (t >= readyAt && currentFrame >= nextAttackFrame)
            {
                bool held = false;
                if (guard && nextAttackFrame == 0)
                {
                    int w = BugPackRules.StrikeWaitTicks(st.Remaining(1, now, factor, BugPackRules.StrikeMarginTicks * dt), dt);
                    if (w > 0) { nextAttackFrame = currentFrame + w; holds++; held = true; }
                }
                if (!held)
                {
                    int c = 65 + rnd.Next(20);
                    nextAttackFrame = currentFrame + c;
                    st.Stamp(1, now, (nextAttackFrame - currentFrame) * dt);
                    strikes++;
                    readyAt = t + 40;
                    if (spam) orderAt = t + 15;   // the player gives a new order right after the hit lands
                }
            }
            currentFrame++;
        }
        return strikes;
    }

    // Path B (BreakObject loop), one unit: [gate] -> strike (30 ticks until the attack is Finished) -> stamp the shortest cooldown (65 ticks) -> WaitForTicks(75 +- 10) -> next pass on
    // the next tick. Spam = a new order right after each strike (a new loop strikes at once). Returns the strikes in 1500 ticks; heldTicks = ticks the gate waited.
    static int SimObject(bool guard, bool spam, out int heldTicks)
    {
        const double dt = 0.02;
        StrikeStamps st = new StrikeStamps();
        Random rnd = new Random(11);
        int strikes = 0, t = 0;
        heldTicks = 0;
        while (t < 1500)
        {
            if (guard)
            {
                int w = BugPackRules.StrikeWaitTicks(st.Remaining(1, t * dt, 1.0, BugPackRules.StrikeMarginTicks * dt), dt);
                t += w; heldTicks += w;
                if (t >= 1500) break;
            }
            strikes++;
            t += 30;
            st.Stamp(1, t * dt, BugPackRules.StrikeMinCooldownTicks(75, 10) * dt);
            if (spam) { t += 1; continue; }
            t += 65 + rnd.Next(20) + 1;
        }
        return strikes;
    }
}
