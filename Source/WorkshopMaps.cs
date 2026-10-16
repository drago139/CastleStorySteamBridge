using System;
using System.IO;
using System.Text.RegularExpressions;
using Brix.Assets;
using Brix.Game.Network;
using Network;
using Brix.Steam;
using Steamworks;
using UnityEngine;
namespace CastleStorySteam {
 public sealed partial class SteamBridgePlugin {
  bool socialDownloadActive;
  bool socialSubscriptionConfirmed, socialDownloadAccepted;
  bool mapPlayerImportStarted;
  volatile bool mapPlayerImportDone;
  volatile string mapPlayerImportError;
  string mapPlayerImportPath;
  ulong verifiedWorkshop;
  MapOffer downloadOffer;
  Asset_Map downloadedMap;
  float nextDownloadPoll,downloadStarted,queryStarted;
  int downloadSession;
  CallResult<SteamUGCQueryCompleted_t> mapQueryResult;
  CallResult<RemoteStorageSubscribePublishedFileResult_t> mapSubscribeResult;
  Callback<DownloadItemResult_t> mapDownloadResult;
  UGCQueryHandle_t mapQueryHandle;
  bool mapQueryActive;
  static bool TryWorkshopId(string query,out ulong id) {
   id=0; if(UInt64.TryParse(query,out id)) return id>0;
   Uri url;if(!Uri.TryCreate(query,UriKind.Absolute,out url)||url.Host!="steamcommunity.com"||url.AbsolutePath!="/sharedfiles/filedetails/") return false;
   Match m=Regex.Match(url.Query,@"(?:\?|&)id=(\d+)(?:&|$)");return m.Success&&UInt64.TryParse(m.Groups[1].Value,out id)&&id>0;
  }
  void QuerySocialWorkshop(ulong id,bool forProposal) {
   if(mapQueryActive||socialDownloadActive||!InProposalLobby()) return;
   int session=socialSession;mapFeedback="Перевіряю карту Workshop…";queryStarted=Time.realtimeSinceStartup;mapQueryActive=true;
   mapQueryHandle=SteamUGC.CreateQueryUGCDetailsRequest(new PublishedFileId_t[]{new PublishedFileId_t(id)},1);
   SteamUGC.SetReturnMetadata(mapQueryHandle,true);
   mapQueryResult=CallResult<SteamUGCQueryCompleted_t>.Create(delegate(SteamUGCQueryCompleted_t result,bool io) {
    try {
     if(session!=socialSession||!InProposalLobby()) return;
     SteamUGCDetails_t details;string metadata;
     if(io||result.m_eResult!=EResult.k_EResultOK||result.m_unNumResultsReturned!=1||!SteamUGC.GetQueryUGCResult(result.m_handle,0,out details)||details.m_eResult!=EResult.k_EResultOK||details.m_nConsumerAppID.m_AppId!=227860||details.m_nPublishedFileId.m_PublishedFileId!=id||details.m_bBanned||!SteamUGC.GetQueryUGCMetadata(result.m_handle,0,out metadata,1024)||!Regex.IsMatch(metadata??"",@"""ItemType""\s*:\s*1\s*[,}]")) {
      mapFeedback="Steam не підтвердив доступну карту Castle Story.";ChatLine(mapFeedback);return;
     }
     verifiedWorkshop=id;
     if(forProposal) {pendingMap=new MapOffer {Author=SteamUser.GetSteamID().m_SteamID,Workshop=id,Code="",Name=ProposalProtocol.Clean(details.m_rgchTitle),OfferId=NewOfferId()};pendingInstalled=null;confirmProposal=true;mapFeedback="Запропонувати «"+pendingMap.Name+"»?";}
     else if(pendingMap!=null&&pendingMap.Workshop==id) {pendingMap.Name=ProposalProtocol.Clean(details.m_rgchTitle);mapFeedback="Workshop: «"+pendingMap.Name+"». Підписатися й завантажити?";}
     ChatLine(mapFeedback);
    } catch(Exception e){mapFeedback="Перевірка Workshop: "+e.Message;Logger.LogWarning(mapFeedback);}
    finally {SteamUGC.ReleaseQueryUGCRequest(result.m_handle);mapQueryActive=false;}
   });
   mapQueryResult.Set(SteamUGC.SendQueryUGCRequest(mapQueryHandle));
  }
  void BeginSocialDownload(MapOffer offer) {
   if(verifiedWorkshop!=offer.Workshop) {QuerySocialWorkshop(offer.Workshop,false);return;}
   downloadOffer=offer;downloadedMap=null;downloadSession=socialSession;socialDownloadActive=true;socialSubscriptionConfirmed=false;socialDownloadAccepted=false;mapPlayerImportStarted=false;mapPlayerImportDone=false;mapPlayerImportError=null;downloadStarted=Time.realtimeSinceStartup;
   if(mapSubscribeResult!=null) mapSubscribeResult.Cancel();
   mapSubscribeResult=CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create(delegate(RemoteStorageSubscribePublishedFileResult_t result,bool io) {
    if(downloadSession!=socialSession||!socialDownloadActive||downloadOffer==null||result.m_nPublishedFileId.m_PublishedFileId!=downloadOffer.Workshop) return;
    if(io||result.m_eResult!=EResult.k_EResultOK) {socialDownloadActive=false;mapFeedback="Підписка не вдалася: "+result.m_eResult;ChatLine(mapFeedback);return;}
    socialSubscriptionConfirmed=true;Logger.LogInfo("Map subscription confirmed by Steam: "+downloadOffer.Workshop);
    if(mapDownloadResult==null) mapDownloadResult=Callback<DownloadItemResult_t>.Create(delegate(DownloadItemResult_t r) {
     if(!socialDownloadActive||downloadOffer==null||r.m_unAppID.m_AppId!=227860||r.m_nPublishedFileId.m_PublishedFileId!=downloadOffer.Workshop) return;
     if(r.m_eResult!=EResult.k_EResultOK){socialDownloadActive=false;mapFeedback="Завантаження Steam: "+r.m_eResult;ChatLine(mapFeedback);}
    });
    if(!SteamUGC.DownloadItem(result.m_nPublishedFileId,true)) {socialDownloadActive=false;mapFeedback="Steam не прийняв завантаження. Спробуй ще раз.";ChatLine(mapFeedback);return;}
    socialDownloadAccepted=true;Logger.LogInfo("Map download accepted by Steam: "+downloadOffer.Workshop);
    WorkshopItem.RecoverItem(result.m_nPublishedFileId);mapFeedback="Підписку підтверджено. Завантажую «"+downloadOffer.Name+"»…";ChatLine(mapFeedback);
   });
   mapSubscribeResult.Set(SteamUGC.SubscribeItem(new PublishedFileId_t(offer.Workshop)));
   mapFeedback="Очікую підтвердження підписки Steam…";
  }
  void SocialDownloadTick() {
   OfferReadyTick();
   if(mapQueryActive&&Time.realtimeSinceStartup-queryStarted>30) {if(mapQueryResult!=null)mapQueryResult.Cancel();SteamUGC.ReleaseQueryUGCRequest(mapQueryHandle);mapQueryActive=false;mapFeedback="Steam не відповів на перевірку карти. Спробуй знову.";}
   if(!socialDownloadActive||downloadOffer==null||Time.realtimeSinceStartup<nextDownloadPoll) return;
   nextDownloadPoll=Time.realtimeSinceStartup+1;
   if(downloadSession!=socialSession||!InProposalLobby()){CancelSocialDownload();return;}
   if(!socialSubscriptionConfirmed||!socialDownloadAccepted){if(Time.realtimeSinceStartup-downloadStarted>60){socialDownloadActive=false;mapFeedback="Steam не підтвердив підписку або завантаження. Спробуй ще раз.";ChatLine(mapFeedback);}return;}
   if(Time.realtimeSinceStartup-downloadStarted>600){socialDownloadActive=false;mapFeedback="Завантаження не завершилось за 10 хв. Перевір завантаження Steam.";ChatLine(mapFeedback);return;}
   PublishedFileId_t id=new PublishedFileId_t(downloadOffer.Workshop); EItemState state=(EItemState)SteamUGC.GetItemState(id);
   ulong received,total;
   if(SteamUGC.GetItemDownloadInfo(id,out received,out total)&&total>0) mapFeedback="Завантажую «"+downloadOffer.Name+"»: "+(100.0*received/total).ToString("F0")+" %";
   if((state&EItemState.k_EItemStateInstalled)==0||(state&(EItemState.k_EItemStateNeedsUpdate|EItemState.k_EItemStateDownloading|EItemState.k_EItemStateDownloadPending))!=0) return;
   ulong size;uint stamp;string folder;
   if(!SteamUGC.GetItemInstallInfo(id,out size,out folder,2048,out stamp)||String.IsNullOrEmpty(folder)||!File.Exists(Path.Combine(folder,"meta.json"))) return;
   try {
    Asset_Map map=new Asset_Map(folder.Replace('\\','/')+"/");map.authorType=AssetAuthorType.Workshop;
    if(!map.IsCurrentVersion()||(map.playableContext&AssetPlayableContext.Multiplayer)==0){socialDownloadActive=false;mapFeedback="Карту завантажено, але вона несумісна з мережевою грою.";ChatLine(mapFeedback);return;}
    if(!mapPlayerImportStarted){
     mapPlayerImportStarted=true;mapFeedback="Додаю «"+downloadOffer.Name+"» у карти гравця…";
     string importSource=folder,playerMaps=AssetDispensary.maps.ugcPath;ulong importId=downloadOffer.Workshop;int importSession=downloadSession;
     System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused){try{string path=WorkshopMapImport.Import(importSource,playerMaps,importId);if(importSession==socialSession)mapPlayerImportPath=path;}catch(Exception e){if(importSession==socialSession)mapPlayerImportError=e.Message;}finally{if(importSession==socialSession)mapPlayerImportDone=true;}});return;
    }
    if(!mapPlayerImportDone)return;
    if(mapPlayerImportError!=null){socialDownloadActive=false;mapFeedback="Карту збережено у Workshop; додавання у Player не вдалося: "+mapPlayerImportError;ChatLine(mapFeedback);return;}
    map=new Asset_Map(mapPlayerImportPath.Replace('\\','/')+"/");map.authorType=AssetAuthorType.UGC;
    Logger.LogInfo("Map installation verified: "+downloadOffer.Workshop+"; state="+state+"; bytes="+size+"; folder="+folder);
    WorkshopItem item=WorkshopItem.RecoverItem(id);if(item!=null){item.itemType=WorkshopItemType.Map;item.contentPath=folder;item.UpdateState();}
    ReadProposalMaps(true);
    downloadedMap=map;pendingInstalled=map;pendingMap=downloadOffer;confirmProposal=false;socialDownloadActive=false;AnnounceMapReady();
    mapFeedback="Завантажено «"+downloadOffer.Name+"». Доступна в картах гравця. "+(hosting?"Підтверди вибір карти.":"Карту може вибрати хост.");ChatLine(mapFeedback);
   }catch(Exception e){socialDownloadActive=false;mapFeedback="Карта не відкрилась: "+e.Message;ChatLine(mapFeedback);}
  }
  void CancelSocialDownload() {
   socialDownloadActive=false;socialSubscriptionConfirmed=false;socialDownloadAccepted=false;downloadOffer=null;downloadedMap=null;
   if(mapSubscribeResult!=null){mapSubscribeResult.Cancel();mapSubscribeResult=null;}
   if(mapQueryResult!=null){mapQueryResult.Cancel();mapQueryResult=null;}
   if(mapQueryActive&&SteamManager.Initialized) SteamUGC.ReleaseQueryUGCRequest(mapQueryHandle);mapQueryActive=false;
  }
 }
}
