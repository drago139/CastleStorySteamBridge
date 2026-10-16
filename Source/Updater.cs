using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // Friend-to-friend updates (0.3.7). A newer bridge offers itself to an older one over the existing link;
    // the older side asks (button), receives the package, checks the author's signature and replaces its own
    // DLL by renaming it. Nothing is downloaded from the internet and nothing is executed.
    public sealed partial class SteamBridgePlugin
    {
        readonly Dictionary<CSteamID, bool> versionAnnounced = new Dictionary<CSteamID, bool>();
        readonly Dictionary<CSteamID, string> peerVersions = new Dictionary<CSteamID, string>();
        sealed class ServeJob
        {
            internal CSteamID Peer; internal int Generation, Index; internal byte[][] Packets;
            internal string Error; internal volatile bool Ready;
        }
        sealed class InstallJob { internal string Message; internal volatile bool Ready; }
        ServeJob serveJob;
        InstallJob installJob;
        int transferGeneration;
        float nextUpdateChunkAt;
        void CancelUpdateTransfers()
        {
            transferGeneration++; serveJob = null; xferActive = false; receiver = null; pendingServePeer = default(CSteamID);
        }
        bool PeerLinkReady(CSteamID id)
        {
            BridgeCore guest;
            return hosting ? guests.TryGetValue(id, out guest) && guest.Ready : bridge != null && bridge.Ready && peer == id;
        }
        void UpdateTransferTick()
        {
            if (installJob != null && installJob.Ready)
            {
                updateStatus = installJob.Message; installJob = null; packageState = 0; packageLogged = false;
                Logger.LogInfo(updateStatus);
            }
            ServeJob job = serveJob;
            if (job == null || !job.Ready) return;
            if (job.Generation != transferGeneration || !PeerLinkReady(job.Peer)) { serveJob = null; return; }
            if (job.Error != null) { Logger.LogWarning("Update preparation failed: " + job.Error); serveJob = null; return; }
            if (Time.realtimeSinceStartup < nextUpdateChunkAt) return;
            P2PSessionState_t link;
            if (SteamNetworking.GetP2PSessionState(job.Peer, out link) && link.m_nBytesQueuedForSend > 128 * 1024) return;
            // At most one 16 KiB chunk per frame, and 20 per second; game traffic is pumped first.
            nextUpdateChunkAt = Time.realtimeSinceStartup + 0.05f;
            try
            {
                SendTo(job.Peer, job.Packets[job.Index++]);
                if (job.Index == job.Packets.Length) { serveJob = null; Logger.LogInfo("Signed update sent to " + PingName(job.Peer)); }
            }
            catch (Exception e) { serveJob = null; Logger.LogWarning("Update send failed: " + e.Message); }
        }
        CSteamID offerFrom;
        string offerVersion = "";
        CSteamID xferFrom;
        uint xferId;
        UpdatePackage.Receiver receiver;
        bool xferActive;
        float xferStarted, nextVersionTick, lastServe;
        string updateStatus = "";
        volatile int packageState; // 0 unknown, 1 can serve, 2 cannot, 3 checking
        CSteamID pendingServePeer; byte[] pendingServePayload;

        string PluginPath { get { return Info != null ? Info.Location : null; } }

        // Leftovers of a previous update (the old DLL could not be deleted while it was loaded).
        void CleanupUpdateLeftovers()
        {
            try
            {
                string p = PluginPath;
                if (string.IsNullOrEmpty(p)) return;
                foreach (string s in new string[] { p + ".old", p + ".new", p + ".sig.old", p + ".sig.new" }) if (File.Exists(s)) File.Delete(s);
            }
            catch (Exception e) { Logger.LogWarning("Update cleanup: " + e.Message); }
        }
        bool packageLogged;
        // 0.3.12: the RSA check reads and verifies the whole DLL; it runs on a pool thread so the game's main loop never waits for it.
        bool PackageAvailable()
        {
            if (packageState == 0) StartPackageCheck();
            return packageState == 1;
        }
        void StartPackageCheck()
        {
            packageState = 3;
            string path = PluginPath, version = BridgeVersion.Text;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                int result = 2;
                try
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path) && File.Exists(path + ".sig")
                        && UpdateVerifier.Verify(version, File.ReadAllBytes(path), File.ReadAllBytes(path + ".sig"))) result = 1;
                }
                catch (Exception) { }
                packageState = result;
            });
        }
        // Runs about once a second: announces our version to every newly connected friend and times out a stalled transfer.
        void UpdateTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextVersionTick) return;
            nextVersionTick = now + 1;
            if (xferActive && now - xferStarted > 90)
            {
                xferActive = false; receiver = null; updateStatus = "Отримання оновлення перервано: немає відповіді 90 с. Спробуй ще раз.";
                offerFrom = xferFrom;       // 0.3.30: the button comes back, no reconnect needed
            }
            PackageAvailable();
            // 0.3.30: a request that arrived while the signature check was still running is answered now, not dropped.
            if (pendingServePeer.m_SteamID != 0 && packageState != 3)
            {
                CSteamID who = pendingServePeer; pendingServePeer = default(CSteamID);
                if (packageState == 1 && PeerLinkReady(who)) ServeUpdate(who, pendingServePayload);
            }
            if (!packageLogged && (packageState == 1 || packageState == 2))
            {
                packageLogged = true;
                Logger.LogInfo("Updates from this copy: " + (packageState == 1 ? "available (signed)" : "not available (no valid signature file)"));
            }
            foreach (CSteamID id in DiagPeers())
            {
                if (versionAnnounced.ContainsKey(id)) continue;
                if (packageState == 3) continue;          // the signature check is still running; announce on the next tick
                versionAnnounced[id] = true;
                byte[] p = Encoding.UTF8.GetBytes(BridgeVersion.Text + "|" + (PackageAvailable() ? "1" : "0"));
                try { SendTo(id, BridgeCore.Packet(BridgeCore.KindVersion, p, p.Length)); }
                catch (Exception e) { Logger.LogWarning("Version announce failed: " + e.Message); }
            }
            PingTick();
            AutoShareTick();
        }
        void OnExtraPacket(CSteamID from, byte kind, byte[] payload)
        {
            if (kind == BridgeCore.KindDiagnosticsRequest || kind == BridgeCore.KindDiagnosticsReport) OnDiagnosticsPacket(from, kind, payload);
            else if (kind == BridgeCore.KindPing || kind == BridgeCore.KindPong) OnPingPacket(from, kind, payload);
            else if (kind == BridgeCore.KindMapChoice) OnMapProposal(from, payload);
            else if (kind == BridgeCore.KindItemFilter) OnItemFilterPacket(from, payload);
            else OnUpdatePacket(from, kind, payload);
        }
        void OnUpdatePacket(CSteamID from, byte kind, byte[] payload)
        {
            try
            {
                if (!IsTrusted(from) || payload == null && kind != BridgeCore.KindVersion) return;
                if (kind == BridgeCore.KindVersion)
                {
                    if (payload == null || payload.Length > 40) return;
                    string[] f = Encoding.UTF8.GetString(payload).Split('|');
                    System.Version parsed;
                    if (f.Length == 2 && UpdateVerifier.TryParse(f[0], out parsed)) peerVersions[from] = f[0];
                    // A peer on 0.3.5 or newer understands report packets: hand over the saved old logs once.
                    if (f.Length == 2 && UpdateVerifier.IsNewer(f[0], "0.3.4")) SharePreviousLogs(from);
                    if (f.Length != 2 || f[1] != "1" || xferActive || !UpdateVerifier.IsNewer(f[0], BridgeVersion.Text)) return;
                    offerFrom = from; offerVersion = f[0]; visible = true;
                    updateStatus = "Друг " + SteamFriends.GetFriendPersonaName(from) + " має новішу версію моста " + f[0] + " (у вас " + BridgeVersion.Text + ").";
                    Logger.LogInfo(updateStatus);
                }
                else if (kind == BridgeCore.KindUpdateRequest) ServeUpdate(from, payload);
                else if (kind == BridgeCore.KindUpdateChunk) ReceiveChunk(from, payload);
            }
            catch (Exception e) { Logger.LogWarning("Update packet failed: " + e.Message); }
        }
        void RequestUpdate()
        {
            if (offerFrom.m_SteamID == 0 || xferActive) return;
            xferId = (uint)new System.Random().Next(1, int.MaxValue);
            xferFrom = offerFrom; receiver = new UpdatePackage.Receiver(xferId); xferActive = true; xferStarted = Time.realtimeSinceStartup;
            byte[] p = Encoding.UTF8.GetBytes(BridgeVersion.Text + "|" + xferId);
            try { SendTo(xferFrom, BridgeCore.Packet(BridgeCore.KindUpdateRequest, p, p.Length)); updateStatus = "Запит оновлення надіслано. Чекаю на файли..."; }
            catch (Exception e) { xferActive = false; receiver = null; updateStatus = "Не вдалося попросити оновлення: " + e.Message; }
            if (xferActive) offerFrom = default(CSteamID);
        }
        void ServeUpdate(CSteamID to, byte[] payload)
        {
            string[] f = Encoding.UTF8.GetString(payload).Split('|');
            uint id;
            if (f.Length != 2 || !uint.TryParse(f[1], out id) || !UpdateVerifier.IsNewer(BridgeVersion.Text, f[0])) return;
            bool available = PackageAvailable();
            if (packageState == 3) { pendingServePeer = to; pendingServePayload = payload; return; }
            if (!available || Time.realtimeSinceStartup < lastServe + 3) return;
            if (serveJob != null) return;
            lastServe = Time.realtimeSinceStartup;
            ServeJob job = new ServeJob(); job.Peer = to; job.Generation = transferGeneration; serveJob = job;
            string path = PluginPath, version = BridgeVersion.Text;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try
                {
                    byte[] dll = File.ReadAllBytes(path), sig = File.ReadAllBytes(path + ".sig");
                    if (!UpdateVerifier.Verify(version, dll, sig)) throw new InvalidOperationException("Package changed; signature/version mismatch");
                    byte[] blob = UpdatePackage.Build(version, dll, sig);
                    List<byte[]> chunks = UpdatePackage.Chunks(id, blob);
                    job.Packets = new byte[chunks.Count][];
                    for (int i = 0; i < chunks.Count; i++) job.Packets[i] = BridgeCore.Packet(BridgeCore.KindUpdateChunk, chunks[i], chunks[i].Length);
                }
                catch (Exception e) { job.Error = e.Message; }
                finally { job.Ready = true; }
            });
        }
        void ReceiveChunk(CSteamID from, byte[] payload)
        {
            if (!xferActive || receiver == null || from != xferFrom) return;
            string error;
            if (!receiver.Accept(payload, out error)) { xferActive = false; receiver = null; offerFrom = xferFrom; updateStatus = "Оновлення відхилено: " + error + "."; Logger.LogWarning(updateStatus); return; }
            xferStarted = Time.realtimeSinceStartup;
            updateStatus = "Отримую оновлення: " + (receiver.Got / 1024) + " з " + (receiver.Total / 1024) + " КБ";
            if (receiver.Complete)
            {
                byte[] blob = receiver.Blob; xferActive = false; receiver = null;
                FinishUpdate(blob);
            }
        }
        void FinishUpdate(byte[] blob)
        {
            if (installJob != null) return;
            InstallJob job = new InstallJob(); installJob = job;
            string target = PluginPath, currentVersion = BridgeVersion.Text;
            updateStatus = "Перевіряю підпис і готую оновлення у фоні...";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try
                {
                    string version; byte[] dll, sig;
                    if (!UpdatePackage.Parse(blob, out version, out dll, out sig)) throw new InvalidOperationException("пошкоджений пакет");
                    if (!UpdateVerifier.IsNewer(version, currentVersion)) throw new InvalidOperationException("версія не новіша");
                    if (!UpdateVerifier.Verify(version, dll, sig)) throw new InvalidOperationException("підпис автора не збігається");
                    // Metadata validation in a separate file, before touching the installed DLL.
                    string validation = target + ".validate";
                    try { File.WriteAllBytes(validation, dll); AssemblyName.GetAssemblyName(validation); }
                    finally { if (File.Exists(validation)) File.Delete(validation); }
                    UpdateFileStore.Apply(target, dll, sig, null);
                    job.Message = "Оновлення " + version + " встановлено. Перезапусти гру, щоб воно почало працювати.";
                }
                catch (Exception e) { job.Message = "Оновлення не встановлено: " + e.Message + ". Попередня версія збережена."; }
                finally { job.Ready = true; }
            });
        }
        void DrawUpdate()
        {
            GUILayout.Label("Версія моста " + BridgeVersion.Text + "; роздача оновлень друзям: " + (PackageAvailable() ? "так" : "ні (немає підпису)"));
            if (updateStatus.Length > 0) GUILayout.Label(updateStatus);
            if (offerFrom.m_SteamID != 0)
            {
                if (GUILayout.Button("Отримати оновлення " + offerVersion + " від друга")) RequestUpdate();
                if (GUILayout.Button("Не зараз")) offerFrom = default(CSteamID);
            }
        }
    }
}
