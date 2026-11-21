using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Brix.Game;
using Brix.Game.Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.84 resync while the game is paused (idea from the user: nothing moves during a pause, so it is the safest moment to line everything up).
    //   Host, once per pause (0.5 s after it starts): marks every component of every network object dirty, so UNET sends the complete synchronized
    //     state once more (fixes anything a guest may hold stale). The host's GameClock.Age rides in the pause packet (bytes 5-8); 0.3.86 found it is
    //     not the timer on screen (see TimeSync.cs), it is kept only for older guests.
    //   Guest, while paused: once a second puts every unit exactly on its authoritative position and facing (LastServerPosition/Rotation) instead of
    //     where interpolation froze it; when the pause ends the smoothing history is reset so units do not "catch up" after the pause.
    // 0.3.86 confirmed state check (extra packet kind 17; older bridges drop it):
    //   Host, 1 s after the full re-send (UNET has sent it by then): a digest of every unit (net id + position, generation number) goes to the guests.
    //   Guest: compares each unit it can see (fog hides some: counted apart) with the position UNET gave it; while some still differ by more than
    //     0.25 u it waits for data still on the way (up to 4 s), then answers with the counts and whether its simulation is really stopped.
    //   Host: a guest that reports differences gets a new full re-send with the next generation (at most 3 per pause); a guest that does not answer
    //     gets the digest again after 4 s (at most 3 times). "Continue" waits until every guest that ever answered has confirmed the current
    //     generation, at most 5 s; a guest that left is not waited for. Answers and digests of an older generation are ignored.
    public sealed partial class SteamBridgePlugin
    {
        const byte KindSyncCheck = BridgeCore.KindSyncCheck;
        static ConfigEntry<bool> prCfg; static float prResyncAt = -1f, prNextSnap; static bool prWasPaused; static int prResyncs;

        void InstallPauseResync()
        {
            try { prCfg = Config.Bind("Game", "PauseResync", true, "While the host pause is on: the host re-sends the complete game state, checks with every guest that units match (re-sends when not) and continues only after the check (at most 5 s); guests put units exactly where the host has them."); }
            catch (Exception e) { Logger.LogWarning("PAUSE-RESYNC unavailable: " + e.Message); }
            InstallAtmosphereGuard();
            try { prFullCfg = Config.Bind("Game", "FullResyncTest", false, "0.3.90: renamed from FullResync, so an old saved value (0.3.87 defaulted to true) no longer switches it on. Host: re-send the COMPLETE game state on pause and after outages. Off: it makes guests re-run the game's change handlers (faction, rules, projects) and broke a guest's tools in a live game (B-056). Only for tests."); } catch (Exception) { }
            try { prOutageCfg = Config.Bind("Game", "OutageResync", true, "Host: after a guest was silent for more than 5 s, re-send the complete game state once it answers again (fixes state lost in long outages)."); } catch (Exception) { }
        }

        static float PrHostAge() { try { return GameClock.Age; } catch (Exception) { return -1f; } }

        // pause packet payload: [0]=on [1..4]=seq [5..8]=host GameClock.Age (float, -1 = unknown); 0.3.86 fields after that, see HpBroadcast
        static void PrWriteAge(byte[] p) { byte[] a = BitConverter.GetBytes(PrHostAge()); Buffer.BlockCopy(a, 0, p, 5, 4); }

        void PauseResyncTick()
        {
            if (prCfg == null || !prCfg.Value) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (hosting)
                {
                    if (hpPaused && !prWasPaused) { prResyncAt = now + 0.5f; psRounds = 0; psAcks.Clear(); psDigestSentAt = -1f; psDigestAt = -1f; }
                    if (hpPaused && prResyncAt > 0 && now >= prResyncAt && NetworkServer.active) { prResyncAt = -1f; if (PrFullOn()) PrFullResync(); PsNewGeneration(now); }
                    if (!hpPaused) { prResyncAt = -1f; psDigestAt = -1f; }
                    PsHostTick(now); PsOutageWatch(now);
                }
                else
                {
                    if (hpPaused && hpByHost && now >= prNextSnap) { prNextSnap = now + 1f; PrSnapUnits(); }
                    PsGuestTick(now);
                }
                if (!hosting && prWasPaused && !hpPaused) { SmReset("pause ended (resync)"); }
            }
            catch (Exception e) { PtLog("PAUSE-RESYNC failed: " + e.Message); }
            prWasPaused = hpPaused;
        }

        // 0.3.88 B-056: a full re-send (every SyncVar of every object marked dirty) makes the guest run the game's SyncVar hooks as if every
        // value had just changed: User.factionSet (guest re-sends CmdSetFactionReady), Faction member list, AffiliationComponent.FactionChanged on
        // every object, GameParam.ParametersChanged, ProjectState / WorkForceProvider hooks... Live game 27.10 22:19: the first OutageResync full
        // re-send was followed by CmdSetFactionReady from the guest (as after each later pause re-send) and within ~40 s the guest's harvest tool,
        // bottom bar and building stopped working for the rest of the match. Off by default; the pause check still compares units and only logs.
        static ConfigEntry<bool> prFullCfg; static uint psDiffLoggedGen;
        static bool PrFullOn() { return prFullCfg != null && prFullCfg.Value; }

        void PrFullResync()
        {
            int objs = 0, comps = 0;
            foreach (KeyValuePair<NetworkInstanceId, NetworkIdentity> kv in NetworkServer.objects)
            {
                if (kv.Value == null) continue; objs++;
                foreach (NetworkBehaviour nb in kv.Value.GetComponents<NetworkBehaviour>()) { if (nb == null) continue; try { nb.SetDirtyBit(0xFFFFFFFFu); comps++; } catch (Exception) { } }
            }
            prResyncs++;
            PtLog("PAUSE-RESYNC host marked " + comps + " components of " + objs + " objects for a full re-send (#" + prResyncs + ")");
        }

        void PrSnapUnits()
        {
            if (getServerPos == null) return; int n = 0;
            for (int i = 0; i < posRegistry.Count; i++)
            {
                UNetTransform t = posRegistry[i]; if (t == null || t.UpdateOnlyOnSleep) continue;
                try { t.transform.position = getServerPos(t); if (getServerRot != null) t.transform.rotation = getServerRot(t); n++; } catch (Exception) { }
            }
            if (Time.frameCount % 5 == 0) PtLog("PAUSE-RESYNC guest placed " + n + " units on the host positions");
        }

        static void PrApplyHostAge(byte[] payload)
        {
            try
            {
                if (prCfg == null || !prCfg.Value || payload == null || payload.Length < 9) return;
                float host = BitConverter.ToSingle(payload, 5); if (host < 0f) return;
                float mine = PrHostAge(); if (mine < 0f || Math.Abs(mine - host) < 0.5f) return;
                GameClock.Age = host;
                PtLog("PAUSE-RESYNC guest GameClock.Age " + mine.ToString("F1") + " s -> host " + host.ToString("F1") + " s (diff " + (host - mine).ToString("F1") + " s)");
            }
            catch (Exception) { }
        }

        // ---------- 0.3.86 atmosphere guard ----------
        // T-149: every full re-send marks UNetJourneeSync's atmosphere colour dirty; on a guest without MaterialToGlobalSettings the game's
        // AtmosphereColorChanged throws a NullReferenceException inside OnDeserialize (13 per run, 16-18 in 0.3.85 runs), which also stops UNET
        // from reading the rest of that message. Without the settings object the colour is only stored, as the initial state does.
        void InstallAtmosphereGuard()
        {
            try
            {
                Type t = typeof(Brix.Game.Network.UNetJourneeSync);
                discoveryHarmony.Patch(AccessTools.Method(t, "AtmosphereColorChanged"), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AgBeforeColorChanged", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(AccessTools.Method(t, "AtmosphereColorSet"), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AgBeforeColorSet", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Logger.LogWarning("ATMOSPHERE-GUARD unavailable: " + e.Message); }
        }
        static int agSkipped;
        static bool AgBeforeColorChanged(Brix.Game.Network.UNetJourneeSync __instance, Color color)
        {
            if (Brix.Legacy.MaterialToGlobalSettings.instance != null) return true;
            __instance.AtmosphereColor = color; agSkipped++; if (agSkipped <= 3) PtLog("ATMOSPHERE-GUARD colour stored without settings object (#" + agSkipped + ")");
            return false;
        }
        static bool AgBeforeColorSet() { return Brix.Legacy.MaterialToGlobalSettings.instance != null; }

        // ---------- 0.3.86 outage resync ----------
        // T-147: after a 12 s outage a catapult (CatapultBody) stayed different on the guest until the next pause re-sent everything. Now the host
        // re-sends the complete state 2 s after a guest that was silent for more than 5 s (no ping answer) answers again. Once per outage.
        static ConfigEntry<bool> prOutageCfg; static readonly Dictionary<CSteamID, bool> psSilent = new Dictionary<CSteamID, bool>(); static float psOutageResyncAt = -1f, prLevelReadyAt = -1f; static int psOutageResyncs;

        void PsOutageWatch(float now)
        {
            if (prOutageCfg == null || !prOutageCfg.Value) return;
            // T-148: a guest that is still loading the level is silent too; a full re-send into its loading broke GeneralRules until the next one.
            // Only outages that start after both have been in the match for 60 s count.
            // T-149: the scene is active ~2 min before the level is ready; count from LEVEL-READY, and give a guest 2 min after it connected
            if (!HpInGame()) prLevelReadyAt = -1f;
            if (!HpInGame() || !NetworkServer.active || prLevelReadyAt < 0 || now - prLevelReadyAt < 60f) { psSilent.Clear(); psOutageResyncAt = -1f; return; }
            foreach (KeyValuePair<CSteamID, PingPeer> kv in pings)
            {
                // the reliable side: what the game traffic goes through (T-148: unreliable pings kept answering while reliable traffic was held)
                float since; if (!guests.ContainsKey(kv.Key) || kv.Value.Reliable.LastReplyAt <= 0f || !guestSince.TryGetValue(kv.Key, out since) || now - since < 120f) { psSilent.Remove(kv.Key); continue; }
                bool silent = now - kv.Value.Reliable.LastReplyAt > 5f, was; psSilent.TryGetValue(kv.Key, out was);
                if (silent && !was) { psSilent[kv.Key] = true; PtLog("OUTAGE-RESYNC guest " + kv.Key.m_SteamID + " silent for more than 5 s"); }
                else if (!silent && was) { psSilent[kv.Key] = false; psOutageResyncAt = now + 2f; PtLog("OUTAGE-RESYNC guest " + kv.Key.m_SteamID + " answers again -> full re-send in 2 s"); }
            }
            if (psOutageResyncAt > 0 && now >= psOutageResyncAt && NetworkServer.active && !hpPaused && PrFullOn()) { psOutageResyncAt = -1f; psOutageResyncs++; PrFullResync(); PtLog("OUTAGE-RESYNC full re-send done (#" + psOutageResyncs + ")"); }
        }

        // ---------- 0.3.86 confirmed state check ----------
        sealed class PsAck { public uint Gen; public int Checked, Differ, Hidden; public float MaxGap; public bool Stopped; public float At; }
        static uint psGen; static int psRounds, psDigestTries; static float psDigestAt = -1f, psDigestSentAt, psContinueAt = -1f;
        static readonly Dictionary<CSteamID, PsAck> psAcks = new Dictionary<CSteamID, PsAck>();
        static readonly HashSet<CSteamID> psCapable = new HashSet<CSteamID>();
        static byte[] psGuestDigest; static uint psGuestGen, psGuestSession; static int psGuestTries; static float psGuestCheckAt;

        void PsNewGeneration(float now) { psGen++; psRounds++; psDigestTries = 0; psDigestAt = now + 1f; }

        void PsHostTick(float now)
        {
            if (hpPaused && psDigestAt > 0 && now >= psDigestAt) { psDigestAt = -1f; PsSendDigest(now); }
            if (hpPaused && psGen > 0 && psDigestAt < 0 && psDigestSentAt > 0)
            {
                bool missing = false;
                foreach (var g in guests)
                {
                    if (!g.Value.Ready || !PsCanConfirm(g.Key)) continue; PsAck a; bool has = psAcks.TryGetValue(g.Key, out a) && a.Gen == psGen;
                    if (has && a.Differ > 0 && psRounds < 3 && !PrFullOn()) { if (psDiffLoggedGen != psGen) { psDiffLoggedGen = psGen; PtLog("PAUSE-SYNC guest " + g.Key.m_SteamID + " differs on " + a.Differ + " units (gen " + psGen + "); full re-send is off (FullResync=false), units are placed from the host positions only"); } continue; }
                    if (has && a.Differ > 0 && psRounds < 3) { PtLog("PAUSE-SYNC guest " + g.Key.m_SteamID + " still differs on " + a.Differ + " units (gen " + psGen + ") -> full re-send again"); PrFullResync(); PsNewGeneration(now); return; }
                    if (!has) missing = true;
                }
                if (missing && now - psDigestSentAt > 4f)
                {
                    if (psDigestTries < 3) { PtLog("PAUSE-SYNC no answer for gen " + psGen + " after 4 s -> digest again"); PsSendDigest(now); }
                    else if (psDigestSentAt > 0) { PtLog("PAUSE-SYNC no answer for gen " + psGen + " after " + psDigestTries + " digests: giving up on this pause"); psDigestSentAt = -1f; }
                }
            }
            if (psContinueAt > 0)
            {
                bool waiting = PsWaiting();
                if (!hpPaused) psContinueAt = -1f;
                else if (!waiting || now - psContinueAt > 5f)
                {
                    PtLog("PAUSE-SYNC continue " + (waiting ? "after 5 s timeout, state NOT confirmed by every guest" : "after confirmed state (gen " + psGen + ")"));
                    psContinueAt = -1f; HpSet(false);
                }
            }
        }

        // guests that answered before (0.3.86+) and have not confirmed the current generation without differences
        bool PsWaiting()
        {
            if (psGen == 0 || psRounds == 0) return false;
            foreach (var g in guests)
            {
                if (!g.Value.Ready || !PsCanConfirm(g.Key)) continue;
                PsAck a; if (!psAcks.TryGetValue(g.Key, out a) || a.Gen != psGen || (a.Differ > 0 && psRounds < 3 && PrFullOn())) return true;
            }
            return false;
        }

        // a guest can confirm when it answered before or announced bridge 0.3.86+
        bool PsCanConfirm(CSteamID id)
        {
            if (psCapable.Contains(id)) return true;
            string v; return peerVersions.TryGetValue(id, out v) && UpdateVerifier.IsNewer(v, "0.3.85");
        }

        bool PsHoldContinue()
        {
            if (prCfg == null || !prCfg.Value || !PsWaiting()) return false;
            if (psContinueAt < 0) { psContinueAt = Time.realtimeSinceStartup; status = "Перевіряю стан у гостей перед продовженням (до 5 с)..."; PtLog("PAUSE-SYNC continue requested: waiting for guest confirmation of gen " + psGen); }
            return true;
        }

        void PsSendDigest(float now)
        {
            // [0]=1 digest [1..4]=gen [5..8]=host session [9..10]=count, then per unit: net id (4) + position (12)
            List<UNetTransform> units = new List<UNetTransform>();
            for (int i = 0; i < posRegistry.Count && units.Count < 2800; i++) { UNetTransform t = posRegistry[i]; if (t != null && !t.UpdateOnlyOnSleep && t.netId.Value != 0) units.Add(t); }
            byte[] p = new byte[11 + units.Count * 16]; p[0] = 1;
            Buffer.BlockCopy(BitConverter.GetBytes(psGen), 0, p, 1, 4); Buffer.BlockCopy(BitConverter.GetBytes(HpSession()), 0, p, 5, 4);
            p[9] = (byte)units.Count; p[10] = (byte)(units.Count >> 8);
            for (int k = 0; k < units.Count; k++)
            {
                int o = 11 + k * 16; Vector3 v = units[k].transform.position;
                Buffer.BlockCopy(BitConverter.GetBytes(units[k].netId.Value), 0, p, o, 4); Buffer.BlockCopy(BitConverter.GetBytes(v.x), 0, p, o + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(v.y), 0, p, o + 8, 4); Buffer.BlockCopy(BitConverter.GetBytes(v.z), 0, p, o + 12, 4);
            }
            byte[] pk = BridgeCore.Packet(KindSyncCheck, p, p.Length); int sent = 0;
            foreach (var g in guests) { try { if (g.Value.Ready && SendTo(g.Key, pk)) sent++; } catch (Exception) { } }
            psDigestTries++; psDigestSentAt = now;
            PtLog("PAUSE-SYNC host digest gen " + psGen + " (" + units.Count + " units, " + p.Length + " B) to " + sent + " guest(s), try " + psDigestTries);
        }

        void OnSyncCheckPacket(CSteamID from, byte[] p)
        {
            try
            {
                if (prCfg == null || !prCfg.Value || p == null || p.Length < 9) return;
                uint gen = BitConverter.ToUInt32(p, 1);
                if (p[0] == 2 && hosting && p.Length >= 22)
                {
                    if (!guests.ContainsKey(from)) return;
                    psCapable.Add(from);
                    if (gen != psGen) { PtLog("PAUSE-SYNC old answer ignored (gen " + gen + ", current " + psGen + ")"); return; }
                    PsAck a = new PsAck(); a.Gen = gen; a.Checked = BitConverter.ToInt32(p, 5); a.Differ = BitConverter.ToInt32(p, 9); a.Hidden = BitConverter.ToInt32(p, 13);
                    a.MaxGap = BitConverter.ToSingle(p, 17); a.Stopped = p[21] == 1; a.At = Time.realtimeSinceStartup; psAcks[from] = a;
                    PtLog("PAUSE-SYNC guest " + from.m_SteamID + " answered gen " + gen + ": checked " + a.Checked + ", differ " + a.Differ + ", hidden by fog " + a.Hidden + ", max gap " + a.MaxGap.ToString("F2") + " u, simulation stopped " + a.Stopped + " (" + ((a.At - psDigestSentAt) * 1000f).ToString("F0") + " ms after the digest)");
                }
                else if (p[0] == 1 && !hosting && from == peer && p.Length >= 11)
                {
                    uint session = BitConverter.ToUInt32(p, 5);
                    if (session == psGuestSession && gen < psGuestGen) { PtLog("PAUSE-SYNC old digest ignored (gen " + gen + " < " + psGuestGen + ")"); return; }
                    psGuestSession = session; psGuestGen = gen; psGuestDigest = p; psGuestTries = 0; psGuestCheckAt = Time.realtimeSinceStartup;
                }
            }
            catch (Exception e) { PtLog("PAUSE-SYNC packet failed: " + e.Message); }
        }

        void PsGuestTick(float now)
        {
            if (psGuestDigest == null || now < psGuestCheckAt || getServerPos == null) return;
            byte[] p = psGuestDigest; int n = p[9] | (p[10] << 8), checkedN = 0, differ = 0, hidden = 0; float maxGap = 0f;
            for (int k = 0; k < n && 11 + k * 16 + 16 <= p.Length; k++)
            {
                int o = 11 + k * 16; uint id = BitConverter.ToUInt32(p, o);
                Vector3 v = new Vector3(BitConverter.ToSingle(p, o + 4), BitConverter.ToSingle(p, o + 8), BitConverter.ToSingle(p, o + 12));
                GameObject go = ClientScene.FindLocalObject(new NetworkInstanceId(id)); UNetTransform t = go == null ? null : go.GetComponent<UNetTransform>();
                if (t == null) { hidden++; continue; }
                checkedN++; float gap = Vector3.Distance(getServerPos(t), v); if (gap > maxGap) maxGap = gap; if (gap > 0.25f) differ++;
            }
            if (differ > 0 && psGuestTries < 4) { psGuestTries++; psGuestCheckAt = now + 1f; return; }   // state may still be on the way
            psGuestDigest = null;
            bool stopped = hpPaused && Time.timeScale == 0f;
            byte[] a = new byte[22]; a[0] = 2;
            Buffer.BlockCopy(BitConverter.GetBytes(psGuestGen), 0, a, 1, 4); Buffer.BlockCopy(BitConverter.GetBytes(checkedN), 0, a, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(differ), 0, a, 9, 4); Buffer.BlockCopy(BitConverter.GetBytes(hidden), 0, a, 13, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(maxGap), 0, a, 17, 4); a[21] = (byte)(stopped ? 1 : 0);
            try { SendTo(peer, BridgeCore.Packet(KindSyncCheck, a, a.Length)); } catch (Exception) { }
            if (differ == 0) PrSnapUnits();
            PtLog("PAUSE-SYNC guest gen " + psGuestGen + ": checked " + checkedN + ", differ " + differ + ", hidden " + hidden + ", max gap " + maxGap.ToString("F2") + " u, stopped " + stopped + ", waited " + psGuestTries + " s");
        }
    }
}
