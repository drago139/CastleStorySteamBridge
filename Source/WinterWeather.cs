using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using UnityEngine;
namespace CastleStorySteam {
 internal sealed class WinterWeather:MonoBehaviour {
  GameObject root;ParticleSystem snow;Material material;Texture2D texture;ParticleSystem.Particle[] flakes=new ParticleSystem.Particle[4200];bool placed;
  internal static float Intensity {get;private set;}
  void Awake(){
   root=new GameObject("SteamBridge_WinterWeather");snow=root.AddComponent<ParticleSystem>();snow.Stop();
   var main=snow.main;main.loop=true;main.startLifetime=100000;main.startSpeed=0;main.startSize=new ParticleSystem.MinMaxCurve(.06f,.16f);main.maxParticles=4200;main.simulationSpace=ParticleSystemSimulationSpace.World;main.startColor=new Color(.94f,.97f,1,.8f);
   var shape=snow.shape;shape.shapeType=ParticleSystemShapeType.Box;shape.box=new Vector3(100,50,100);
   Shader shader=Shader.Find("Particles/Alpha Blended")??Shader.Find("Sprites/Default");material=new Material(shader);
   texture=new Texture2D(16,16,TextureFormat.ARGB32,false);var pixels=new Color[256];for(int y=0;y<16;y++)for(int x=0;x<16;x++){float d=Vector2.Distance(new Vector2(x,y),new Vector2(7.5f,7.5f))/7.5f;pixels[y*16+x]=new Color(1,1,1,Mathf.Clamp01(1-d*d));}texture.SetPixels(pixels);texture.Apply();material.mainTexture=texture;snow.GetComponent<ParticleSystemRenderer>().sharedMaterial=material;
   var collision=snow.collision;collision.enabled=false;collision.type=ParticleSystemCollisionType.World;collision.collidesWith=1024;collision.lifetimeLoss=1;collision.quality=ParticleSystemCollisionQuality.Low;
   var emission=snow.emission;emission.enabled=false;snow.Play();
  }
  void LateUpdate(){
   var cam=Camera.main;if(cam==null)return;
   float time=Time.time;Intensity=Mathf.Lerp(.15f,1,Mathf.PerlinNoise(time/95f,3.7f));
   float gust=Mathf.PerlinNoise(time/11f,8.2f),angle=Mathf.PerlinNoise(time/140f,12.6f)*Mathf.PI*4;
   var velocity=snow.velocityOverLifetime;velocity.enabled=true;velocity.space=ParticleSystemSimulationSpace.World;velocity.x=Mathf.Cos(angle)*(.4f+gust*4);velocity.z=Mathf.Sin(angle)*(.4f+gust*4);velocity.y=-2.1f-Intensity*1.7f;
   // Fixed world-space density: retain particles during pans/zoom, recycle only
   // flakes leaving the volume behind the view. Never clear or burst on camera motion.
   Vector3 center=cam.transform.position+cam.transform.forward*45;
   const float span=160,vertical=110;
   int count;
   if(!placed){count=flakes.Length;var random=new System.Random(7931);for(int i=0;i<count;i++){
    flakes[i].position=center+new Vector3((float)random.NextDouble()*span-span/2,(float)random.NextDouble()*vertical-vertical/2,(float)random.NextDouble()*span-span/2);
    flakes[i].startLifetime=100000;flakes[i].remainingLifetime=100000;flakes[i].startSize=.055f+(float)random.NextDouble()*.09f;
   }placed=true;}else count=snow.GetParticles(flakes);
   for(int i=0;i<count;i++){
    Vector3 p=flakes[i].position-center;p.x=Mathf.Repeat(p.x+span/2,span)-span/2;p.y=Mathf.Repeat(p.y+vertical/2,vertical)-vertical/2;p.z=Mathf.Repeat(p.z+span/2,span)-span/2;flakes[i].position=center+p;
    float density=Mathf.Clamp01((Intensity*.7f+.25f-(i%97)/97f)*12);
    flakes[i].startColor=new Color(.94f,.97f,1,density*.8f);flakes[i].remainingLifetime=100000;
   }
   snow.SetParticles(flakes,count);

  }
  void OnDestroy(){Intensity=0;if(root!=null)Destroy(root);if(material!=null)Destroy(material);if(texture!=null)Destroy(texture);}
 }
 // Diagnostic (0.3.35): says what a long frame coincided with, so freezes are measured instead of guessed.
 internal sealed class WinterFrameWatch:MonoBehaviour {
  internal static int RebuildFrame=-100;int g0,g1,g2,logged;
  void Start(){g0=GC.CollectionCount(0);g1=GC.CollectionCount(1);g2=GC.CollectionCount(2);}
  void Update(){
   int a=GC.CollectionCount(0),b=GC.CollectionCount(1),c=GC.CollectionCount(2);
   float ms=Time.unscaledDeltaTime*1000f;
   if(ms>=250&&logged<40&&SeasonalEvents.Log!=null){logged++;SeasonalEvents.Log("Frame hitch "+ms.ToString("F0")+" ms; heap "+(GC.GetTotalMemory(false)/1048576)+" MB; GC gen0 +"+(a-g0)+", gen1 +"+(b-g1)+", gen2 +"+(c-g2)+"; snow rebuild "+(Time.frameCount-RebuildFrame<=1?"in this frame":"not in this frame"));}
   g0=a;g1=b;g2=c;
  }
 }
 internal sealed class WinterHat:MonoBehaviour {
  Toolbag bag;LocalFactionVision vision;GameObject root;readonly List<Renderer> renderers=new List<Renderer>();Material red,white;Mesh cloth,brim;
  public void Init(Toolbag target){
   bag=target;vision=bag.GetComponent<LocalFactionVision>();root=new GameObject("SteamBridge_WinterHat");root.transform.SetParent(bag.Head.Limb.transform,false);
   red=new Material(Shader.Find("Standard"));red.color=new Color(.72f,.025f,.035f);red.SetFloat("_Glossiness",.08f);white=new Material(Shader.Find("Standard"));white.color=new Color(.96f,.97f,1);white.SetFloat("_Glossiness",.05f);
   // The native head socket supplies the animated position and orientation.
   Matrix4x4 m=bag.Head.Limb.socketMatrix;Brix.Utils.Matrix4x4Utils.SetTransformFromMatrix(root.transform,ref m);root.transform.localPosition+=root.transform.localRotation*Vector3.up*.10f;
   var vertices=new List<Vector3>();var triangles=new List<int>();const int sides=32,rings=20;
   for(int j=0;j<=rings;j++){float t=(float)j/rings;Vector3 center=new Vector3(.44f*t*t,.04f+.90f*t-.66f*t*t*t,0);float radius=.335f*Mathf.Pow(1-t,.85f);for(int i=0;i<=sides;i++){float a=i*Mathf.PI*2/sides;Vector3 tangent=new Vector3(.88f*t,.90f-1.98f*t*t,0).normalized;Vector3 across=new Vector3(tangent.y,-tangent.x,0);vertices.Add(center+(across*Mathf.Cos(a)+Vector3.forward*Mathf.Sin(a))*radius*(1+.025f*Mathf.Sin(a*5+t*3)));}}
   for(int j=0;j<rings;j++)for(int i=0;i<sides;i++){int a=j*(sides+1)+i,b=a+sides+1;triangles.Add(a);triangles.Add(b);triangles.Add(a+1);triangles.Add(a+1);triangles.Add(b);triangles.Add(b+1);}
   cloth=new Mesh();cloth.SetVertices(vertices);cloth.SetTriangles(triangles,0);cloth.RecalculateNormals();cloth.RecalculateBounds();var cap=new GameObject("Red fabric");cap.transform.SetParent(root.transform,false);cap.AddComponent<MeshFilter>().sharedMesh=cloth;var r=cap.AddComponent<MeshRenderer>();r.sharedMaterial=red;renderers.Add(r);
   vertices.Clear();triangles.Clear();
   for(int i=0;i<=48;i++)for(int j=0;j<=12;j++){float a=i*Mathf.PI*2/48,b=j*Mathf.PI*2/12;float radius=.31f+.068f*Mathf.Cos(b);vertices.Add(new Vector3(Mathf.Cos(a)*radius,.045f+.075f*Mathf.Sin(b),Mathf.Sin(a)*radius));}
   for(int i=0;i<48;i++)for(int j=0;j<12;j++){int a=i*13+j,b=a+13;triangles.Add(a);triangles.Add(a+1);triangles.Add(b);triangles.Add(a+1);triangles.Add(b+1);triangles.Add(b);}
   brim=new Mesh();brim.SetVertices(vertices);brim.SetTriangles(triangles,0);brim.RecalculateNormals();brim.RecalculateBounds();var trim=new GameObject("Continuous white fur brim");trim.transform.SetParent(root.transform,false);trim.AddComponent<MeshFilter>().sharedMesh=brim;var tr=trim.AddComponent<MeshRenderer>();tr.sharedMaterial=white;renderers.Add(tr);
   Vector3 pom=new Vector3(.44f,.28f,0);Puff(pom,Vector3.one*.23f);
   for(int i=0;i<18;i++){float a=i*2.399963f,y=1-2*(i+.5f)/18;float radius=Mathf.Sqrt(1-y*y);Puff(pom+new Vector3(Mathf.Cos(a)*radius,y,Mathf.Sin(a)*radius)*.095f,Vector3.one*.065f);}

   if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter red hat attached: "+bag.name);
  }
  void Puff(Vector3 position,Vector3 scale){var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);Destroy(go.GetComponent<Collider>());go.transform.SetParent(root.transform,false);go.transform.localPosition=position;go.transform.localScale=scale;var r=go.GetComponent<Renderer>();r.sharedMaterial=white;renderers.Add(r);}
  void LateUpdate(){bool show=WinterEvents.Enabled&&VisualOptions.WinterHats&&bag!=null&&bag.isActiveAndEnabled&&!bag.Head.Occupied&&vision!=null&&vision.IsVisibleForMe();foreach(var r in renderers)if(r!=null)r.enabled=show;}
  void OnDestroy(){if(root!=null)Destroy(root);if(red!=null)Destroy(red);if(white!=null)Destroy(white);if(cloth!=null)Destroy(cloth);if(brim!=null)Destroy(brim);}
 }
}

