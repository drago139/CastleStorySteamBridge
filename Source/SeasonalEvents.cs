using System;
using System.IO;
using System.Reflection;
using Path = System.IO.Path;
using System.Collections.Generic;
using BepInEx;
using Brix.Engine;
using Brix.Assets;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Utils;
using Brix.Utils.Unity;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
namespace CastleStorySteam {
 internal sealed class SeasonalEvents : MonoBehaviour {
  internal static bool Active { get { return SeasonalCalendar.Halloween(DateTime.Now) && !Neo.disableSpecialAssets; } }
  internal static bool WinterActive { get { return SeasonalCalendar.Winter(DateTime.Now) && !Neo.disableSpecialAssets; } }
  internal static string Status { get { return WinterActive ? "Події за календарем: зима (грудень — лютий)." : Active ? "Події за календарем: Геловін (1 жовтня — 1 листопада)." : "Події за календарем: Геловін 1 жовтня — 1 листопада."; } }
  sealed class Hat { internal Mesh mesh;internal Material material;internal Texture2D texture;internal Matrix4x4 grip; }
  readonly List<Hat> hats=new List<Hat>(); readonly List<SeasonalCostume> costumes=new List<SeasonalCostume>();
  Harmony harmony;float nextScan;bool attempted;internal static Action<string> Log;
  void Awake(){
   gameObject.AddComponent<WinterEvents>();gameObject.AddComponent<WinterFrameWatch>();
   harmony=new Harmony("local.castlestory.seasonalevents");
   harmony.Patch(AccessTools.Method(typeof(Asset_Mod),"Disk_LoadMod"),new HarmonyMethod(typeof(SeasonalEvents),"AllowOriginalMoon"));
   if(Log!=null)Log("Halloween: Capture the Pumpkin uses native pumpkin patches and cooking pots.");
  }
  static bool AllowOriginalMoon(Asset_Mod __instance,object[] __args){
   if(__instance.shortCodename!="halloween2017" || Active)return true;
   __args[0]=null;return false;
  }
  static bool IsPumpkinMode(){
   try{
    if(GameParam.Gamemode==null)return false;
    // gtg is the permanent renamed version; ctp is the restored Halloween mode.
    return GameParam.Gamemode.shortCodename=="ctp";
   }catch{return false;}
  }
  void Update(){
   if(Time.realtimeSinceStartup<nextScan)return;nextScan=Time.realtimeSinceStartup+(costumes.Count>0?30:2);
   UpdateMoon();
   costumes.RemoveAll(delegate(SeasonalCostume c){return c==null;});
   if(!Active||!VisualOptions.Costumes){foreach(var c in costumes)if(c!=null)Destroy(c.gameObject);costumes.Clear();return;}
   Toolbag[] bags=UnityEngine.Object.FindObjectsOfType<Toolbag>();if(bags.Length==0)return;
   if(!attempted){attempted=true;LoadHats();}
   if(hats.Count==0)return;
   int added=0;
   foreach(Toolbag bag in bags){
    if(bag.Head==null||bag.Head.Limb==null||bag.GetComponent<Labor>()==null)continue;
    Transform socket=bag.Head.Limb.transform;if(socket.Find("SteamBridge_SeasonalCostume")!=null)continue;
    NetworkIdentity net=bag.GetComponent<NetworkIdentity>();uint identity=net!=null?net.netId.Value:(uint)bag.GetInstanceID();Hat hat=hats[(int)(identity%(uint)hats.Count)];
    GameObject go=new GameObject("SteamBridge_SeasonalCostume");go.layer=socket.gameObject.layer;go.transform.SetParent(socket,false);
    Matrix4x4 matrix=bag.Head.Limb.socketMatrix*hat.grip.inverse;Matrix4x4Utils.SetTransformFromMatrix(go.transform,ref matrix);
    go.AddComponent<MeshFilter>().sharedMesh=hat.mesh;MeshRenderer renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=hat.material;
    SeasonalCostume c=go.AddComponent<SeasonalCostume>();c.bag=bag;c.meshRenderer=renderer;c.vision=bag.GetComponent<LocalFactionVision>();costumes.Add(c);added++;
   }
   if(added>0&&Log!=null)Log("Halloween: added "+added+" visual costumes; no tools, damage or save entities changed.");
  }
  MeshFilter moonFilter; Renderer moonRenderer; Mesh originalMoon, pumpkinMoon; Material originalMoonMaterial, pumpkinMoonMaterial;
  readonly List<Texture2D> moonTextures=new List<Texture2D>();
  void RestoreMoon(){
   if(moonFilter!=null && originalMoon!=null)moonFilter.sharedMesh=originalMoon;
   if(moonRenderer!=null && originalMoonMaterial!=null)moonRenderer.sharedMaterial=originalMoonMaterial;
   moonFilter=null;moonRenderer=null;
  }
  Texture2D MoonTexture(string name){
   Texture2D t=new Texture2D(2,2,TextureFormat.RGB24,false);
   if(!t.LoadImage(File.ReadAllBytes(Path.Combine(Paths.GameRootPath,"Info/Lua/Mods/halloween2017/Moon/PumpkinMoon_"+name+".jpg")))){Destroy(t);throw new IOException("Moon texture: "+name);}
   moonTextures.Add(t);return t;
  }
  void UpdateMoon(){
   if(!IsPumpkinMode() || !Active || !VisualOptions.Moon){RestoreMoon();return;}
   if(moonFilter!=null)return;
   try{
    foreach(Brix.Skybox.Orbit orbit in UnityEngine.Object.FindObjectsOfType<Brix.Skybox.Orbit>()){
     if(orbit.TCelestialBody==null || orbit.TCelestialBody.name!="Moon")continue;
     MeshFilter mf=orbit.TCelestialBody.GetComponentInChildren<MeshFilter>();Renderer mr=orbit.TCelestialBody.GetComponentInChildren<Renderer>();
     if(mf==null||mr==null)continue;
     if(pumpkinMoon==null){
      pumpkinMoon=ObjImporter.ImportFile(Path.Combine(Paths.GameRootPath,"Info/Lua/Mods/halloween2017/Moon/PumpkinMoon.obj"));
      Brix.Lua.LuaMesh.RecalculateTangents(pumpkinMoon);
      Shader shader=Shader.Find("Castle Story/Corruptron");if(shader==null)throw new IOException("Moon shader unavailable");
      pumpkinMoonMaterial=new Material(shader);
      pumpkinMoonMaterial.SetTexture("_Diffuse",MoonTexture("Diffuse"));pumpkinMoonMaterial.SetTexture("_Normals",MoonTexture("Normals"));
      Texture2D glow=MoonTexture("Glow");pumpkinMoonMaterial.SetTexture("_Emission",glow);pumpkinMoonMaterial.SetTexture("_DistanceMap",glow);pumpkinMoonMaterial.SetTexture("_Specular",Texture2D.blackTexture);
      pumpkinMoonMaterial.SetColor("_TintColor",new Color(.757f,.757f,.757f,1));pumpkinMoonMaterial.SetColor("_FalloffColor",new Color(.173f,.4f,.361f,1));
      pumpkinMoonMaterial.SetColor("_EmissionColor",new Color(.4f,.886f,.416f,1));pumpkinMoonMaterial.SetColor("_EmissionColor2",new Color(.553f,1,.537f,1));
      pumpkinMoonMaterial.SetFloat("_EmissionPower",.55f);pumpkinMoonMaterial.SetFloat("_EyesGlowMultiplier",1);pumpkinMoonMaterial.SetFloat("_CracksMultiplier",.3846163f);pumpkinMoonMaterial.SetFloat("_Glow",50);
     }
     originalMoon=mf.sharedMesh;originalMoonMaterial=mr.sharedMaterial;moonFilter=mf;moonRenderer=mr;
     mf.sharedMesh=pumpkinMoon;mr.sharedMaterial=pumpkinMoonMaterial;
     if(Log!=null)Log("CTP: pumpkin moon applied to active sky.");return;
    }
   }catch(Exception e){if(Log!=null)Log("CTP moon failed: "+e.Message);}
  }
  void LoadHats(){
   Add("Cowboy","Cowboy",new Vector3(0,-0.229089f,-0.006397f),353.6631f,1);
   Add("Clown","Clown",new Vector3(-0.00011f,-0.023184f,-0.099536f),0,1);
   Add("Pirate","Pirate",new Vector3(0,-0.090214f,-0.041757f),0,1);
   Add("Vampire","Vampire",new Vector3(0,0.133287f,0.006121f),0,1);
   Add("Werewolf","Wolf",new Vector3(0,0.22815f,-0.13865f),0,1);
   Add("Frankenstein","Frankenstein",new Vector3(0,-0.102833f,0),0,1);
   // grip is inverted when applied to the socket; use reciprocal values here
   // so the original halloween accessory meshes keep their intended size.
   Add("Eye","Eye",new Vector3(0,-0.1f,0),0,7.6923077f);
   Add("JackO","JackO",new Vector3(0,-0.1f,0),0,10f);
   Add("WitchHat","WitchHat",new Vector3(0,-0.147958f,0),347.7499f,1);
   Add("AlienHead","AlienHead",new Vector3(0,-0.1f,0),0,10f);
   if(Log!=null)Log("Halloween original costume assets loaded: "+hats.Count+"/10; local calendar="+DateTime.Now.ToString("yyyy-MM-dd"));
  }
  void Add(string folder,string name,Vector3 position,float angle,float scale){
   Mesh mesh=null;Texture2D texture=null;Material material=null;
   try {
    string root=Path.Combine(Paths.GameRootPath,"Info/Lua/Mods/halloween2016/"+folder);
    string obj=Path.Combine(root,name+".obj"),jpg=Path.Combine(root,name+"_Diffuse.jpg");if(!File.Exists(obj)||!File.Exists(jpg))return;
    mesh=ObjImporter.ImportFile(obj);mesh.RecalculateBounds();texture=new Texture2D(2,2,TextureFormat.RGB24,false);
    if(!texture.LoadImage(File.ReadAllBytes(jpg)))throw new IOException("Invalid costume texture");
    Shader shader=Shader.Find("Standard");if(shader==null)throw new IOException("Costume shader unavailable");
    material=new Material(shader);material.mainTexture=texture;material.SetFloat("_Glossiness",0.15f);
    hats.Add(new Hat{mesh=mesh,texture=texture,material=material,grip=Matrix4x4.TRS(position,Quaternion.Euler(angle,0,0),Vector3.one*scale)});
   } catch(Exception e){if(mesh!=null)Destroy(mesh);if(texture!=null)Destroy(texture);if(material!=null)Destroy(material);if(Log!=null)Log("Halloween costume skipped: "+name+": "+e.Message);}
  }
  void OnDestroy(){RestoreMoon();if(pumpkinMoon!=null)Destroy(pumpkinMoon);if(pumpkinMoonMaterial!=null)Destroy(pumpkinMoonMaterial);foreach(var t in moonTextures)Destroy(t);if(harmony!=null)harmony.UnpatchSelf();foreach(var c in costumes)if(c!=null)Destroy(c.gameObject);foreach(var h in hats){Destroy(h.mesh);Destroy(h.material);Destroy(h.texture);}hats.Clear();}
 }
 internal sealed class SeasonalCostume : MonoBehaviour {
  internal Toolbag bag;internal MeshRenderer meshRenderer;internal LocalFactionVision vision;
  void LateUpdate(){
   if(bag==null){Destroy(gameObject);return;}
   // Never expose an enemy through fog or replace an equipped helmet.
   bool visible=false;try{visible=SeasonalEvents.Active && VisualOptions.Costumes && bag.isActiveAndEnabled && !bag.Head.Occupied && vision!=null && vision.IsVisibleForMe();}catch{visible=false;}
   meshRenderer.enabled=visible;
  }
 }
}
