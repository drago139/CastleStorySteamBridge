#if FOGTRACE
using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.AI.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
namespace CastleStorySteam {
 public sealed partial class SteamBridgePlugin {
  // Observation only. Bounded128 marker/cell samples, <=2 observations/100ms.
  // Order attribution is bounded256 cells/300 seconds; unknown is explicit. Cell spheres are native data, not shader readback.
  // No fog/renderer/order/network state writes. Pixel visibility needs a screenshot.
  sealed class FogMarker { internal Component Marker; internal GameObject Target; internal Vector3 Position; internal Faction Owner; internal string Path; internal int Last=-1; internal float Next; }
  static readonly List<FogMarker> fogMarkers=new List<FogMarker>();
  sealed class FogOrder { internal Faction Owner; internal float At; internal string Path; }
  static readonly Dictionary<XYZ,FogOrder> fogOrders=new Dictionary<XYZ,FogOrder>();
  static int fogCursor,fogDropped,fogStacks; static float fogNextTick;
  void FogInstall() {
   foreach(string method in new string[]{"OnStartAnywhere","OnDeserialize","StateChange"})
    discoveryHarmony.Patch(AccessTools.Method(typeof(Blueprint),method),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogBlueprint")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(UNetProjectCmd),"RpcAddHighlightOnPosition"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogRpc")));
   foreach(string method in new string[]{"HighlightPosition","HighlightObject","HighlightBlock"})
    discoveryHarmony.Patch(AccessTools.Method(typeof(Highlight),method),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogHighlight")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(WorkNode),"Dig"),new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogDigOrder")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(CombatNodes),"BreakObject",new Type[]{typeof(Labor),typeof(GameComponent)}),new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogBreakOrder")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(LandscapeProjectUI),"AddOrSetGoal"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogLandscape")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(LandscapeProjectUI),"ClearGoals"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogLandscapeClear")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(UNetProjectCmd),"CmdAddBreakVoxelGoal"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogDigCommand")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(UNetProjectCmd),"CmdBreakObject"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogBreakCommand")));
   discoveryHarmony.Patch(AccessTools.Method(typeof(OverheadDisplayDriver),"Init"),null,new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin),"FogOverhead")));
   PtLog("FOG-MARKER hooks=14 observation-only anti-blueprint/work/selection/command/overhead; cell fog uses native VisionArray spheres, not shader pixel readback");
  }
  static void FogQueue(Component marker,GameObject target,Vector3 position,Faction owner,string path) {
   if(marker==null)return;
   for(int i=0;i<fogMarkers.Count;i++)if(fogMarkers[i].Marker==marker && (!(marker is LandscapeProjectUI) || fogMarkers[i].Position==position)){fogMarkers[i].Position=position;fogMarkers[i].Target=target;fogMarkers[i].Owner=owner;fogMarkers[i].Path=path;fogMarkers[i].Last=-1;return;}
   if(fogMarkers.Count>=128){fogMarkers.RemoveAt(0);fogDropped++;}
   fogMarkers.Add(new FogMarker{Marker=marker,Target=target,Position=position,Owner=owner,Path=path});
  }
  static void FogBlueprint(Blueprint __instance,System.Reflection.MethodBase __originalMethod) {
   try{if(__instance!=null&&__instance.IsAnti)FogQueue(__instance,__instance.gameObject,__instance.transform.position,__instance.faction,"Blueprint."+__originalMethod.Name+"/anti-SyncVar");}catch(Exception){}
  }
  static void FogHighlight(Highlight __instance,System.Reflection.MethodBase __originalMethod) {
   try{GameObject go=__instance.TargetGO;GameComponent gc=go==null?null:go.GetComponent<GameComponent>();FogQueue(__instance,go,__instance.transform.position,gc==null?null:gc.faction,"Highlight."+__originalMethod.Name+"/owner-is-target-not-order-author");if(fogStacks<32){fogStacks++;PtLog("FOG-CALL pos="+__instance.transform.position+" stack="+new System.Diagnostics.StackTrace(1,false).ToString().Replace("\r","").Replace("\n"," | "));}}catch(Exception){}
  }
  static void FogOverhead(OverheadDisplayDriver __instance) {try{DigGoal g=__instance.GetComponent<DigGoal>();Blueprint b=__instance.GetComponent<Blueprint>();if(g!=null&&__instance.overheadDisplay!=null)FogQueue(__instance,null,g.transform.position,g.faction,"OverheadDisplayDriver.Init/DigGoal; owner-is-goal-not-order-author");else if(b!=null&&b.IsAnti&&__instance.overheadDisplay!=null)FogQueue(__instance,b.gameObject,b.transform.position,b.faction,"OverheadDisplayDriver.Init/anti-blueprint");}catch(Exception){}}
  static void FogCommand(UNetProjectCmd cmd,GameObject worker,Vector3 pos,string path) {try{User u=cmd.GetComponent<User>();Labor l=worker==null?null:worker.GetComponent<Labor>();PtLog("FOG-CMD role="+(NetworkServer.active?"host":"guest")+" senderFaction="+(u==null||u.faction==null?"unknown":u.faction.ID.ToString())+" workerFaction="+(l==null||l.faction==null?"unknown":l.faction.ID.ToString())+" localFaction="+(User.LocalUser==null||User.LocalUser.faction==null?"unknown":User.LocalUser.faction.ID.ToString())+" pos="+pos+" path="+path+"; observed after native command, not proof work started");}catch(Exception){}}
  static void FogDigCommand(UNetProjectCmd __instance,GameObject laborGO,int index) {FogCommand(__instance,laborGO,XYZ.FromScalaire(index).ToVector3(),"UNetProjectCmd.CmdAddBreakVoxelGoal");}
  static void FogBreakCommand(UNetProjectCmd __instance,GameObject laborGO,GameObject go) {if(go!=null)FogCommand(__instance,laborGO,go.transform.position,"UNetProjectCmd.CmdBreakObject target="+go.name);}
  static void FogRememberOrder(Labor labor,Vector3 position,string path) {
   if(labor==null)return;XYZ key=XYZ.FromVector3(position);FogOrder old;
   if(fogOrders.TryGetValue(key,out old)&&old.Owner==labor.faction&&Time.realtimeSinceStartup-old.At<10)return;
   if(fogOrders.Count>=256)fogOrders.Clear();
   fogOrders[key]=new FogOrder{Owner=labor.faction,At=Time.realtimeSinceStartup,Path=path};
   PtLog("FOG-ORDER owner="+(labor.faction==null?"unknown":labor.faction.ID.ToString())+" pos="+key+" path="+path+"; authoritative worker attribution, not target ownership");
  }
  static void FogDigOrder(Labor labor,Location toDig) {try{FogRememberOrder(labor,toDig.Position,"WorkNode.Dig");}catch(Exception){}}
  static void FogBreakOrder(Labor labor,GameComponent gameComponent) {try{if(gameComponent!=null)FogRememberOrder(labor,gameComponent.transform.position,"CombatNodes.BreakObject");}catch(Exception){}}
  static void FogLandscape(LandscapeProjectUI __instance,XYZ pos,LandscapeGoal.Landscape type) {try{if(type==LandscapeGoal.Landscape.Dig)FogQueue(__instance,__instance.gameObject,pos.ToVector3(),__instance.faction,"LandscapeProjectUI.AddOrSetGoal/Dig; bounded128-cell sample");}catch(Exception){}}
  static void FogLandscapeClear(LandscapeProjectUI __instance) {try{for(int i=fogMarkers.Count-1;i>=0;i--)if(fogMarkers[i].Marker==__instance)fogMarkers.RemoveAt(i);}catch(Exception){}}
  static void FogRpc(UNetProjectCmd __instance,int x,int y,int z) {
   try{User u=__instance.GetComponent<User>();PtLog("FOG-RPC add pos="+new XYZ(x,y,z)+" userOwner="+(u==null||u.faction==null?"unknown":u.faction.ID.ToString())+" path=UNetProjectCmd.RpcAddHighlightOnPosition; no author/fog in payload");}catch(Exception){}
  }
  static void FogTraceTick(float now) {
   if(now<fogNextTick||User.LocalUser==null||User.LocalUser.faction==null)return;fogNextTick=now+.1f;
   try{Faction local=User.LocalUser.faction;
    for(int batch=0;batch<2&&fogMarkers.Count>0;batch++) {
     if(fogCursor>=fogMarkers.Count)fogCursor=0;FogMarker m=fogMarkers[fogCursor];
     if(m.Marker==null||!m.Marker.gameObject.activeInHierarchy){fogMarkers.RemoveAt(fogCursor);continue;}
     fogCursor++;if(now<m.Next)continue;m.Next=now+2f;
     Renderer r=m.Marker.GetComponent<Renderer>();LandscapeProjectUI land=m.Marker as LandscapeProjectUI;OverheadDisplayDriver overhead=m.Marker as OverheadDisplayDriver;bool enabled=land!=null?land.ShowGoals:overhead!=null?overhead.overheadDisplay!=null&&overhead.overheadDisplay.gameObject.activeInHierarchy:r!=null&&r.enabled;
     bool frustum=false;if(enabled&&Camera.main!=null){if(land==null&&overhead==null)frustum=GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(Camera.main),r.bounds);else{Vector3 vp=Camera.main.WorldToViewportPoint(m.Position);frustum=vp.z>0&&vp.x>=0&&vp.x<=1&&vp.y>=0&&vp.y<=1;}}
     float nearest=float.PositiveInfinity;int own=0;
     if(local.SharedVision!=null)foreach(LocalFactionVision v in local.SharedVision.AllVisions){if(v==null||v.faction!=local||v.LineOfSightRange<=0)continue;own++;nearest=Math.Min(nearest,Vector3.Distance(m.Position,v.transform.position));}
     Vector4[] array=FogOfWarRenderer.VisionArray;bool covered=false;int spheres=0;
     if(array!=null)foreach(Vector4 v in array)if(v.w>0){spheres++;if((new Vector3(v.x,v.y,v.z)-m.Position).sqrMagnitude<=v.w*v.w)covered=true;}
     int state=(enabled?1:0)|(frustum?2:0)|(covered?4:0);if(m.Last==state)continue;m.Last=state;
     FogOrder order;fogOrders.TryGetValue(XYZ.FromVector3(m.Position),out order);if(order!=null&&now-order.At>300)order=null;
     LocalFactionVision vision=m.Target==null?null:m.Target.GetComponent<LocalFactionVision>();string native=vision==null?"no-component":vision.IsVisibleForFaction(local).ToString();
     PtLog("FOG-MARKER local="+local.ID+" role="+(NetworkServer.active?"host":"guest")+" owner="+(m.Owner==null?"unknown":m.Owner.ID.ToString())+" pos="+m.Position+" orderOwner="+(order==null||order.Owner==null?"unknown":order.Owner.ID.ToString())+" orderPath="+(order==null?"unattributed":order.Path)+" nearestOwnVision="+nearest.ToString("F1")+" ownCount="+own+" cellHiddenNativeSpheres="+(array==null?"unavailable":(!covered).ToString())+" spheres="+spheres+" allVisible="+LocalFactionVision.AllVisible+" global="+local.HasGlobalFogVision+" nativeObjectVisible="+native+" enabled="+enabled+" inCameraFrustum="+frustum+" path="+m.Path+" dropped="+fogDropped+"; screenshot required; target owner may differ from order author");
    }
   }catch(Exception ex){fogNextTick=now+10;PtLog("FOG-MARKER unavailable "+ex.GetType().Name);}
  }
 }
}

#endif
