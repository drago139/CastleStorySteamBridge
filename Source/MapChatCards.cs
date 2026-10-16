using System;
using System.Collections.Generic;
using Brix.Assets;
using Brix.UI.Builder.Panel;
using Steamworks;
using UnityEngine;
namespace CastleStorySteam {
 internal sealed class MapChatCard {
  internal MapOffer Offer; internal Asset_Map Installed;
  internal float LookupAt; internal bool Selected;
 }
 public sealed partial class SteamBridgePlugin {
  readonly List<MapChatCard> chatMapCards=new List<MapChatCard>();
  internal IList<MapChatCard> SocialCards {get{return chatMapCards;}}
  internal float SocialChatHeightLimit(RectTransform chat) {
   Canvas canvas=chat.GetComponentInParent<Canvas>();Camera camera=canvas!=null&&canvas.renderMode!=RenderMode.ScreenSpaceOverlay?canvas.worldCamera:null;
   Vector3[] corners=new Vector3[4];chat.GetWorldCorners(corners);float bottom=RectTransformUtility.WorldToScreenPoint(camera,corners[0]).y;
   float limit=float.MaxValue;
   foreach(PlayerRow row in playerRows){ThumbnailPanelComponent panel=row.Panel.Instance;if(panel==null||!panel.gameObject.activeInHierarchy)continue;RectTransform rect=panel.transform as RectTransform;if(rect==null)continue;rect.GetWorldCorners(corners);float playerBottom=RectTransformUtility.WorldToScreenPoint(camera,corners[0]).y;limit=Mathf.Min(limit,playerBottom-bottom-12);}
   return limit==float.MaxValue?limit:Mathf.Max(100,limit/Mathf.Max(.1f,canvas==null?1:canvas.scaleFactor));
  }
  void RememberMapOffer(MapOffer offer,bool selected) {
   MapChatCard card=null;
   foreach(MapChatCard item in chatMapCards) if(SameOffer(item.Offer,offer)){card=item;break;}
   if(card==null){card=new MapChatCard {Offer=offer};chatMapCards.Add(card);if(chatMapCards.Count>8)chatMapCards.RemoveAt(0);}
   if(selected){foreach(MapChatCard item in chatMapCards)item.Selected=false;card.Selected=true;}
   if(SameOffer(offer,pendingMap)&&pendingInstalled!=null)card.Installed=pendingInstalled;
  }
  Asset_Map CardInstalled(MapChatCard card) {
   if(card.Installed!=null)return card.Installed;
   if(downloadedMap!=null&&SameOffer(card.Offer,downloadOffer)){card.Installed=downloadedMap;return card.Installed;}
   if(Time.realtimeSinceStartup<card.LookupAt)return null;card.LookupAt=Time.realtimeSinceStartup+2;
   ReadProposalMaps(false);
   foreach(Asset_Map asset in proposalMaps)if(card.Offer.Workshop!=0?MapWorkshopId(asset)==card.Offer.Workshop:asset.codename==card.Offer.Code){card.Installed=asset;return asset;}
   return null;
  }
  internal string CardName(MapChatCard card) {return card.Offer.Name+(card.Selected?" • вибрана хостом":"");}
  internal string CardStatus(MapChatCard card) {
   if(SameOffer(card.Offer,pendingMap)&&(socialDownloadActive||mapQueryActive))return mapFeedback;
   if(CardInstalled(card)!=null)return card.Offer.Workshop!=0?"Workshop • карта встановлена":"Карта встановлена";
   return card.Offer.Workshop!=0?"Workshop • карти немає на цьому ПК":"Локальної карти немає на цьому ПК";
  }
  internal string CardAction(MapChatCard card) {
   if(SameOffer(card.Offer,pendingMap)&&socialDownloadActive)return "Завантаження…";
   if(SameOffer(card.Offer,pendingMap)&&mapQueryActive)return "Перевірка…";
   if(CardInstalled(card)==null)return card.Offer.Workshop==0?"Карта відсутня":(verifiedWorkshop==card.Offer.Workshop?"▼ Підписатися й скачати":"▼ Скачати");
   return hosting?(card.Selected?"Вибрана":"Вибрати карту"):"Встановлена";
  }
  internal bool CardCanAct(MapChatCard card) {return InProposalLobby()&&!socialDownloadActive&&!mapQueryActive&&(CardInstalled(card)==null?card.Offer.Workshop!=0:hosting&&!card.Selected);}
  internal bool CardNeedsDownload(MapChatCard card){return card.Offer.Workshop!=0&&CardInstalled(card)==null;}
  internal void CardAct(MapChatCard card) {
   if(!CardCanAct(card))return;
   pendingMap=card.Offer;pendingInstalled=CardInstalled(card);mapSelected=card.Selected;confirmProposal=false;nextMapLookup=0;
   ConfirmMapAction();
  }
 }
}
