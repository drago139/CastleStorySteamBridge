using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using Brix.Network;
using Network;
using Brix.Engine;
using Brix.Assets;
using Brix.Game.Network;
using Brix.UI.Builder.Panel;
using Brix.UI.Builder.Menu;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
namespace CastleStorySteam {
 public sealed partial class SteamBridgePlugin {
  sealed class PlayerRow { internal ThumbnailPanelComponent.BasePanel Panel; internal int Team, Index; }
  readonly List<PlayerRow> playerRows=new List<PlayerRow>();
  readonly List<Asset_Map> proposalMaps=new List<Asset_Map>();
  readonly List<Asset_Map> pickerMaps=new List<Asset_Map>();
  readonly PeerReportGate proposalGate=new PeerReportGate();
  MapOffer pendingMap;
  bool confirmProposal;
  float nextProposalAt;
  string mapFeedback="Карта: /map назва або посилання Workshop";
  int socialSession;
  uint nextOfferId=(uint)Environment.TickCount;
  readonly HashSet<CSteamID> mapReadyPeers=new HashSet<CSteamID>();
  readonly HashSet<CSteamID> mapNotifiedPeers=new HashSet<CSteamID>();
  Asset_Map pendingInstalled;
  float nextOfferPoll;
  float nextMapLookup;
  bool mapSelected;
  float nextChatAttach;
  bool chatScanLogged,chatAttached;
  int chatScanSlow;
  void InitLobbyPing() {
   PatchSocial(typeof(UNetManager),"UpdateLobbyPlayersInfos","AfterLobbyInfo",false);
   PatchSocial(typeof(NetworkPlayersMenu),"GetPlayerPanel","BindPlayerRow",false);
   PatchSocial(typeof(ChatPanelComponent),"Build","WrapChatBuild",true);
   PatchSocial(typeof(ChatBoxDriver),"Initialize","AfterGameChat",false);
   PatchSocial(typeof(NetworkChatMenu),"SendChatMessage","ChatCommand",true);
   Logger.LogInfo("Lobby numeric ping and native chat map controls ready.");
  }
  void PatchSocial(Type type,string method,string patch,bool prefix) {
   MethodInfo target=type.GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance);
   if(target==null) throw new MissingMethodException(type.Name,method);
   HarmonyMethod hook=new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod(patch,BindingFlags.NonPublic|BindingFlags.Static));
   discoveryHarmony.Patch(target,prefix?hook:null,prefix?null:hook);
  }
  static void BindPlayerRow(int __0,int __1,IPanel __result) {
   SteamBridgePlugin p=discoveryInstance; ThumbnailPanelComponent.BasePanel panel=__result as ThumbnailPanelComponent.BasePanel;
   if(p==null||panel==null) return;
   if(p.playerRows.Count>=64) p.playerRows.Clear();
   p.playerRows.Add(new PlayerRow {Panel=panel,Team=__0,Index=__1});
  }
  static void AfterLobbyInfo(UNetManager __instance) {
   SteamBridgePlugin p=discoveryInstance; if(p==null||!p.hosting||__instance.LobbyPlayerList==null) return;
   foreach(LobbyPlayer player in __instance.LobbyPlayerList) {
    if(player==null||player.isHost||!p.guests.ContainsKey(player.SteamID)) continue;
    PingPeer ping; player.Networkping=p.pings.TryGetValue(player.SteamID,out ping)&&p.ReliablePingFresh(player.SteamID)?(int)Math.Round(ping.Reliable.Last):-1;
   }
  }
  bool ReliablePingFresh(CSteamID id) {
   PingPeer p; return pings.TryGetValue(id,out p)&&p.Reliable.Received>0&&Time.realtimeSinceStartup-p.Reliable.LastReplyAt<=6;
  }
  string CompactPing(CSteamID id) { PingPeer p; if(!pings.TryGetValue(id,out p)||p.Reliable.Received==0) return "вимірювання…"; return ReliablePingFresh(id)?p.Reliable.Last.ToString("F0")+" мс":"немає відповіді"; }
  void LateUpdate() {
   SocialDownloadTick();
   if(Time.realtimeSinceStartup>=nextChatAttach&&InProposalLobby()){long scanStart=System.Diagnostics.Stopwatch.GetTimestamp();nextChatAttach=Time.realtimeSinceStartup+(chatAttached?30:20);ChatPanelComponent[] chats=UnityEngine.Object.FindObjectsOfType<ChatPanelComponent>();if(!chatScanLogged){chatScanLogged=true;Logger.LogInfo("Native lobby chat panels: "+chats.Length);}foreach(ChatPanelComponent chat in chats) AttachChatControls(chat);chatAttached=chats.Length>0;double scanMs=(System.Diagnostics.Stopwatch.GetTimestamp()-scanStart)*1000.0/System.Diagnostics.Stopwatch.Frequency;if(scanMs>=30&&chatScanSlow++<20)Logger.LogInfo("Chat panel scan took "+scanMs.ToString("F0")+" ms");}
   if(!InProposalLobby()) return;
   foreach(PlayerRow row in playerRows) {
    ThumbnailPanelComponent panel=row.Panel.Instance; if(panel==null||!panel.gameObject.activeInHierarchy||panel._playerNameText==null) continue;
    int index=0; foreach(LobbyPlayer player in UNetManager.instance.LobbyPlayerList) {
     if(player==null||player.TeamNumber!=row.Team) continue;
     if(index++!=row.Index) continue;
     string ping=player.isHost?(hosting?"0 мс • хост":CompactPing(peer)):(hosting?CompactPing(player.SteamID):(player.ping<=0?"вимірювання…":player.ping+" мс до хоста"));
     panel._playerNameText.supportRichText=false; panel._playerNameText.text=player.playerName+"  • "+ping; break;
    }
   }
  }
  bool InProposalLobby() { return SteamManager.Initialized&&!gameStarting&&!loadActive&&UNetManager.instance!=null&&UNetManager.instance.LocalLobbyPlayer!=null&&(NetworkServer.active||NetworkClient.active); }
  void ResetProposals() { socialSession++; proposalGate.Clear();chatMapCards.Clear();pendingMap=null; pendingInstalled=null;mapSelected=false;nextMapLookup=0;mapReadyPeers.Clear();mapNotifiedPeers.Clear();confirmProposal=false; nextProposalAt=0; mapFeedback="Вибери карту зі списку «Карти ▾»."; CancelSocialDownload(); }
  static void WrapChatBuild(ref Action<ChatPanelComponent> onReset,ref Action<ChatPanelComponent> onRefresh) {
   Action<ChatPanelComponent> reset=onReset,refresh=onRefresh;
   onReset=delegate(ChatPanelComponent panel) { if(reset!=null) reset(panel); AttachChatControls(panel); };
   onRefresh=delegate(ChatPanelComponent panel) { if(refresh!=null) refresh(panel); AttachChatControls(panel); };
  }
  static void AttachChatControls(ChatPanelComponent panel) {
   if(panel==null||discoveryInstance==null) return;
   ChatMapControls controls=panel.GetComponent<ChatMapControls>();
   if(controls==null) {controls=panel.gameObject.AddComponent<ChatMapControls>();controls.Initialize(panel,discoveryInstance);discoveryInstance.Logger.LogInfo("Native chat map buttons attached.");}
  }
  void ChatLine(string text) { GameSignals.Invoke(UNetManager.LobbyNewChatMessage,"<color=#FFC1C5>[Карти] "+text+"</color>"); Logger.LogInfo("Chat map: "+text); }
  static bool ChatCommand(string __0) {
   SteamBridgePlugin p=discoveryInstance; if(p==null||!p.InProposalLobby()||String.IsNullOrEmpty(__0)) return true;
   string s=__0.Trim();
   if(s.Equals("/maps",StringComparison.OrdinalIgnoreCase)||s.Equals("/карти",StringComparison.OrdinalIgnoreCase)) { p.ListMapsInChat(); return false; }
   if(s.StartsWith("/map ",StringComparison.OrdinalIgnoreCase)||s.StartsWith("/карта ",StringComparison.OrdinalIgnoreCase)) { p.FindMapCommand(s.Substring(s.IndexOf(' ')+1).Trim()); return false; }
   return true;
  }
  void ReadProposalMaps(bool refresh) {
   proposalMaps.Clear();HashSet<string> keys=new HashSet<string>();foreach(Asset_Map asset in AssetDispensary.maps.SendQuery(AssetQueryParameters.AllSources|(refresh?AssetQueryParameters.RefreshCache:0),AssetPlayableContext.Multiplayer))if(asset!=null&&asset.IsCurrentVersion()){string key=asset.workshopID!=0?"w:"+asset.workshopID:"c:"+asset.codename;if(keys.Add(key))proposalMaps.Add(asset);}
   proposalMaps.Sort(delegate(Asset_Map a,Asset_Map b){return StringComparer.OrdinalIgnoreCase.Compare(MapName(a),MapName(b));});
  }
  static string MapName(Asset_Map map) {return ProposalProtocol.Clean(String.IsNullOrEmpty(map.customName)?map.displayName:map.customName);}
  static ulong MapWorkshopId(Asset_Map map) {
   if(map.workshopID!=0)return map.workshopID;
   if(map.authorType!=AssetAuthorType.Workshop||String.IsNullOrEmpty(map.path))return 0;
   ulong id;if(!UInt64.TryParse(Path.GetFileName(map.path.TrimEnd('/','\\')),out id))return 0;
   ulong size;uint stamp;string folder;
   if(!SteamUGC.GetItemInstallInfo(new PublishedFileId_t(id),out size,out folder,2048,out stamp))return 0;
   return String.Equals(Path.GetFullPath(folder).TrimEnd('/','\\'),Path.GetFullPath(map.path).TrimEnd('/','\\'),StringComparison.OrdinalIgnoreCase)?id:0;
  }
  uint NewOfferId() {nextOfferId++;if(nextOfferId==0)nextOfferId++;return nextOfferId;}
  internal string[] SocialMapChoices() {
   ReadProposalMaps(false);pickerMaps.Clear();pickerMaps.AddRange(proposalMaps);List<string> names=new List<string>();foreach(Asset_Map map in pickerMaps) names.Add(MapName(map)+(MapWorkshopId(map)!=0?" • Workshop":""));return names.ToArray();
  }
  internal void SocialChoose(int index) {
   if(!InProposalLobby()||socialDownloadActive||mapQueryActive||index<0||index>=pickerMaps.Count) return;
   Asset_Map map=pickerMaps[index];pendingMap=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=MapWorkshopId(map),Code=map.codename,Name=MapName(map),OfferId=NewOfferId()};mapSelected=false;nextMapLookup=0;
   pendingInstalled=map;confirmProposal=true;ConfirmMapAction();
  }
  bool SameOffer(MapOffer a,MapOffer b) {return ProposalProtocol.SameOffer(a,b);}
  void AnnounceMapReady() {
   if(pendingMap==null||FindInstalledOffer(pendingMap)==null) return;
   if(hosting) {mapReadyPeers.Add(SteamUser.GetSteamID());return;}
   MapOffer ready=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=pendingMap.Workshop,Code=pendingMap.Code,Name=pendingMap.Name,OfferId=pendingMap.OfferId,Action=3};SendOffer(ready);
  }
  bool AllMapPeersReady() {foreach(CSteamID id in DiagPeers()) if(!mapReadyPeers.Contains(id)) return false;return true;}
  void OfferReadyTick() {
   if(pendingMap==null||!hosting||confirmProposal||!InProposalLobby()||Time.realtimeSinceStartup<nextOfferPoll) return;
   nextOfferPoll=Time.realtimeSinceStartup+2;
   foreach(CSteamID id in DiagPeers()) if(!mapNotifiedPeers.Contains(id)&&PeerSupportsProposals(id)) {byte[] data=ProposalProtocol.Encode(pendingMap);SendTo(id,BridgeCore.Packet(BridgeCore.KindMapChoice,data,data.Length));mapNotifiedPeers.Add(id);}
  }
  void ListMapsInChat() {
   if(!InProposalLobby()) return; ReadProposalMaps(false);
   List<string> names=new List<string>(); foreach(Asset_Map asset in proposalMaps) names.Add(MapName(asset));
   ChatLine("Доступні карти: "+String.Join(", ",names.ToArray()));
   ChatLine("Напиши /map назва або /map посилання Workshop. Потім підтвердь кнопкою в чаті.");
  }
  void FindMapCommand(string query) {
   if(!InProposalLobby()||socialDownloadActive) return;
   ulong workshop; if(TryWorkshopId(query,out workshop)) { QuerySocialWorkshop(workshop,true); return; }
   ReadProposalMaps(false); List<Asset_Map> found=new List<Asset_Map>();
   foreach(Asset_Map asset in proposalMaps) {string name=MapName(asset); if(name.Equals(query,StringComparison.OrdinalIgnoreCase)) {found.Clear();found.Add(asset);break;} if(name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) found.Add(asset);}
   if(found.Count!=1) {ChatLine(found.Count==0?"Карту не знайдено. /maps покаже список.":"Є кілька збігів. Введи повну назву карти.");return;}
   Asset_Map map=found[0]; pendingMap=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=MapWorkshopId(map),Code=map.codename,Name=MapName(map),OfferId=NewOfferId()}; pendingInstalled=map;confirmProposal=true;
   mapFeedback="Запропонувати «"+pendingMap.Name+"»?";
  }
  bool PeerSupportsProposals(CSteamID id) {string v; return peerVersions.TryGetValue(id,out v)&&UpdateVerifier.IsNewer(v,"0.3.14");}
  void SendOffer(MapOffer offer) { byte[] payload=ProposalProtocol.Encode(offer); foreach(CSteamID id in DiagPeers()) if(PeerSupportsProposals(id)) SendTo(id,BridgeCore.Packet(BridgeCore.KindMapChoice,payload,payload.Length)); }
  void ConfirmMapAction() {
   if(!InProposalLobby()||pendingMap==null||socialDownloadActive) return;
   if(confirmProposal) {
    if(Time.realtimeSinceStartup<nextProposalAt) {mapFeedback="Зачекай 5 секунд між пропозиціями."; return;}
    bool hasPeers=DiagPeers().Count>0;
    bool supported=false;foreach(CSteamID id in DiagPeers())if(PeerSupportsProposals(id))supported=true;
    if(hasPeers&&!supported){mapFeedback="Дочекайся підтвердження версії друга (0.3.15+).";return;}
    mapReadyPeers.Clear();mapNotifiedPeers.Clear();pendingMap.Author=SteamUser.GetSteamID().m_SteamID;pendingMap.Action=0;SendOffer(pendingMap);RememberMapOffer(pendingMap,false);nextProposalAt=Time.realtimeSinceStartup+5;confirmProposal=false;AnnounceMapReady();
    ChatLine(ProposalProtocol.Clean(PingName(new CSteamID(pendingMap.Author)))+" хоче грати на «"+pendingMap.Name+"».");
    if(pendingMap.Workshop!=0)ChatLine("Підписка Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id="+pendingMap.Workshop);
    mapFeedback=hosting?"Обрати «"+pendingMap.Name+"»?":"Очікуємо рішення хоста: «"+pendingMap.Name+"»."; return;
   }
   Asset_Map installed=FindInstalledOffer(pendingMap);
   if(installed==null) { if(pendingMap.Workshop!=0) BeginSocialDownload(pendingMap); else mapFeedback="Цієї локальної карти немає. Потрібне посилання Workshop або файл від автора."; return; }
   if(!hosting) {MapOffer vote=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=pendingMap.Workshop,Code=pendingMap.Code,Name=pendingMap.Name,OfferId=pendingMap.OfferId,Action=1};SendOffer(vote);AnnounceMapReady();ChatLine("Ти підтвердив карту «"+vote.Name+"». Остаточний вибір — у хоста.");return;}
   SelectConfirmedMap(installed);
  }
  Asset_Map FindInstalledOffer(MapOffer offer) {
   if(pendingInstalled!=null&&SameOffer(offer,pendingMap)) return pendingInstalled;
   if(downloadedMap!=null&&SameOffer(offer,downloadOffer)) return downloadedMap;
   if(Time.realtimeSinceStartup<nextMapLookup) return null;nextMapLookup=Time.realtimeSinceStartup+2;
   ReadProposalMaps(false); foreach(Asset_Map asset in proposalMaps) if(offer.Workshop!=0?MapWorkshopId(asset)==offer.Workshop:asset.codename==offer.Code) {pendingInstalled=asset;return asset;}return null;
  }
  void SelectConfirmedMap(Asset_Map map) {
   if(!InProposalLobby()||!hosting||UNetManager.instance.LocalLobbyPlayer==null||!UNetManager.instance.LocalLobbyPlayer.isHost) return;
   Brix.UI.Builder.Menu.LobbyMenu.SetIsReady(false);
   BaseMapLibraryMenu.SetSelectedMap(map,AssetPlayableContext.Multiplayer,BaseAssetMenu.MenuGameContext.Normal,BaseAssetMenu.MenuTypeContext.New);
   BaseMapLibraryMenu.SetSelectedAsLoaded();
   LobbyMenu.CloseMapLibrary(); LobbyMenu.RefreshSignal.Invoke();
   MapOffer selected=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=pendingMap.Workshop,Code=pendingMap.Code,Name=pendingMap.Name,OfferId=pendingMap.OfferId,Action=2};SendOffer(selected);ChatLine("Хост підтвердив «"+selected.Name+"». Карта вибрана в лобі; натисніть «Готовий». ");pendingMap=selected;mapSelected=true;RememberMapOffer(selected,true);mapFeedback="Вибрано «"+selected.Name+"».";
  }
  void DeclineMapAction() { if(socialDownloadActive) {CancelSocialDownload();ChatLine("Очікування завантаження скасовано. Підписка Steam зберігається.");} pendingMap=null;confirmProposal=false;mapFeedback="Карта: /map назва або посилання Workshop"; }
  void OnMapProposal(CSteamID from,byte[] payload) {
   if(!InProposalLobby()) return; MapOffer offer; if(!ProposalProtocol.Decode(payload,out offer)) return;
   if(hosting) {BridgeCore sender;if(!guests.TryGetValue(from,out sender)||!sender.Ready||offer.Author!=from.m_SteamID||offer.Action==2) return;}
   else if(bridge==null||!bridge.Ready||from!=peer) return;
   if(!proposalGate.Accept(from.m_SteamID,offer.Action,Time.realtimeSinceStartup,5)) return;
   string name=ProposalProtocol.Clean(PingName(new CSteamID(offer.Author)));
   if(offer.Action==0||offer.Action==2)RememberMapOffer(offer,offer.Action==2);
   if(offer.Action==0) {if(SameOffer(pendingMap,offer)){AnnounceMapReady();return;}ChatLine(name+" хоче грати на «"+offer.Name+"».");if(offer.Workshop!=0)ChatLine("Підписка Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id="+offer.Workshop);if(!socialDownloadActive){pendingMap=offer;pendingInstalled=null;mapSelected=false;nextMapLookup=0;mapReadyPeers.Clear();mapNotifiedPeers.Clear();confirmProposal=false;mapFeedback=hosting?"Обрати «"+offer.Name+"»?":"Завантажити й підтвердити «"+offer.Name+"»?";AnnounceMapReady();} }
   else if(offer.Action==1) ChatLine(name+" підтвердив карту «"+offer.Name+"».");
   else if(offer.Action==3) {if(!SameOffer(pendingMap,offer)) return;mapReadyPeers.Add(new CSteamID(offer.Author));ChatLine(name+" має завантажену карту «"+offer.Name+"».");}
   else {ChatLine("Хост вибрав «"+offer.Name+"». Готовність треба підтвердити знову.");mapSelected=true;if(!socialDownloadActive){bool same=SameOffer(pendingMap,offer);pendingMap=offer;if(!same)pendingInstalled=null;nextMapLookup=0;mapFeedback="Вибрано «"+offer.Name+"». Якщо карти немає — натисни червону стрілку.";}}
   if(hosting) {byte[] data=ProposalProtocol.Encode(offer);foreach(CSteamID id in DiagPeers()) if(id!=from&&PeerSupportsProposals(id)) SendTo(id,BridgeCore.Packet(BridgeCore.KindMapChoice,data,data.Length));}
  }
  void DrawSocialOverlay() { }
  internal string SocialStatus {get{return mapFeedback;}}
  internal string SocialButton {get {if(pendingMap==null) return "";if(confirmProposal) return "Так, запропонувати";if(socialDownloadActive) return "Завантаження…";if(mapQueryActive)return "Перевірка…";if(FindInstalledOffer(pendingMap)==null) return pendingMap.Workshop==0?"Карта відсутня":(verifiedWorkshop==pendingMap.Workshop?"▼ Підписатися й скачати":"▼ Скачати з Workshop");return mapSelected?"":(hosting?"Вибрати карту":"Так, хочу цю карту");}}
  internal bool SocialCanConfirm {get{return pendingMap!=null&&!socialDownloadActive&&!mapQueryActive&&InProposalLobby();}}
  internal bool SocialNeedsDownload {get{return pendingMap!=null&&pendingMap.Workshop!=0&&FindInstalledOffer(pendingMap)==null;}}
  internal bool SocialInLobby {get{return InProposalLobby();}}
  internal bool SocialIsProposalConfirmation {get{return confirmProposal;}}
  internal bool SocialBusy {get{return socialDownloadActive||mapQueryActive;}}
  internal void SocialLayoutLog(string text){Logger.LogInfo("Lobby chat layout: "+text);}
  internal void SocialConfirm() {ConfirmMapAction();}
  internal void SocialCancel() {DeclineMapAction();}
  internal void SocialList() {ListMapsInChat();}
 }
}
