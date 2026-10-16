using System;
using UnityEngine;
using UnityEngine.UI;
namespace CastleStorySteam {
 public sealed class InGameChatSize : MonoBehaviour {
  RectTransform output,box;float boxPadding;
  public void Initialize(ChatBoxDriver driver,SteamBridgePlugin plugin){
   Text text=driver.chatPanel.GetPlugComponent<Text>("Output Text");
   ScrollRect scroll=text.GetComponentInParent<ScrollRect>();
   output=scroll==null?text.rectTransform.parent as RectTransform:scroll.transform as RectTransform;
   if(scroll==null){box=output.parent as RectTransform;boxPadding=box.rect.height-output.rect.height;}
   string layout="";for(Transform parent=output;parent!=null;parent=parent.parent){RectTransform rect=parent as RectTransform;layout+=parent.name+(rect==null?"":"="+rect.rect.size+" anchors="+rect.anchorMin+"/"+rect.anchorMax)+"; ";}
   plugin.SocialLayoutLog("In-game chat: "+layout);
   text.fontSize=Mathf.Max(16,text.fontSize);
  }
  static void SetHeight(RectTransform rect,float height){float delta=height-rect.rect.height;if(Mathf.Abs(delta)<.1f)return;Vector2 position=rect.anchoredPosition;rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,height);rect.anchoredPosition=position+new Vector2(0,delta*(rect.pivot.y-1));}
  void LateUpdate(){if(output==null)return;if(box!=null)SetHeight(box,220+boxPadding);SetHeight(output,220);}
 }
 public sealed partial class SteamBridgePlugin {
  static void AfterGameChat(ChatBoxDriver __instance){SteamBridgePlugin plugin=discoveryInstance;if(plugin==null||__instance==null||__instance.chatPanel==null)return;try{InGameChatSize size=__instance.chatPanel.GetComponent<InGameChatSize>();if(size==null){size=__instance.chatPanel.gameObject.AddComponent<InGameChatSize>();size.Initialize(__instance,plugin);}}catch(Exception e){plugin.Logger.LogWarning("In-game chat sizing: "+e.Message);}}
 }
}
