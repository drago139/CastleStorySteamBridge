using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // Bug pack (PATCHSET-bug-pack): the small decisions of PickerGuard, DamagedBlockFix, CatapultClickLocal, ClickLog, AlchemistBoost, LooseCleanup and StrikeGuard without Unity or game
    // types, so BugPackTests can drive them.
    // No game types here.
    internal static class BugPackRules
    {
        internal const string CatapultClickSound = "Catapult_Set_Target";   // SoundEngine.Play name of the "target chosen" click in CatapultBody.React_Healthy_Viser
        internal const float OwnOrderSeconds = 6f;      // a target order given on THIS PC marks its catapult for this long (unscaled time)
        internal const int MaxOwnMarks = 256;
        internal const int GuardLogFirst = 20;          // PickerGuard: the first swallowed exceptions are logged one by one,
        internal const int GuardLogEvery = 1000;        // then every 1000th (a persistent fault would write one line per frame)
        internal const int GuardMaxSignatures = 40;     // PickerGuard: distinct (exception type, top frame) pairs that are counted and may add a line of their own
        internal const int GuardStormAt = 100;          // PickerGuard: a signature that repeated this often gets one more line
        internal const int ClickBurst = 120;            // ClickLog: lines allowed in one burst
        internal const double ClickPerSecond = 1.0;     // ClickLog: sustained lines per second after the burst
        internal const int ClickMatchLimit = 4000;      // ClickLog: hard cap per match (about 1.2 MB of perf-trace.txt)
        internal const int CatapultAllowedBurst = 20;   // CatapultClickLocal: log budget for own orders (a token bucket of its own, so a muted storm cannot starve it)
        internal const double CatapultAllowedPerSecond = 1.0;
        internal const int CatapultMutedBurst = 10;     // CatapultClickLocal: log budget for muted decisions
        internal const double CatapultMutedPerSecond = 0.1;
        internal const int NameMaxLength = 40;          // ClickLog: object names are cut to this

        internal static bool LogSwallowed(int count)
        {
            return count > 0 && (count <= GuardLogFirst || count % GuardLogEvery == 0);
        }

        // PickerGuard: an extra line for a signature (exception type + top frame) on its first sight, when it reaches the storm count, and on every 1000th. occurrence >= 1.
        internal static bool LogSignature(int occurrence)
        {
            return occurrence == 1 || occurrence == GuardStormAt || (occurrence > 0 && occurrence % GuardLogEvery == 0);
        }

        // Removes the entries the caller calls dead (for a Unity object: "== null", which is also true for a destroyed one). Returns how many were removed.
        // A null list or a null predicate removes nothing.
        internal static int RemoveDead<T>(List<T> list, Predicate<T> dead)
        {
            if (list == null || dead == null) return 0;
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--) if (dead(list[i])) { list.RemoveAt(i); removed++; }
            return removed;
        }

        // DamagedBlockFix: a crack-material array that cannot be drawn (empty, or its first material missing) must become "no result", so the caller keeps the intact materials.
        internal static bool UnusableDamageMaterials(int length, bool firstIsMissing)
        {
            return length == 0 || firstIsMissing;
        }

        // CatapultClickLocal: while one React_Healthy_Viser runs without a fresh own order, only the one click sound is skipped; every other sound plays.
        internal static bool SoundAllowed(bool muteThisReaction, string soundName)
        {
            return !muteThisReaction || soundName != CatapultClickSound;
        }

        // AlchemistBoost (host only): mode 0 = off, 1 = only in the game's debug / editor mode, 2 = always.
        internal static bool AlchemistActive(int mode, bool server, bool editorMode)
        {
            return server && (mode >= 2 || (mode == 1 && editorMode));
        }

        // AlchemistBoost: the ammo after a throw. The floor is clamped to 1..999; a unit that holds more keeps what it has.
        internal static int AlchemistTopUp(int current, int floor)
        {
            if (floor < 1) floor = 1;
            if (floor > 999) floor = 999;
            return current < floor ? floor : current;
        }

        // AlchemistBoost: the throw cooldown. A factor outside (0, 1) changes nothing; the result never drops below 0.1 s (or below the vanilla value if that is smaller).
        internal static float AlchemistCooldown(float vanilla, float factor)
        {
            if (!(factor > 0f) || factor >= 1f) return vanilla;
            return Math.Max(vanilla * factor, Math.Min(vanilla, 0.1f));
        }

        // AlchemistBoost: the first 20 top-ups are logged, then every 200th.
        internal static bool AlchemistLogTopUp(int count)
        {
            return count > 0 && (count <= 20 || count % 200 == 0);
        }

        // LooseCleanup: what a thing lying loose on the ground is, by its factory asset key. Raw = logs, stone, iron, crystals, clay, fiber (RawResources and the Log blocks);
        // Finished = crafted things (BuildResources: ingots, glass, fabric, rope, bombs ...; the other Blocks: bricks, planks ...; Accessories: dropped tools and weapons, not the Held* props);
        // Keep = everything else (structures, barrels, pallets, units, unknown keys): never removed.
        internal static LooseKind ClassifyLoose(string factory, string name)
        {
            if (string.IsNullOrEmpty(factory) || string.IsNullOrEmpty(name)) return LooseKind.Keep;
            if (factory == "RawResources") return LooseKind.Raw;
            if (factory == "Blocks") return name.IndexOf("Log", StringComparison.OrdinalIgnoreCase) >= 0 ? LooseKind.Raw : LooseKind.Finished;
            if (factory == "BuildResources") return LooseKind.Finished;
            if (factory == "Accessories") return name.StartsWith("Held", StringComparison.Ordinal) ? LooseKind.Keep : LooseKind.Finished;
            return LooseKind.Keep;
        }

        // LooseCleanup: has a thing lain long enough? A limit of 0 or less (or NaN) means "never" for that kind.
        internal static bool LooseExpired(LooseKind kind, double lyingSeconds, double rawMinutes, double finishedMinutes)
        {
            if (kind == LooseKind.Keep) return false;
            double limit = kind == LooseKind.Raw ? rawMinutes : finishedMinutes;
            if (!(limit > 0)) return false;
            return lyingSeconds >= limit * 60.0;
        }

        // LooseCleanup: one log line per sweep that removed something, the first 100, then every 10th.
        internal static bool LooseLogSweep(int sweepsWithRemovals)
        {
            return sweepsWithRemovals > 0 && (sweepsWithRemovals <= 100 || sweepsWithRemovals % 10 == 0);
        }

        // StrikeGuard (HOST only): the melee attack cooldown survives a new order. Times are game seconds (Time.fixedTime); one AI tick = one FixedUpdate (Time.fixedDeltaTime).
        internal const int StrikeMarginTicks = 2;         // slack so a fight without new orders is never held back by rounding
        internal const int StrikeMaxWaitTicks = 3000;     // a single wait never exceeds this (60 s at 50 ticks/s); the game's cooldowns are about 75 ticks
        internal const int StrikeLogFirst = 20;           // the first 20 held-back strikes get a line each, then every 100th
        internal const int StrikeLogEvery = 100;
        internal const double StrikeWindowSeconds = 60.0; // one summary line per minute of game time that had melee strikes
        internal const int MaxStrikeStamps = 1024;
        internal const int MaxHitUnits = 512;

        // StrikeGuard: the cfg factor clamped to 0..1 (NaN = 1). 0 = no wait; 1 = the whole cooldown of the last strike; above 1 counts as 1, so a fight without new orders is never slowed.
        internal static double StrikeFactor(float factor)
        {
            if (float.IsNaN(factor)) return 1.0;
            if (factor <= 0f) return 0.0;
            if (factor >= 1f) return 1.0;
            return factor;
        }

        // StrikeGuard: seconds a unit must still wait before its next strike may start = anchor + factor * cooldown - margin - now; 0 = it may strike now.
        // A clock that went back (now before the anchor) or a NaN gives 0, so a stale stamp can never hold a unit for longer than one cooldown.
        internal static double StrikeWait(double now, double anchor, double cooldown, double factor, double margin)
        {
            if (double.IsNaN(now) || double.IsNaN(anchor) || double.IsNaN(cooldown) || double.IsNaN(factor) || double.IsNaN(margin) || now < anchor) return 0.0;
            double wait = anchor + Math.Max(0.0, Math.Min(1.0, factor)) * Math.Max(0.0, cooldown) - Math.Max(0.0, margin) - now;
            return wait > 0.0 ? wait : 0.0;
        }

        // StrikeGuard: the shortest cooldown the game can roll, Profession.RandomizedAttackCooldown = AttackCooldown + Random.Range(-randomness, randomness); never below 0.
        internal static int StrikeMinCooldownTicks(int attackCooldown, int randomness)
        {
            return Math.Max(0, attackCooldown - Math.Max(0, randomness));
        }

        // StrikeGuard: a wait in seconds as whole AI ticks, rounded up, 1..StrikeMaxWaitTicks; 0 when there is nothing to wait or the tick length is unusable.
        internal static int StrikeWaitTicks(double waitSeconds, double tickSeconds)
        {
            if (!(waitSeconds > 0.0) || !(tickSeconds > 0.0)) return 0;
            double ticks = Math.Ceiling(waitSeconds / tickSeconds - 1e-9);
            if (ticks < 1.0) return 1;
            return ticks >= StrikeMaxWaitTicks ? StrikeMaxWaitTicks : (int)ticks;
        }

        // StrikeGuard: a line for each of the first 20 held-back strikes, then for every 100th.
        internal static bool StrikeLogDelay(int count)
        {
            return count > 0 && (count <= StrikeLogFirst || count % StrikeLogEvery == 0);
        }

        // StrikeGuard / CombatAnomaly summary window: -1 = (re)start the window now (no window yet, or the clock went back), 1 = a full window passed (write the line, start a new
        // one), 0 = keep counting.
        internal static int StrikeWindowAction(double now, double windowStart)
        {
            if (windowStart < 0.0 || double.IsNaN(windowStart) || now < windowStart - 1.0) return -1;   // 1 s slack: Update and FixedUpdate read slightly different clocks
            return now - windowStart >= StrikeWindowSeconds ? 1 : 0;
        }

        // CombatAnomaly (log only, [Diagnostics] CombatAnomalyLog): thresholds and the log budget.
        internal const double StrikeQuietSeconds = 30.0;  // a unit record with no running cooldown and nothing new for this long may be dropped from the shared table
        internal const double AnomMeleeFactor = 0.8;      // MELEE-FAST: a strike starts sooner than this share of Profession.AttackCooldown ...
        internal const double AnomRangeSlack = 1.05;      // RANGED-FAR: the target is farther than range x this
        internal const double AnomRangedFactor = 0.8;     // RANGED-FAST: a launch comes sooner than this share of the expected interval
        internal const int AnomLogFirst = 50;             // the first 50 events in full, then at most one per unit per 10 s and at most 10 per summary window
        internal const double AnomUnitGapSeconds = 10.0;
        internal const int AnomWindowCap = 10;
        internal const int AnomReportCap = 20;            // SessionShare: COMBAT-ANOM detail lines kept per friend report (minute / installed / error lines always)

        // MELEE-FAST threshold in ticks: 0.8 x AttackCooldown, but never above the shortest cooldown the game itself can roll (AttackCooldown - AttackCooldownRandomness),
        // so a legal random roll is never flagged. 0 = no check (no cooldown known).
        internal static double MeleeFastThresholdTicks(int attackCooldown, int randomness)
        {
            if (attackCooldown <= 0) return 0.0;
            return Math.Min(AnomMeleeFactor * attackCooldown, StrikeMinCooldownTicks(attackCooldown, randomness));
        }

        // MELEE-FAST: the time between two strike STARTS of one unit, in whole ticks (rounded: a float 36.99 is the 37th tick), is below the threshold. A first strike, a negative
        // interval or no threshold is never flagged.
        internal static bool MeleeFast(double intervalTicks, double thresholdTicks)
        {
            if (double.IsNaN(intervalTicks) || intervalTicks < 0.0 || !(thresholdTicks > 0.0)) return false;
            return Math.Round(intervalTicks) < thresholdTicks;
        }

        // A short word for a strike end (log lines are cut at 300 characters in the friend report).
        internal static string StrikeEndShort(StrikeEnd end)
        {
            switch (end)
            {
                case StrikeEnd.Hit: return "hit";
                case StrikeEnd.NoHit: return "nohit";
                case StrikeEnd.Aborted: return "abort";
                default: return "none";
            }
        }

        // SessionShare: a COMBAT-ANOM line that is always kept in the friend report (the summary, the install line, errors); detail lines are capped per report.
        internal static bool AnomAlwaysKeep(string line)
        {
            if (line == null) return false;
            int i = line.IndexOf("COMBAT-ANOM ", StringComparison.Ordinal);
            if (i < 0) return true;
            string rest = line.Substring(i + 12);
            return rest.StartsWith("minute", StringComparison.Ordinal) || rest.StartsWith("installed", StringComparison.Ordinal) || rest.StartsWith("error", StringComparison.Ordinal);
        }

        // RANGED-FAR: the target is farther than the weapon's range x 1.05 (no range known = no check).
        internal static bool RangedFar(double distance, double range)
        {
            return range > 0.0 && !double.IsNaN(distance) && distance > range * AnomRangeSlack;
        }

        // RANGED-FAST: two launches of one unit came sooner than 0.8 x the expected interval (no interval known = no check).
        internal static bool RangedFast(double intervalSeconds, double expectedSeconds)
        {
            return expectedSeconds > 0.0 && !double.IsNaN(intervalSeconds) && intervalSeconds >= 0.0 && intervalSeconds < AnomRangedFactor * expectedSeconds;
        }

        internal static string StrikeEndText(StrikeEnd end)
        {
            switch (end)
            {
                case StrikeEnd.Hit: return "hit";
                case StrikeEnd.NoHit: return "finished-no-hit";
                case StrikeEnd.Aborted: return "aborted";
                default: return "no-end (cut or still running)";
            }
        }

        // ClickLog: object names go into a log that travels in diagnostics zips. A name made only of plain characters is kept (without a trailing "(Clone)", cut to 40 characters);
        // anything else (text a player typed) becomes "?<length>".
        internal static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7);
            if (name.Length > NameMaxLength) name = name.Substring(0, NameMaxLength);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool plain = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-' || c == '.' || c == ' ' || c == '(' || c == ')' || c == '/' || c == '#' || c == ':';
                if (!plain) return "?" + name.Length;
            }
            return name;
        }
    }

    internal enum LooseKind { Keep, Raw, Finished }

    // CatapultClickLocal: the catapults (by instance id) that this PC just gave a target order to.
    internal sealed class OrderMarks
    {
        readonly Dictionary<int, float> until = new Dictionary<int, float>();

        internal int Count { get { return until.Count; } }

        internal void Mark(int id, float now)
        {
            if (until.Count >= BugPackRules.MaxOwnMarks) Trim(now);
            until[id] = now + BugPackRules.OwnOrderSeconds;
        }

        internal bool IsFresh(int id, float now)
        {
            float limit;
            return until.TryGetValue(id, out limit) && now <= limit;
        }

        internal void Clear() { until.Clear(); }

        void Trim(float now)
        {
            List<int> old = new List<int>();
            foreach (KeyValuePair<int, float> pair in until) if (pair.Value < now) old.Add(pair.Key);
            for (int i = 0; i < old.Count; i++) until.Remove(old[i]);
            if (until.Count >= BugPackRules.MaxOwnMarks) until.Clear();
        }
    }

    // A token bucket: a burst of `burst` events, then `perSecond` events per second. ClickLog uses it so a whole match stays covered and a click storm cannot flood perf-trace.txt;
    // CatapultClickLocal uses two of them to budget its log lines.
    internal sealed class TokenBucket
    {
        readonly double burst, rate;
        double tokens, last = double.NaN;
        int dropped;

        internal TokenBucket(double burst, double perSecond) { this.burst = burst; rate = perSecond; tokens = burst; }

        internal bool Allow(double now)
        {
            if (!double.IsNaN(last) && now > last) tokens = Math.Min(burst, tokens + (now - last) * rate);
            last = now;
            if (tokens < 1.0) { dropped++; return false; }
            tokens -= 1.0;
            return true;
        }

        // Events refused since the last call; the caller writes one "skipped N" note with the next accepted event.
        internal int TakeDropped() { int d = dropped; dropped = 0; return d; }

        internal void Reset() { tokens = burst; last = double.NaN; dropped = 0; }
    }

    // How a unit's last melee strike ended (CombatAnomaly). None = no end callback yet: still running, or cut without one.
    internal enum StrikeEnd : byte { None, Hit, NoHit, Aborted }

    // ONE per-unit record (key = the unit's GameObject instance id) shared by StrikeGuard and CombatAnomaly, in game seconds (NaN = never):
    //   StrikeGuard   : Anchor / Cooldown = when the cooldown that counts started and how long it is (Stamp, Remaining, TryGet)
    //   CombatAnomaly : the last melee strike START (the game's Striking.Strike) and how it ended, the last ranged launch and the interval the game set after it.
    internal sealed class StrikeStamps
    {
        struct Unit
        {
            internal bool Stamped; internal double Anchor, Cooldown;
            internal double StrikeStart; internal StrikeEnd End;
            internal double LaunchAt, LaunchCooldown;
            internal double Seen;
        }
        readonly Dictionary<int, Unit> map = new Dictionary<int, Unit>();

        internal int Count { get { return map.Count; } }

        // Units that hold a StrikeGuard stamp (the table also holds telemetry-only records).
        internal int StampedCount
        {
            get { int n = 0; foreach (KeyValuePair<int, Unit> pair in map) if (pair.Value.Stamped) n++; return n; }
        }

        static Unit Fresh()
        {
            Unit u;
            u.Stamped = false; u.Anchor = 0.0; u.Cooldown = 0.0;
            u.StrikeStart = double.NaN; u.End = StrikeEnd.None;
            u.LaunchAt = double.NaN; u.LaunchCooldown = 0.0; u.Seen = double.NaN;
            return u;
        }

        Unit Get(int id, double now)
        {
            Unit u;
            if (map.TryGetValue(id, out u)) return u;
            if (map.Count >= BugPackRules.MaxStrikeStamps) Prune(now);
            return Fresh();
        }

        void Put(int id, Unit u, double now) { u.Seen = now; map[id] = u; }

        // ---- StrikeGuard
        internal void Stamp(int id, double now, double cooldownSeconds)
        {
            Unit u = Get(id, now);
            u.Stamped = true; u.Anchor = now; u.Cooldown = cooldownSeconds > 0.0 ? cooldownSeconds : 0.0;
            Put(id, u, now);
        }

        // Seconds this unit must still wait (0 = none or no stamp).
        internal double Remaining(int id, double now, double factor, double margin)
        {
            Unit u;
            return map.TryGetValue(id, out u) && u.Stamped ? BugPackRules.StrikeWait(now, u.Anchor, u.Cooldown, factor, margin) : 0.0;
        }

        internal bool TryGet(int id, out double anchor, out double cooldown)
        {
            Unit u;
            bool found = map.TryGetValue(id, out u) && u.Stamped;
            anchor = found ? u.Anchor : 0.0; cooldown = found ? u.Cooldown : 0.0;
            return found;
        }

        // ---- CombatAnomaly
        // A melee strike starts: returns the start of the previous one (NaN = none) and how it ended; then records this start.
        internal double NoteStrikeStart(int id, double now, out StrikeEnd previousEnd)
        {
            Unit u = Get(id, now);
            double previous = u.StrikeStart;
            previousEnd = u.End;
            u.StrikeStart = now; u.End = StrikeEnd.None;
            Put(id, u, now);
            return previous;
        }

        // The running strike ended; the first end of a strike wins (a strike ends once).
        internal void NoteStrikeEnd(int id, double now, StrikeEnd end)
        {
            Unit u;
            if (!map.TryGetValue(id, out u) || double.IsNaN(u.StrikeStart) || u.End != StrikeEnd.None) return;
            u.End = end;
            Put(id, u, now);
        }

        // A ranged launch: returns the previous launch time (NaN = none) and the interval the game set after it (its JobRangedCooldown), then records this launch and its interval.
        internal double NoteLaunch(int id, double now, double cooldownSeconds, out double previousCooldown)
        {
            Unit u = Get(id, now);
            double previous = u.LaunchAt;
            previousCooldown = u.LaunchCooldown;
            u.LaunchAt = now; u.LaunchCooldown = cooldownSeconds > 0.0 ? cooldownSeconds : 0.0;
            Put(id, u, now);
            return previous;
        }

        // Drops the units that can no longer matter: no running guard cooldown and nothing written for StrikeQuietSeconds (or a clock that went back).
        // If the table is still full, starts over.
        internal void Prune(double now)
        {
            List<int> old = new List<int>();
            foreach (KeyValuePair<int, Unit> pair in map)
            {
                Unit u = pair.Value;
                bool clockBack = now < u.Seen - 1.0 || (u.Stamped && now < u.Anchor - 1.0);   // 1 s slack: hooks read Time.time in Update and Time.fixedTime in FixedUpdate
                bool cooldownOver = !u.Stamped || now - u.Anchor > u.Cooldown;
                bool quiet = double.IsNaN(u.Seen) || now - u.Seen > BugPackRules.StrikeQuietSeconds;
                if (clockBack || (cooldownOver && quiet)) old.Add(pair.Key);
            }
            for (int i = 0; i < old.Count; i++) map.Remove(old[i]);
            if (map.Count >= BugPackRules.MaxStrikeStamps) map.Clear();
        }

        internal void Clear() { map.Clear(); }
    }

    // CombatAnomaly log budget: the first AnomLogFirst events are written in full; after that at most one per unit per AnomUnitGapSeconds AND at most AnomWindowCap per summary
    // window (NewWindow), so a big fight cannot flood perf-trace.txt or the friend report; the rest are only counted.
    internal sealed class AnomLimiter
    {
        readonly Dictionary<int, double> last = new Dictionary<int, double>();
        int written, suppressed, inWindow;

        internal int Written { get { return written; } }

        internal bool Allow(int unitId, double now)
        {
            double t;
            bool seen = last.TryGetValue(unitId, out t);
            if (written >= BugPackRules.AnomLogFirst)
            {
                bool unitTooSoon = seen && now >= t && now - t < BugPackRules.AnomUnitGapSeconds;
                if (unitTooSoon || inWindow >= BugPackRules.AnomWindowCap) { suppressed++; return false; }
                inWindow++;
            }
            if (!seen && last.Count >= BugPackRules.MaxHitUnits) Trim(now);
            last[unitId] = now;
            written++;
            return true;
        }

        // A new summary window starts: the per-window cap counts again from 0.
        internal void NewWindow() { inWindow = 0; }

        // Events refused since the last call (for the summary line).
        internal int TakeSuppressed() { int s = suppressed; suppressed = 0; return s; }

        internal void Clear() { last.Clear(); written = 0; suppressed = 0; inWindow = 0; }

        void Trim(double now)
        {
            List<int> old = new List<int>();
            foreach (KeyValuePair<int, double> pair in last) if (now < pair.Value || now - pair.Value >= BugPackRules.AnomUnitGapSeconds) old.Add(pair.Key);
            for (int i = 0; i < old.Count; i++) last.Remove(old[i]);
            if (last.Count >= BugPackRules.MaxHitUnits) last.Clear();
        }
    }
}
