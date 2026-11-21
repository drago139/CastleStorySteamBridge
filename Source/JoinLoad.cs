using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Brix.Network;
using Brix.Game.Network;
using HarmonyLib;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    public sealed partial class SteamBridgePlugin
    {
        const int JoinLoadChannel = 79; // 77 game, 78 positions; never share their queues.
        const string JoinLoadPresence = "csb_joinload_v1";
        sealed class JoinWindow
        {
            internal NetworkConnection Connection;
            internal float Started, LastState, NextSend;
            internal uint Token;
            internal CSteamID Peer;
            internal JoinLoadRing Ring = new JoinLoadRing();
            internal bool Loading, SawLoading, Complete, Saved, Allowed, Asked, Sent, Denied, Disconnected;
            internal int Step = -1, MaxStep = -1, StepAge;
            internal byte[] Report;
            internal int Offset;
        }
        readonly List<JoinWindow> jlWindows = new List<JoinWindow>(4);
        readonly JoinLoadRing jlLocal = new JoinLoadRing();
        readonly JoinLoadReceiver jlReceiver = new JoinLoadReceiver();
        readonly byte[] jlPacket = new byte[1024*1024]; // Fixed upper bound, drains oversized reliable Steam packets without allocating.
        ConfigEntry<string> jlConsent;
        JoinWindow jlPrompt;
        CSteamID jlHost;
        uint jlToken;
        float jlNextSecond, jlNextReceive, jlNextState, jlUntil, jlWorst, jlGlobalSend;
        int jlGc;
        bool jlWasClient, jlWasLoading, jlSaved, jlReady, jlGotReport;
        string jlStatus = "Waiting for a compatible host (0.3.72+).", jlReceived = "", jlPresence = "";
        bool jlShowReport;
        Rect jlConsentRect = new Rect(160, 100, 490, 165);
        Vector2 jlReportScroll;
        double jlCostMs, jlWorstCost; int jlCostCount;

        void InstallJoinLoad()
        {
            try
            {
                jlConsent = Config.Bind("Diag", "ShareJoinLoad", "Ask", new ConfigDescription(
                    "Share technical loading samples with the connected Steam friend only. Ask prompts once; Always remembers approval; Never sends no report.",
                    new AcceptableValueList<string>("Ask", "Always", "Never")));
                foreach (string name in new string[] { "OnServerConnect", "OnServerDisconnect" })
                {
                    MethodInfo target = AccessTools.Method(typeof(UNetManager),name,new Type[] { typeof(NetworkConnection) });
                    if (target == null) throw new MissingMethodException(name);
                    discoveryHarmony.Patch(target, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod(name == "OnServerConnect" ? "JlConnected" : "JlDisconnected",BindingFlags.Static | BindingFlags.NonPublic)));
                }
                jlReady = true; jlGc = GC.CollectionCount(0); jlToken = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(),0);
                Logger.LogInfo("JOINLOAD v1 ready: 120s connection windows; channel 79, 16KB, <=1.6KB/s total; consent Ask; no game message hooks.");
            }
            catch (Exception e) { Logger.LogWarning("JOINLOAD initialization failed: " + e); }
        }
        static void JlConnected(NetworkConnection __0)
        {
            SteamBridgePlugin p = discoveryInstance;
            if (p == null || __0 == null) return;
            foreach (JoinWindow w in p.jlWindows) if (w.Connection == __0) return;
            if(p.jlWindows.Count>=4)
                for(int i=0;i<p.jlWindows.Count;i++)if(p.jlWindows[i].Disconnected){p.JlSave(p.jlWindows[i],"retired disconnected window");p.jlWindows.RemoveAt(i);break;}
            if(p.jlWindows.Count>=4)return;
            p.jlWindows.Add(new JoinWindow { Connection = __0, Started = Time.realtimeSinceStartup });
            p.JlEvent("connect", __0.connectionId);
        }
        static void JlDisconnected(NetworkConnection __0)
        {
            SteamBridgePlugin p = discoveryInstance; if (p == null) return;
            foreach (JoinWindow w in p.jlWindows) if (w.Connection == __0) { w.Disconnected = w.Complete = true; p.JlSave(w,"disconnect"); p.JlEvent("disconnect",__0.connectionId); }
        }
        void JoinLoadBegin()
        {
            float now=Time.realtimeSinceStartup;
            if(NetworkServer.active)
            {
                foreach(NetworkConnection c in NetworkServer.connections) if(c!=null)JlConnected(c);
                foreach(JoinWindow w in jlWindows)
                { w.Started=now;w.Ring=new JoinLoadRing();w.Complete=w.Saved=w.Sent=w.SawLoading=false;w.Report=null;w.Offset=0; }
            }
            JlEvent("load-begin",-1);
        }
        void JlEvent(string name, int connection)
        {
            string line = "JOINLOAD event=" + name + " conn=" + connection + " t=" + Time.realtimeSinceStartup.ToString("F1");
            Logger.LogInfo(line); WriteFreezeLine(line,false); PtLog(line);
        }

        void JoinLoadTick()
        {
            if (!jlReady) return;
            float now = Time.realtimeSinceStartup;
            bool client = !NetworkServer.active && NetworkClient.active;
            if ((client && !jlWasClient) || (loadActive && !jlWasLoading && !NetworkServer.active))
            {
                jlToken++; if (jlToken == 0) jlToken++;
                jlUntil = now + 150; jlSaved = jlGotReport = false; jlHost = new CSteamID(0); jlNextState = 0; jlReceiver.Reset();
                jlStatus = "Waiting for host diagnostics; loading reports transfer after loading.";
            }
            if ((jlWasClient && !client) || (jlWasLoading && !loadActive))
            {
                jlUntil=now+60;
                if (!jlSaved) { JlPersist("joinload-local",jlLocal.Report("local load ended/disconnected")); jlSaved = true; }
                jlNextState = 0;
            }
            jlWasClient = client; jlWasLoading = loadActive;
            if(jlToken!=0 && jlUntil>0 && now>jlUntil && !jlGotReport && jlHost.m_SteamID!=0)jlStatus="звіту від хоста нема (він не поділився або час передачі минув); локальні записи збережено.";
            bool recording = loadActive || now <= jlUntil;
            for (int i = 0; i < jlWindows.Count; i++) if (!jlWindows[i].Complete && now - jlWindows[i].Started <= 120) recording = true;
            if (recording) jlWorst = Math.Max(jlWorst,Time.unscaledDeltaTime * 1000f);
            // State/discovery and channel receive are sampled; no allocations/lookups per frame.
            if (now < jlNextSecond) return;
            jlNextSecond = now + 1f;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int gc = GC.CollectionCount(0), delta = gc - jlGc; jlGc = gc;
                if (SteamManager.Initialized)
                {
                    JlPublish();
                    if ((now <= jlUntil || loadActive) && jlToken != 0 && !NetworkServer.active) JlSendState(now);
                    JlReceive(now);
                }
                if (recording)
                {
                    NetworkConnection c = null;
                    if (NetworkClient.allClients.Count > 0 && NetworkClient.allClients[0] != null) c = NetworkClient.allClients[0].connection;
                    JoinLoadSample sample = JlSample(c,NetworkServer.active,now,delta);
                    jlLocal.Add(sample);
                    if (loadActive && !NetworkServer.active) JlLog(sample);
                }
                for (int i = jlWindows.Count - 1; i >= 0; i--)
                {
                    JoinWindow w = jlWindows[i];
                    if (!w.Complete)
                    {
                        if (!NetworkServer.active || now - w.Started > 120) w.Complete = true;
                        else
                        {
                            JoinLoadSample sample = JlSample(w.Connection,true,now,delta);
                            if (w.Peer.m_SteamID != 0) { sample.Step = w.Step; sample.MaxStep = w.MaxStep; sample.StepAge = w.StepAge + now - w.LastState; }
                            w.Ring.Add(sample); JlLog(sample);
                            if (w.SawLoading && !w.Loading) w.Complete = true;
                        }
                    }
                    if (w.Complete) { JlSave(w,"window ended"); JlDeliver(w,now); }
                    if(!NetworkServer.active)w.Disconnected=true;
                    // Retain a dropped connection long enough for its authenticated side-channel end state/report.
                    if (w.Disconnected && now-w.Started>180) { if (jlPrompt == w) jlPrompt = null; jlWindows.RemoveAt(i); }
                }
            }
            catch (Exception e) { Logger.LogWarning("JOINLOAD sample failed: " + e.GetType().Name + " " + e.Message); }
            finally
            {
                jlWorst = 0;
                double cost = (System.Diagnostics.Stopwatch.GetTimestamp()-t0)*TickMs;
                jlCostMs += cost; jlWorstCost = Math.Max(jlWorstCost,cost); jlCostCount++;
                if (jlCostCount % 120 == 0) PtLog("JOINLOAD-COST samples=" + jlCostCount + " avgMs=" + (jlCostMs/jlCostCount).ToString("F3") + " maxMs=" + jlWorstCost.ToString("F3"));
            }
        }
        JoinLoadSample JlSample(NetworkConnection c, bool server, float now, int gc)
        {
            JoinLoadSample s = new JoinLoadSample { At=now, FrameMs=Time.unscaledDeltaTime*1000f, WorstMs=jlWorst,
                Conn=c==null ? -1 : c.connectionId, Host=c==null ? -1 : c.hostId, Server=server, Ready=c!=null && c.isReady,
                Route="unknown", Rtt=-1, Queue=-1, Bytes=-1, Lost=-1, Gc=gc, Ram=FreeRamMb(),
                Step=loadActive && !server ? loadProgress : -1, MaxStep=loadActive && !server ? loadMax : -1, StepAge=loadActive && !server ? now-lastLoadProgressAt : -1 };
            if (c == null) return s;
            string address = c.address ?? "";
            s.Route = address.StartsWith("26.",StringComparison.Ordinal) ? "Radmin-LAN" : "direct-LAN";
            if (LoopbackAddress.IsLoopback(address) || c.hostId < 0)
            {
                s.Route = c.hostId < 0 ? "local-loopback" : "bridge";
                CSteamID id = server ? new CSteamID(0) : peer;
                if (server) { int port = ConnectionPort(c); foreach (KeyValuePair<CSteamID,BridgeCore> g in guests) if (g.Value.Port == port) { id=g.Key; break; } }
                P2PSessionState_t state;
                if (SteamManager.Initialized && id.m_SteamID != 0 && SteamNetworking.GetP2PSessionState(id,out state)) s.Route += state.m_bUsingRelay != 0 ? "/relay" : "/P2P";
            }
            if (c.hostId < 0) return s;
            byte e;
            int n=NetworkTransport.GetCurrentRTT(c.hostId,c.connectionId,out e); if(e==0)s.Rtt=n;
            n=NetworkTransport.GetOutgoingMessageQueueSize(c.hostId,out e);if(e==0)s.Queue=n;
            n=NetworkTransport.GetOutgoingFullBytesCountForConnection(c.hostId,c.connectionId,out e);if(e==0)s.Bytes=n;
            n=NetworkTransport.GetIncomingPacketLossCount(c.hostId,c.connectionId,out e);if(e==0)s.Lost=n;
            return s;
        }
        void JlLog(JoinLoadSample s) { string text=s.ToString(); Logger.LogInfo(text); WriteFreezeLine(text,false); }
        void JlSave(JoinWindow w,string reason)
        {
            if(w.Saved)return; w.Saved=true;
            string report=w.Ring.Report(reason+"; mod="+BridgeVersion.Text+" build="+BridgeVersion.Build+" UTC-end="+DateTime.UtcNow.ToString("o")+" uptime-end="+Time.realtimeSinceStartup.ToString("F1"));
            w.Report=Encoding.UTF8.GetBytes(report); JlPersist("joinload-local",report);
        }
        void JlPersist(string prefix,string report)
        {
            string dir=Path.Combine(Paths.BepInExRootPath,"SteamBridgeDiagnostics");
            // Anonymous unique names: no Steam ID, friend name, IP, machine/user name or paths in transferred content.
            string name=prefix+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".txt";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try
                {
                    lock(jlFileLock)
                    {
                        Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,name),report,new UTF8Encoding(true));
                        string[] files=Directory.GetFiles(dir,prefix+"-*.txt");Array.Sort(files,StringComparer.Ordinal);
                        for(int i=0;i<files.Length-5;i++)File.Delete(files[i]);
                    }
                }
                catch(Exception e){ Logger.LogWarning("JOINLOAD save failed: "+e.Message); }
            });
        }
        static readonly object jlFileLock=new object();
        string JoinLoadReport() { return jlLocal.Report("local snapshot")+"\r\n--- HOST REPORT ---\r\n"+jlReceived; }

        void JlPublish()
        {
            string value="";
            if(NetworkServer.active)
            {
                RadminState state=CurrentRadminState();
                value="1|"+(state!=null && state.Service=="Running" ? state.Ip : "");
            }
            if(value==jlPresence)return;
            if(SteamFriends.SetRichPresence(JoinLoadPresence,value.Length==0 ? null : value))jlPresence=value;
        }
        uint JlIdentity()
        {
            UNetManager m=UNetManager.instance;
            if(m!=null && m.LocalLobbyPlayer!=null)return m.LocalLobbyPlayer.netId.Value;
            if(NetworkClient.allClients.Count>0)
            {
                NetworkClient c=NetworkClient.allClients[0];
                if(c!=null && c.connection!=null)
                    foreach(PlayerController pc in c.connection.playerControllers)
                        if(pc!=null && pc.gameObject!=null){NetworkIdentity ni=pc.gameObject.GetComponent<NetworkIdentity>();if(ni!=null)return ni.netId.Value;}
            }
            return 0;
        }
        CSteamID JlFindHost()
        {
            if(bridge!=null && IsFriend(peer))return peer;
            CSteamID lobby=SteamManager.Instance.LobbyId;
            if(lobby.m_SteamID!=0){CSteamID owner=SteamMatchmaking.GetLobbyOwner(lobby);if(owner!=SteamUser.GetSteamID() && IsFriend(owner))return owner;}
            // Radmin joins need no game bridge or Steam lobby. Match the actual target address to the friend's capability presence.
            if(NetworkClient.allClients.Count>0)
            {
                NetworkClient c=NetworkClient.allClients[0];string address=c==null || c.connection==null ? "" : c.connection.address;
                if(!string.IsNullOrEmpty(address) && address.StartsWith("26.",StringComparison.Ordinal))
                    for(int i=0;i<SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);i++)
                    {
                        CSteamID id=SteamFriends.GetFriendByIndex(i,EFriendFlags.k_EFriendFlagImmediate);
                        if(SteamFriends.GetFriendRichPresence(id,JoinLoadPresence)=="1|"+address)return id;
                    }
            }
            return new CSteamID(0);
        }
        void JlSendState(float now)
        {
            if(now<jlNextState)return; jlNextState=now+3;
            if(jlHost.m_SteamID==0)jlHost=JlFindHost();
            if(jlHost.m_SteamID==0 || !IsFriend(jlHost))return;
            string cap=SteamFriends.GetFriendRichPresence(jlHost,JoinLoadPresence);
            if(!cap.StartsWith("1|",StringComparison.Ordinal)) { jlStatus="Host has no JOINLOAD v1 capability (not shared).";return; }
            byte[] state=new byte[5];state[0]=(byte)(loadActive ? 1 : 0);
            state[1]=(byte)Math.Min(255,loadProgress);state[2]=(byte)Math.Min(255,loadMax);
            state[3]=(byte)Math.Min(255,(int)Math.Max(0,now-lastLoadProgressAt));state[4]=(byte)(GameClientConnected() ? 1 : 0);
            JlSend(jlHost,JoinLoadWire.Packet(JoinLoadWire.State,jlToken,JlIdentity(),0,state,state.Length));
        }
        bool JoinLoadAcceptRequest(CSteamID id)
        {
            if(!jlReady || !IsFriend(id))return false;
            if(jlHost==id && jlToken!=0)return true;
            // Accepting the Steam session is not consent to send a report. The bounded state handshake must map to a game connection first.
            if(NetworkServer.active && jlWindows.Count>0 && !guests.ContainsKey(id))return true;
            return false;
        }
        bool JlSend(CSteamID id,byte[] packet)
        {
            if(!IsFriend(id))return false;
            P2PSessionState_t s;
            if(SteamNetworking.GetP2PSessionState(id,out s) && s.m_nBytesQueuedForSend>32768)return false;
            return SteamNetworking.SendP2PPacket(id,packet,(uint)packet.Length,EP2PSend.k_EP2PSendReliable,JoinLoadChannel);
        }
        void JlReceive(float now)
        {
            if(now<jlNextReceive)return;jlNextReceive=now+0.2f;
            uint n;
            for(int i=0;i<8 && SteamNetworking.IsP2PPacketAvailable(out n,JoinLoadChannel);i++)
            {
                uint read;CSteamID from;
                // Drain into one fixed buffer, then reject oversized/foreign packets before parsing.
                if(!SteamNetworking.ReadP2PPacket(jlPacket,(uint)jlPacket.Length,out read,out from,JoinLoadChannel))break;
                if(n>JoinLoadWire.MaxPacket || read!=n || !IsFriend(from) || !JoinLoadWire.Valid(jlPacket,(int)read))continue;
                uint token=JoinLoadWire.Get(jlPacket,4);
                if(jlPacket[3]==JoinLoadWire.State && NetworkServer.active)
                {
                    if(read!=JoinLoadWire.Header+5)continue;
                    uint identity=JoinLoadWire.Get(jlPacket,8);
                    JoinWindow w=null;
                    foreach(JoinWindow x in jlWindows)if(x.Peer==from && x.Token==token){w=x;break;}
                    if(w==null)
                    {
                        GameObject obj=identity==0 ? null : NetworkServer.FindLocalObject(new NetworkInstanceId(identity));
                        NetworkIdentity ni=obj==null ? null : obj.GetComponent<NetworkIdentity>();
                        if(ni==null || ni.connectionToClient==null || ni.connectionToClient.hostId<0)continue;
                        // A netId is public, so it cannot alone bind a Steam sender to a player.
                        LobbyPlayer player = obj.GetComponent<LobbyPlayer>();
                        BridgeCore core; bool knownBridge = guests.TryGetValue(from, out core);
                        bool bridgeConnection = knownBridge || LoopbackAddress.IsLoopback(ni.connectionToClient.address);
                        if(!JoinLoadWire.CanBind(player==null ? 0 : player.Network_steamID,from.m_SteamID,bridgeConnection,
                            knownBridge && core.Port==ConnectionPort(ni.connectionToClient)))continue;
                        foreach(JoinWindow x in jlWindows)if(x.Connection==ni.connectionToClient && (x.Peer.m_SteamID==0 || x.Peer==from)){w=x;break;}
                        if(w==null)continue;
                        if(w.Token!=token && jlPacket[JoinLoadWire.Header]==1) { w.SawLoading=w.Sent=w.Saved=w.Complete=false;w.Offset=0;w.Report=null; }
                        w.Peer=from;w.Token=token;
                        // A new level in the same lobby is a fresh loading window.
                        if(jlPacket[JoinLoadWire.Header]==1 && !w.SawLoading){w.Started=now;w.Complete=w.Saved=false;w.Ring=new JoinLoadRing();}
                    }
                    w.LastState=now;w.Loading=jlPacket[JoinLoadWire.Header]==1;w.SawLoading|=w.Loading;
                    w.Step=jlPacket[JoinLoadWire.Header+1];w.MaxStep=jlPacket[JoinLoadWire.Header+2];w.StepAge=jlPacket[JoinLoadWire.Header+3];
                    if(!w.Asked)
                    {
                        if(jlConsent.Value=="Always"){w.Allowed=true;w.Asked=true;}
                        else if(jlConsent.Value=="Never"){w.Denied=true;w.Asked=true;JlSend(from,JoinLoadWire.Packet(JoinLoadWire.Declined,token,0,0,null,0));}
                        else if(jlPrompt==null){w.Asked=true;jlPrompt=w;}
                    }
                }
                else if(from==jlHost && token==jlToken)
                {
                    if(jlPacket[3]==JoinLoadWire.Declined){jlStatus="хост не поділився діагностикою.";continue;}
                    if(loadActive)continue; // Never receive/report during loading, even if a peer is mistaken.
                    string report=jlReceiver.Accept(jlPacket,(int)read,jlToken);
                    if(report!=null){jlGotReport=true;jlReceived=report;jlStatus="отримано о "+DateTime.Now.ToString("HH:mm:ss");JlPersist("peer-joinload",report);}
                }
            }
        }
        void JlDeliver(JoinWindow w,float now)
        {
            if(!SteamManager.Initialized || w.Sent || w.Denied || !w.Allowed || w.Peer.m_SteamID==0 || w.Token==0 || w.Report==null || w.Loading || loadActive)return;
            // Missing end-state: retain the local report but never send bulk data into an unknown loading state.
            if(now-w.LastState>12 || now<jlGlobalSend)return;
            P2PSessionState_t state;int queue=SteamNetworking.GetP2PSessionState(w.Peer,out state) ? state.m_nBytesQueuedForSend : -1;
            if(queue>32768){w.Denied=true;JlEvent("report-aborted-Steam-backlog",w.Connection==null ? -1 : w.Connection.connectionId);return;}
            if(!JoinLoadWire.CanDeliver(IsFriend(w.Peer),w.Allowed,loadActive,w.Loading,now-w.LastState,queue))return;
            int n=Math.Min(JoinLoadWire.Chunk,w.Report.Length-w.Offset);if(n<=0){w.Sent=true;return;}
            byte[] piece=new byte[n];Buffer.BlockCopy(w.Report,w.Offset,piece,0,n);
            if(JlSend(w.Peer,JoinLoadWire.Packet(JoinLoadWire.Report,w.Token,(uint)w.Report.Length,w.Offset,piece,n)))
            {w.Offset+=n;jlGlobalSend=now+0.65f;if(w.Offset==w.Report.Length)w.Sent=true;}
        }
        void DrawJoinLoadConsent()
        {
            if(jlPrompt!=null)
                jlConsentRect=GUILayout.Window(227879,jlConsentRect,delegate(int id)
                {
                    GUILayout.Label("Share technical loading diagnostics (no personal data) with "+PingName(jlPrompt.Peer)+"?");
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("Yes, always")){jlConsent.Value="Always";jlPrompt.Allowed=true;jlPrompt=null;}
                    if(GUILayout.Button("This time") && jlPrompt!=null){jlPrompt.Allowed=true;jlPrompt=null;}
                    if(GUILayout.Button("No") && jlPrompt!=null){jlConsent.Value="Never";jlPrompt.Denied=true;JlSend(jlPrompt.Peer,JoinLoadWire.Packet(JoinLoadWire.Declined,jlPrompt.Token,0,0,null,0));jlPrompt=null;}
                    GUILayout.EndHorizontal();GUI.DragWindow();
                },"Loading diagnostics — Steam friend only");
        }
        void DrawJoinLoad()
        {
            GUILayout.Label("Діагностика хоста: "+jlStatus);
            if(jlReceived.Length>0)
            {
                if(GUILayout.Button(jlShowReport ? "Hide host report" : "Show host report"))jlShowReport=!jlShowReport;
                if(jlShowReport){jlReportScroll=GUILayout.BeginScrollView(jlReportScroll,GUILayout.Height(150));GUILayout.TextArea(jlReceived);GUILayout.EndScrollView();}
                if(GUILayout.Button("Add host report to ZIP"))MakeDiagBundle(true);
            }
        }
        void ShutdownJoinLoad()
        {
            foreach(JoinWindow w in jlWindows)JlSave(w,"plugin shutdown");
            if(!jlSaved)JlPersist("joinload-local",jlLocal.Report("plugin shutdown"));
            if(SteamManager.Initialized)SteamFriends.SetRichPresence(JoinLoadPresence,null);
        }
    }
}
