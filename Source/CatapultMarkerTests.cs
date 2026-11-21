using System;
using System.Collections.Generic;
using CastleStorySteam;
using Brix.Game.Components.CatapultSpace;
using UnityEngine;
using UnityEngine.Networking;

// Exact production callbacks against a small game surface; no Unity rendering claim.
namespace UnityEngine {
 public class Object { }
 public class MonoBehaviour : Object { }
 public class GameObject {
  readonly Dictionary<Type, object> parts = new Dictionary<Type, object>();
  public T GetComponent<T>() where T:class { object p; return parts.TryGetValue(typeof(T),out p)?p as T:null; }
  public T AddComponent<T>() where T:new() { T p=new T();parts[typeof(T)]=p;return p; }
 }
 public struct Vector3 { public float x; public Vector3(float n){x=n;} public static Vector3 zero {get{return new Vector3(0);}} public static bool operator ==(Vector3 a,Vector3 b){return a.x==b.x;} public static bool operator !=(Vector3 a,Vector3 b){return !(a==b);} public override bool Equals(object o){return o is Vector3 && this==(Vector3)o;} public override int GetHashCode(){return x.GetHashCode();} }
}
namespace UnityEngine.Networking { public static class NetworkServer {public static bool active;} public static class NetworkClient {public static bool active=true;} }
namespace HarmonyLib {
 public class Harmony {public void Patch(System.Reflection.MethodInfo m,HarmonyMethod p=null,HarmonyMethod postfix=null){} }
 public class HarmonyMethod { public HarmonyMethod(Type t,string n){} }
 public static class AccessTools {public static System.Reflection.MethodInfo Method(Type t,string n){return typeof(AccessTools).GetMethod("Dummy");} public static void Dummy(){} }
}
namespace Brix.Game.Components.CatapultSpace {
 public class CatapultState { public class MessageViser{} }
 public class CatapultControl {public GameObject gameObject=new GameObject();public CatapultBody body;public Vector3 cible,cibleNormale;public T GetComponent<T>() where T:class{return gameObject.GetComponent<T>();} }
 public class Trajectory {public bool Active;public void SetActif(bool value){Active=value;} }
 public class CatapultBody {public CatapultControl control; public bool afficherCible;public Trajectory trajetCible=new Trajectory(); public int Messages;public void HandleMessage(CatapultState.MessageViser m){Messages++;afficherCible=true;trajetCible.Active=true;} }
}
class CatapultMarkerTests {
 static int count;
 static void Check(bool ok,string name){count++;if(!ok)throw new Exception(name);}
 static void Main(){
  CatapultControl c=new CatapultControl();CatapultBody b=new CatapultBody();c.body=b;b.control=c;c.cible=new Vector3(5);
  ClientCatapultMarker.Deserialized(c);
  Check(b.Messages==0,"deserialize defers until Healthy reaction");
  ClientCatapultMarker.HealthyTick(b);Check(b.Messages==1 && b.afficherCible,"first target shown when Healthy");
  ClientCatapultMarker.HealthyTick(b);ClientCatapultMarker.Deserialized(c);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==1,"unchanged target once only");
  c.cibleNormale=new Vector3(2);ClientCatapultMarker.Deserialized(c);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==2,"normal-only change delivered");
  c.cible=new Vector3(9);ClientCatapultMarker.Deserialized(c);c.cible=new Vector3(10);ClientCatapultMarker.Deserialized(c);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==3,"coalesce latest while pending");
  b.afficherCible=false;ClientCatapultMarker.Initialized(b,true);Check(b.Messages==3,"transition callback does not reenter state machine");ClientCatapultMarker.HealthyTick(b);Check(b.Messages==4 && b.afficherCible,"resend after initializing reset");
  c.cible=Vector3.zero;ClientCatapultMarker.Deserialized(c);Check(!b.afficherCible && !b.trajetCible.Active,"zero hides immediately");ClientCatapultMarker.HealthyTick(b);Check(b.Messages==4,"zero never aims");
  NetworkServer.active=true;c.cible=new Vector3(20);ClientCatapultMarker.Deserialized(c);ClientCatapultMarker.Initialized(b,true);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==4,"host untouched");NetworkServer.active=false;
  c.body=null;ClientCatapultMarker.Deserialized(c);c.body=b;ClientCatapultMarker.HealthyTick(b);Check(b.Messages==5,"target survives body attachment later");
  ClientCatapultMarker.Initialized(b,false);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==5,"failed transition no resend");
  NetworkClient.active=false;c.cible=new Vector3(21);ClientCatapultMarker.Deserialized(c);ClientCatapultMarker.HealthyTick(b);Check(b.Messages==5,"offline untouched");
  Console.WriteLine("PASS "+count+" catapult callback checks; no renderer/network simulation");
 }
}
