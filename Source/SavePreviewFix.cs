using System;
using System.Collections.Generic;
using System.IO;
using Brix.Assets;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStorySteam {
 internal static class SavePreviewFix {
  sealed class Entry { internal long Stamp,Length; internal Texture2D Texture; }
  static readonly Dictionary<string,Entry> cache=new Dictionary<string,Entry>();
  static readonly Dictionary<string,long> failed=new Dictionary<string,long>();
  static Harmony harmony;
  const int MaxBytes=32*1024*1024;
  internal static void Install() {
   harmony=new Harmony("local.castlestory.steambridge.savepreview");
   harmony.Patch(AccessTools.Method(typeof(Asset_Map),"Disk_ReadThumbnail"),new HarmonyMethod(typeof(SavePreviewFix),"Read"));
   harmony.Patch(AccessTools.Method(typeof(Asset_Map),"Disk_ReadThumbnailAsync"),new HarmonyMethod(typeof(SavePreviewFix),"ReadAsync"));
   harmony.Patch(AccessTools.Method(typeof(Asset_Map),"Disk_WriteThumbnail"),new HarmonyMethod(typeof(SavePreviewFix),"Write"));
   SceneManager.activeSceneChanged+=ClearScene;
  }
  static bool Save(Asset_Map asset) { return asset!=null && asset.mapType==Asset_Map.MapType.Save; }
  static bool Read(Asset_Map __instance,ref Texture2D __result) {
   if(!Save(__instance))return true;
   __result=Load(__instance);return false;
  }
  static bool ReadAsync(Asset_Map __instance,Action<Texture2D> callback) {
   if(!Save(__instance))return true;
   Texture2D texture=Load(__instance);
   // The native cached path already calls callbacks synchronously. An optional
   // preview must complete even when the PNG is missing or invalid.
   try { if(callback!=null)callback(texture); } catch(Exception) { }
   return false;
  }
  static Texture2D Load(Asset_Map asset) {
   string path=null;
   Texture2D texture=null;
   try {
    path=Path.Combine(asset.path,"thumbnail.png");FileInfo info=new FileInfo(path);
    if(!info.Exists || info.Length<33 || info.Length>MaxBytes)return null;
    long stamp=info.LastWriteTimeUtc.Ticks;Entry hit;
    if(cache.TryGetValue(path,out hit)&&hit.Stamp==stamp&&hit.Length==info.Length&&hit.Texture!=null) { asset.thumbnail=hit.Texture;return hit.Texture; }
    long bad;if(failed.TryGetValue(path,out bad)&&bad==stamp)return null;
    byte[] bytes=File.ReadAllBytes(path);
    if(!ValidPng(bytes)){failed[path]=stamp;return null;}
    texture=new Texture2D(2,2,TextureFormat.RGBA32,false);
    if(!texture.LoadImage(bytes)){UnityEngine.Object.Destroy(texture);failed[path]=stamp;return null;}
    Texture2D small=Compact(texture);
    if(small!=texture)UnityEngine.Object.Destroy(texture);
    texture=small;
    if(hit!=null&&hit.Texture!=null)UnityEngine.Object.Destroy(hit.Texture);
    cache[path]=new Entry{Stamp=stamp,Length=info.Length,Texture=texture};
    asset.thumbnail=texture;return texture;
   } catch(Exception e) { Note("read",e,asset!=null?asset.path:null);if(texture!=null)UnityEngine.Object.Destroy(texture);return null; }
  }
  static int noted; static BepInEx.Logging.ManualLogSource noteLog;
  // 0.3.60: a preview problem never becomes a failed save/load; the first few are logged with the path involved.
  static void Note(string where,Exception e,string path) {
   try {
    if(noted++>=5)return; if(noteLog==null)noteLog=BepInEx.Logging.Logger.CreateLogSource("SavePreview");
    noteLog.LogWarning("Save preview "+where+" skipped: "+e.GetType().Name+": "+e.Message+"; path=["+(path??"null")+"]");
   } catch(Exception) { }
  }
  internal static bool ValidPng(byte[] data) { return SavePreviewRules.ValidPng(data); }
  internal static Texture2D Compact(Texture2D source) {
   if(source.width<=512&&source.height<=512)return source;
   int width,height; SavePreviewRules.Fit(source.width,source.height,out width,out height);
   RenderTexture prior=RenderTexture.active,target=null;Texture2D result=null;
   try {
    target=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32);
    Graphics.Blit(source,target);RenderTexture.active=target;
    result=new Texture2D(width,height,TextureFormat.RGBA32,false);
    result.ReadPixels(new Rect(0,0,width,height),0,0);result.Apply();return result;
   } catch { if(result!=null)UnityEngine.Object.Destroy(result);throw; }
   finally {RenderTexture.active=prior;if(target!=null)RenderTexture.ReleaseTemporary(target);}
  }
  static bool Write(Asset_Map __instance) {
   if(!Save(__instance))return true;
   Texture2D source=__instance.thumbnail,small=null;
   if(source==null)return false;
   try {
    small=Compact(source);
    File.WriteAllBytes(Path.Combine(__instance.path,"thumbnail.png"),small.EncodeToPNG());
   } catch(Exception ex) { Note("write",ex,__instance.path);
    // Preview generation is optional. Keep the native world-data writer running;
    // never remove or rewrite another save's files to work around a picture.
   } finally { if(small!=null&&small!=source)UnityEngine.Object.Destroy(small); }
   return false;
  }
  static void ClearScene(Scene before,Scene after) {Clear();}
  static void Clear() {foreach(Entry item in cache.Values)if(item.Texture!=null)UnityEngine.Object.Destroy(item.Texture);cache.Clear();failed.Clear();}
  internal static void Shutdown() {SceneManager.activeSceneChanged-=ClearScene;Clear();if(harmony!=null)harmony.UnpatchSelf();harmony=null;}
 }
}
