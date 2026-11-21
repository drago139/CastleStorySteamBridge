using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.AI;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.85 B-050 rest sync: loose resources (RawStone etc., KinematicProjectile + Rigidbody, no transform sync) fall and roll by local physics
    // on each PC: pieces dropped by digging/destroyed blocks and thrown pieces after their hit (the physicalize SyncVar is never set). Guests end with
    // the piece 1-4 u away from where the host has it, for the rest of the match.
    // Host: once a second every spawned object with a non-kinematic Rigidbody and a KinematicProjectile, not carried (no parent), is checked; when it rests
    // for the first time or more than 0.2 u from where it was last sent, its position and rotation go to 0.3.85+ guests (extra packet kind 16; older bridges drop
    // kinds above their range). (First try hooked KinematicProjectile.InitBeforeSpawn: digging drops never pass there, so nothing was ever sent.)
    // Guest: puts the piece exactly there, stops its local flight and holds it still (kinematic; Physicalize() / the next throw reset that).
    public sealed partial class SteamBridgePlugin
    {
        const byte KindRestSync = BridgeCore.KindRestSync;
        sealed class RsEntry { public Rigidbody Rb; public Transform T; public Vector3 Sent; public bool HasSent; public int Seen; }
        static ConfigEntry<bool> rsCfg; static readonly Dictionary<uint, RsEntry> rsKnown = new Dictionary<uint, RsEntry>();
        static float rsNext; static int rsScan, rsSentTotal, rsAppliedTotal;

        void InstallRestSync()
        {
            rsCfg = Config.Bind("Game", "RestSync", true, "Host: loose resources that came to rest are put at the same place on every guest (B-050).");
        }

        void RestSyncTick()
        {
            if (rsCfg == null || !rsCfg.Value) return;
            float now = Time.realtimeSinceStartup; if (now < rsNext) return; rsNext = now + 1f;
            try
            {
                if (!hosting || !NetworkServer.active) { if (rsKnown.Count > 0) rsKnown.Clear(); return; }
                rsScan++; byte[] buf = null; int n = 0, loose = 0;
                foreach (NetworkIdentity ni in NetworkServer.objects.Values)
                {
                    if (ni == null) continue;
                    uint id = ni.netId.Value; RsEntry e;
                    if (!rsKnown.TryGetValue(id, out e))
                    {
                        e = new RsEntry(); e.Seen = rsScan; rsKnown[id] = e;
                        if (ni.GetComponent<KinematicProjectile>() == null) continue;   // Rb stays null: not a loose resource
                        e.Rb = ni.GetComponent<Rigidbody>(); e.T = ni.transform; if (e.Rb != null) e.Sent = e.T.position;
                    }
                    e.Seen = rsScan;
                    Rigidbody rb = e.Rb; if (rb == null || rb.isKinematic || RsCarried(e.T) || !ni.gameObject.activeInHierarchy) continue;
                    loose++;
                    if (!(rb.IsSleeping() || (rb.velocity.sqrMagnitude < 0.01f && rb.angularVelocity.sqrMagnitude < 0.01f))) continue;
                    Vector3 p = e.T.position; if (e.HasSent && (p - e.Sent).sqrMagnitude < 0.04f) continue;   // first rest is always sent: a piece dug out can settle differently on a guest that removes the block later (T-143)
                    if (!RsRemoteSees(ni)) { e.Sent = p; e.HasSent = true; continue; }   // hidden from guests (fog): they get the current place when it is spawned for them
                    if (n == 120) break;   // the rest goes next second
                    if (buf == null) buf = new byte[2 + 120 * 32];
                    Quaternion q = e.T.rotation; int o = 2 + n * 32;
                    RsPut(buf, o, id); RsPutF(buf, o + 4, p.x); RsPutF(buf, o + 8, p.y); RsPutF(buf, o + 12, p.z);
                    RsPutF(buf, o + 16, q.x); RsPutF(buf, o + 20, q.y); RsPutF(buf, o + 24, q.z); RsPutF(buf, o + 28, q.w);
                    ResourceScatterLog.Sync(ni.gameObject, p, "HOST_SEND");
                    e.Sent = p; e.HasSent = true; n++;
                }
                if (rsScan % 60 == 0)
                {
                    int kps = 0, kin = 0, par = 0; string pn = "-"; foreach (var kv in rsKnown) { RsEntry x = kv.Value; if (x.T == null) continue; kps++; if (x.Rb == null || x.Rb.isKinematic) kin++; if (RsCarried(x.T)) par++; if (x.T.parent != null && pn == "-") pn = x.T.parent.name; }
                    PtLog("REST-SYNC scan objects=" + NetworkServer.objects.Count + " resources=" + kps + " kinematicOrNoBody=" + kin + " carried=" + par + " loose=" + loose + " parent e.g. " + pn + " sent total " + rsSentTotal);
                }
                if (rsScan % 60 == 0) { List<uint> gone = new List<uint>(); foreach (var kv in rsKnown) if (kv.Value.Seen != rsScan) gone.Add(kv.Key); foreach (uint id in gone) rsKnown.Remove(id); }
                if (n == 0) return;
                for (int start = 0; start < n; start += 40)
                {
                    int c = Math.Min(40, n - start); byte[] part = new byte[2 + c * 32]; part[0] = 1; part[1] = (byte)c;
                    Buffer.BlockCopy(buf, 2 + start * 32, part, 2, c * 32);
                    byte[] pk = BridgeCore.Packet(KindRestSync, part, part.Length);
                    foreach (var g in guests) { try { if (g.Value.Ready) SendTo(g.Key, pk); } catch (Exception) { } }
                }
                rsSentTotal += n; PtLog("REST-SYNC host sent " + n + " resting pieces (total " + rsSentTotal + ", loose " + loose + ", known " + rsKnown.Count + ")");
            }
            catch (Exception e) { PtLog("REST-SYNC failed: " + e.Message); }
        }

        // carried / stored = under another networked object (unit, stockpile); loose pieces sit under plain scene containers
        static bool RsCarried(Transform t) { Transform p = t.parent; return p != null && p.GetComponentInParent<NetworkIdentity>() != null; }

        static bool RsRemoteSees(NetworkIdentity ni)
        {
            try { if (ni.observers == null) return false; foreach (NetworkConnection c in ni.observers) if (c != null && c.GetType().Name.IndexOf("Local", StringComparison.Ordinal) < 0) return true; } catch (Exception) { return true; }
            return false;
        }

        static void RsPut(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static void RsPutF(byte[] b, int o, float f) { Buffer.BlockCopy(BitConverter.GetBytes(f), 0, b, o, 4); }

        void OnRestSyncPacket(CSteamID from, byte[] payload)
        {
            try
            {
                if (hosting || from != peer || rsCfg == null || !rsCfg.Value || payload == null || payload.Length < 2 || payload[0] != 1) return;
                int n = payload[1]; if (payload.Length < 2 + n * 32) return;
                int applied = 0, missing = 0; float maxGap = 0f;
                for (int k = 0; k < n; k++)
                {
                    int o = 2 + k * 32; uint id = BitConverter.ToUInt32(payload, o);
                    Vector3 p = new Vector3(BitConverter.ToSingle(payload, o + 4), BitConverter.ToSingle(payload, o + 8), BitConverter.ToSingle(payload, o + 12));
                    Quaternion q = new Quaternion(BitConverter.ToSingle(payload, o + 16), BitConverter.ToSingle(payload, o + 20), BitConverter.ToSingle(payload, o + 24), BitConverter.ToSingle(payload, o + 28));
                    GameObject go = ClientScene.FindLocalObject(new NetworkInstanceId(id));
                    ResourceScatterLog.Sync(go, p, "RECEIVED", id);
                    if (go == null || RsCarried(go.transform)) { missing++; continue; }
                    float gap = Vector3.Distance(go.transform.position, p); if (gap > maxGap) maxGap = gap;
                    KinematicProjectile kp = go.GetComponent<KinematicProjectile>(); if (kp != null && kp.enabled) kp.enabled = false;
                    Rigidbody rb = go.GetComponent<Rigidbody>();
                    if (rb != null) { if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } rb.isKinematic = true; }
                    go.transform.position = p; go.transform.rotation = q; applied++;
                    ResourceScatterLog.Sync(go, p, "APPLIED");
                }
                rsAppliedTotal += applied;
                PtLog("REST-SYNC guest placed " + applied + " pieces (max gap " + maxGap.ToString("F2") + " u, not found/carried " + missing + ", total " + rsAppliedTotal + ")");
            }
            catch (Exception) { }
        }
    }
}
