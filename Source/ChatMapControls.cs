using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Brix.UI.Builder.Panel;
namespace CastleStorySteam {
 public sealed class ChatMapControls : MonoBehaviour {
  sealed class CardView {internal MapChatCard Card;internal RectTransform Root;internal Text Name,Status,Action;internal Button Button;}
  SteamBridgePlugin plugin;ChatPanelComponent panel;
  RectTransform root,toolbar,output,content,textRect;
  Vector2 oldRootMax,oldOutputMin;ContentSizeFitter fitter;ContentSizeFitter.FitMode oldFit;
  Text hint,proposalLabel;Button proposalButton;GameObject picker;
  readonly List<CardView> views=new List<CardView>();
  bool expanded;float nextLayoutLog;float lastContentHeight;string lastText="";
  public void Initialize(ChatPanelComponent target,SteamBridgePlugin owner) {
   panel=target;plugin=owner;root=panel.transform as RectTransform;oldRootMax=root.offsetMax;
   output=panel._outputScrollRect.transform as RectTransform;oldOutputMin=output.offsetMin;
   content=panel._outputScrollRect.content;textRect=panel._outputText.rectTransform;
   fitter=content.GetComponent<ContentSizeFitter>();if(fitter!=null)oldFit=fitter.verticalFit;
   toolbar=Rect("MapToolbar",root);toolbar.gameObject.AddComponent<LayoutElement>().ignoreLayout=true;
   toolbar.anchorMin=new Vector2(output.anchorMin.x,0);toolbar.anchorMax=new Vector2(output.anchorMax.x,0);toolbar.offsetMin=new Vector2(oldOutputMin.x,44);toolbar.offsetMax=new Vector2(output.offsetMax.x,78);
   toolbar.gameObject.AddComponent<Image>().color=new Color(.12f,.12f,.12f,.98f);
   Text mapLabel;Button maps=Button("Карти ▾",toolbar,TogglePicker,out mapLabel);Place(maps.transform as RectTransform,0,.22f,0,32);
   hint=Label("MapHint",toolbar);Place(hint.rectTransform,.23f,1,0,32);hint.text="Обери карту, щоб запропонувати її в чаті";hint.fontSize=18;hint.alignment=TextAnchor.MiddleLeft;
   proposalButton=Button("Запропонувати",toolbar,delegate{plugin.SocialConfirm();},out proposalLabel);Place(proposalButton.transform as RectTransform,.23f,1,0,32);
   panel._outputText.alignment=TextAnchor.UpperLeft;
  }
  RectTransform Rect(string name,Transform parent) {
   GameObject go=new GameObject(name,typeof(RectTransform));RectTransform r=go.GetComponent<RectTransform>();r.SetParent(parent,false);r.localScale=Vector3.one;return r;
  }
  void Place(RectTransform r,float left,float right,float bottom,float top) {r.anchorMin=new Vector2(left,0);r.anchorMax=new Vector2(right,0);r.offsetMin=new Vector2(2,bottom);r.offsetMax=new Vector2(-2,top);}
  Text Label(string name,Transform parent) {
   RectTransform rect=Rect(name,parent);Text text=rect.gameObject.AddComponent<Text>();rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(8,2);rect.offsetMax=new Vector2(-8,-2);
   text.font=panel._outputText.font;text.fontSize=18;text.color=new Color(1,.86f,.66f);text.supportRichText=false;text.raycastTarget=false;text.horizontalOverflow=HorizontalWrapMode.Wrap;text.verticalOverflow=VerticalWrapMode.Truncate;return text;
  }
  Button Button(string name,Transform parent,UnityEngine.Events.UnityAction action,out Text label) {
   RectTransform rect=Rect(name,parent);Image image=rect.gameObject.AddComponent<Image>();image.color=new Color(.3f,.26f,.15f);Button button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;button.onClick.AddListener(action);
   label=Label("Label",rect);label.text=name;label.alignment=TextAnchor.MiddleCenter;return button;
  }
  void TogglePicker() {
   if(picker!=null){Destroy(picker);picker=null;return;}
   string[] choices=plugin.SocialMapChoices();RectTransform p=Rect("MapListInChat",root);picker=p.gameObject;picker.AddComponent<LayoutElement>().ignoreLayout=true;
   p.anchorMin=new Vector2(output.anchorMin.x,0);p.anchorMax=new Vector2(output.anchorMax.x,0);p.offsetMin=new Vector2(oldOutputMin.x,80);p.offsetMax=new Vector2(output.offsetMax.x,root.rect.height-4);picker.AddComponent<Image>().color=new Color(.1f,.1f,.1f,.99f);
   ScrollRect scroll=picker.AddComponent<ScrollRect>();RectTransform viewport=Rect("Viewport",p);viewport.anchorMin=Vector2.zero;viewport.anchorMax=Vector2.one;viewport.offsetMin=new Vector2(4,4);viewport.offsetMax=new Vector2(-4,-4);viewport.gameObject.AddComponent<Image>();viewport.gameObject.AddComponent<Mask>().showMaskGraphic=false;
   RectTransform list=Rect("Maps",viewport);list.anchorMin=new Vector2(0,1);list.anchorMax=new Vector2(1,1);list.pivot=new Vector2(.5f,1);list.offsetMin=new Vector2(0,-choices.Length*36);list.offsetMax=Vector2.zero;
   scroll.viewport=viewport;scroll.content=list;scroll.horizontal=false;scroll.vertical=true;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=32;
   for(int i=0;i<choices.Length;i++){int selected=i;Text label;Button button=Button(choices[i],list,delegate{plugin.SocialChoose(selected);Destroy(picker);picker=null;},out label);RectTransform row=button.transform as RectTransform;row.anchorMin=new Vector2(0,1);row.anchorMax=new Vector2(1,1);row.pivot=new Vector2(.5f,1);row.offsetMin=new Vector2(2,-(i+1)*36+1);row.offsetMax=new Vector2(-2,-i*36-1);label.alignment=TextAnchor.MiddleLeft;}
  }
  void Update(){if(plugin==null||toolbar==null)return;bool show=plugin.SocialInLobby;toolbar.gameObject.SetActive(show);foreach(CardView view in views)view.Root.gameObject.SetActive(show);if(!show){if(picker!=null){Destroy(picker);picker=null;}return;}
   bool propose=plugin.SocialIsProposalConfirmation;proposalButton.gameObject.SetActive(propose);hint.gameObject.SetActive(!propose);hint.text=plugin.SocialBusy?plugin.SocialStatus:"Обери карту, щоб запропонувати її в чаті";proposalLabel.text=plugin.SocialButton;proposalButton.interactable=plugin.SocialCanConfirm;
   SyncCards();
  }
  void SyncCards() {
   IList<MapChatCard> cards=plugin.SocialCards;
   for(int i=views.Count-1;i>=0;i--)if(!cards.Contains(views[i].Card)){Destroy(views[i].Root.gameObject);views.RemoveAt(i);}
   foreach(MapChatCard card in cards){bool exists=false;foreach(CardView view in views)if(view.Card==card)exists=true;if(exists)continue;
    CardView v=new CardView();v.Card=card;v.Root=Rect("MapOffer "+card.Offer.OfferId,content);v.Root.gameObject.AddComponent<LayoutElement>().ignoreLayout=true;v.Root.gameObject.AddComponent<Image>().color=new Color(.16f,.14f,.13f,.98f);
    v.Name=Label("MapName",v.Root);Place(v.Name.rectTransform,0,.65f,42,87);v.Name.fontStyle=FontStyle.Bold;
    v.Status=Label("MapState",v.Root);Place(v.Status.rectTransform,0,.65f,3,43);v.Status.fontSize=16;
    v.Button=Button("",v.Root,delegate{plugin.CardAct(card);},out v.Action);Place(v.Button.transform as RectTransform,.67f,1,17,69);views.Add(v);
   }
   foreach(CardView v in views){v.Name.text=plugin.CardName(v.Card);v.Status.text=plugin.CardStatus(v.Card);v.Action.text=plugin.CardAction(v.Card);v.Action.color=plugin.CardNeedsDownload(v.Card)?new Color(1,.25f,.22f):new Color(1,.87f,.55f);v.Button.interactable=plugin.CardCanAct(v.Card);}
  }
  void LateUpdate() {
   if(plugin==null||root==null||output==null||content==null)return;
   RectTransform parent=root.parent as RectTransform;if(parent==null)return;
   float baseline=(root.anchorMax.y-root.anchorMin.y)*parent.rect.height+oldRootMax.y-root.offsetMin.y;
   bool lobby=plugin.SocialInLobby;float desired=Mathf.Clamp(baseline+160,300,470);if(lobby)desired=Mathf.Min(desired,plugin.SocialChatHeightLimit(root));root.offsetMax=new Vector2(oldRootMax.x,oldRootMax.y+desired-baseline);
   output.offsetMin=oldOutputMin+new Vector2(0,lobby?34:0);expanded=true;
   AlignHorizontal(toolbar);if(picker!=null)AlignHorizontal(picker.transform as RectTransform);
   if(!lobby){if(fitter!=null)fitter.verticalFit=oldFit;return;}
   if(fitter!=null)fitter.verticalFit=ContentSizeFitter.FitMode.Unconstrained;
   float textHeight=Mathf.Max(32,panel._outputText.preferredHeight+10);float total=textHeight+views.Count*94;
   bool wasBottom=panel._outputScrollRect.verticalNormalizedPosition<=.03f;
   if(textRect!=content){textRect.anchorMin=new Vector2(0,1);textRect.anchorMax=new Vector2(1,1);textRect.pivot=new Vector2(.5f,1);textRect.offsetMin=new Vector2(0,-textHeight);textRect.offsetMax=Vector2.zero;}
   content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,total);
   for(int i=0;i<views.Count;i++){RectTransform r=views[i].Root;r.anchorMin=new Vector2(0,1);r.anchorMax=new Vector2(1,1);r.pivot=new Vector2(.5f,1);r.offsetMin=new Vector2(0,-textHeight-(i+1)*94+2);r.offsetMax=new Vector2(0,-textHeight-i*94-2);}
   if((Mathf.Abs(total-lastContentHeight)>1||lastText!=panel._outputText.text)&&wasBottom){Canvas.ForceUpdateCanvases();panel._outputScrollRect.verticalNormalizedPosition=0;}
   lastContentHeight=total;lastText=panel._outputText.text;
   if(nextLayoutLog==0){nextLayoutLog=1;plugin.SocialLayoutLog("chat="+root.rect.size+" viewport="+output.rect.size+" content="+content.rect.size+" sameText="+(textRect==content));}
  }
  void AlignHorizontal(RectTransform target){Vector3[] corners=new Vector3[4];output.GetWorldCorners(corners);float left=root.InverseTransformPoint(corners[0]).x-root.rect.xMin,right=root.InverseTransformPoint(corners[2]).x-root.rect.xMax;target.anchorMin=new Vector2(0,target.anchorMin.y);target.anchorMax=new Vector2(1,target.anchorMax.y);target.offsetMin=new Vector2(left,target.offsetMin.y);target.offsetMax=new Vector2(right,target.offsetMax.y);}
  void RestoreLayout(){if(!expanded)return;root.offsetMax=oldRootMax;output.offsetMin=oldOutputMin;if(fitter!=null)fitter.verticalFit=oldFit;expanded=false;}
  void OnDisable(){RestoreLayout();}
 }
}
