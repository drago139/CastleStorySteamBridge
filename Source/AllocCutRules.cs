using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // Pure logic of the allocation-cut features (wave 2 C2, integrated in the freeze build): the guard mode, dead-entry compaction, the swallow rule and the
    // repeated-error log throttle. No Unity / game types, so AllocCutTests.cs can compile and run it with plain csc (RunUnitTests.ps1 suite 'AllocCutTests').
    internal static class AcRules
    {
        internal const int ModeOff = 0, ModeObserve = 1, ModeOn = 2;

        internal static int NormalizeMode(int cfg) { return cfg < ModeOff ? ModeOff : (cfg > ModeOn ? ModeOn : cfg); }

        // observe and on both look at the lists / count errors
        internal static bool Counts(int mode) { return mode >= ModeObserve; }

        // only mode 'on' changes anything, and the kill flag (BepInEx\stalevision-off.flag) turns it back into observe at once
        internal static bool Acts(int mode, bool killFlag) { return mode == ModeOn && !killFlag; }

        // number of entries for which alive(x) is false (read only)
        internal static int CountDead<T>(List<T> list, Predicate<T> alive)
        {
            if (list == null) return 0;
            int dead = 0;
            for (int i = 0; i < list.Count; i++) if (!alive(list[i])) dead++;
            return dead;
        }

        // removes the dead entries in place, keeping the order of the others; returns how many were removed (the list is not touched when none)
        internal static int Compact<T>(List<T> list, Predicate<T> alive)
        {
            if (list == null) return 0;
            int c = list.Count, w = 0;
            for (int i = 0; i < c; i++)
            {
                T x = list[i];
                if (alive(x)) { if (w != i) list[w] = x; w++; }
            }
            if (w < c) list.RemoveRange(w, c - w);
            return c - w;
        }

        // The guard swallows the error only when it is the known kind (NullReference / MissingReference) AND the purge really removed something;
        // otherwise the error has another cause and the game must see it.
        internal static bool ShouldSwallow(bool knownKind, int removed) { return knownKind && removed > 0; }

        // Key of a repeated error: digits are ignored (counters, ids, times), only the first 160 characters count, the exception type is mixed in.
        // nonStringTypeHash: hash of the message's type when the message is not a string (0 otherwise); excTypeHash: hash of the exception type (0 = none).
        internal static int ThrottleKey(string message, int nonStringTypeHash, int excTypeHash)
        {
            int h = 17;
            if (message != null)
            {
                int n = message.Length < 160 ? message.Length : 160;
                for (int i = 0; i < n; i++) { char ch = message[i]; if (ch >= '0' && ch <= '9') continue; h = unchecked(h * 31 + ch); }
            }
            else h = nonStringTypeHash;
            if (excTypeHash != 0) h = unchecked(h * 31 + excTypeHash);
            return h;
        }
    }

    // At most 'limit' events per key and per window are let through, the rest is dropped and counted. When a window ends with drops, Allow returns a one-line summary.
    internal sealed class AcThrottle
    {
        sealed class Slot { public int Start; public int Count; public int Dropped; public string Sample; }
        readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();
        readonly int limit, windowMs; long droppedTotal;

        internal AcThrottle(int limit, int windowMs) { this.limit = limit < 0 ? 0 : limit; this.windowMs = windowMs < 1 ? 1 : windowMs; }

        internal long DroppedTotal { get { lock (slots) return droppedTotal; } }
        internal int SlotCount { get { lock (slots) return slots.Count; } }

        // true = let the event through. sample / altSample are only used when a new slot is created (text for the summary line); closed = summary of the window that just ended, or null.
        internal bool Allow(int key, int nowMs, string sample, string altSample, out string closed)
        {
            closed = null;
            lock (slots)
            {
                Slot sl;
                if (!slots.TryGetValue(key, out sl))
                {
                    if (slots.Count > 256) slots.Clear();   // memory bound: keys are digit-stripped, so this only happens with very varied messages
                    sl = new Slot(); sl.Start = nowMs;
                    string s = sample != null ? sample : (altSample != null ? altSample : "null");
                    sl.Sample = (s.Length > 90 ? s.Substring(0, 90) : s).Replace('\n', ' ').Replace('\r', ' ');
                    slots[key] = sl;
                }
                else if (unchecked(nowMs - sl.Start) >= windowMs)
                {
                    if (sl.Dropped > 0) closed = "dropped " + sl.Dropped + " ERROR events in " + (unchecked(nowMs - sl.Start) / 1000) + " s: " + sl.Sample;
                    sl.Start = nowMs; sl.Count = 0; sl.Dropped = 0;
                }
                sl.Count++;
                if (sl.Count <= limit) return true;
                sl.Dropped++; droppedTotal++;
                return false;
            }
        }
    }
}
