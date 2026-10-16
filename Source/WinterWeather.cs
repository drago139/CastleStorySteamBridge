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
 } internal sealed class WinterCrystalSnow:MonoBehaviour {
  sealed class Patch {internal Mesh mesh;internal Vector3[] surface;internal float[] variation;internal bool[] valid;internal MeshRenderer renderer;internal Renderer source;internal int n;internal Matrix4x4 toLocal;internal Vector3[] original;internal int[] indices;internal Vector3 bmin,bmax;}
  readonly List<Patch> patches=new List<Patch>();readonly List<Material> frostMats=new List<Material>();readonly List<Color> frostOrig=new List<Color>();readonly List<string> frostProp=new List<string>();Material material;float amount,builtAmount;int builtStep=-1,pending;
  public void Init(FireflyNest nest){
   material=new Material(Shader.Find("Standard"));material.color=new Color(.93f,.965f,1);material.SetFloat("_Glossiness",.08f);
   foreach(var filter in GetComponentsInChildren<MeshFilter>(true)){
    Renderer source=filter.GetComponent<Renderer>();if(source==null||filter.sharedMesh==null||!filter.name.StartsWith("Shard",StringComparison.OrdinalIgnoreCase))continue;RegisterFrost(source);
    Vector3[] v=filter.sharedMesh.vertices;for(int i=0;i<v.Length;i++)v[i]=transform.InverseTransformPoint(filter.transform.TransformPoint(v[i]));int[] ids=filter.sharedMesh.triangles;
    Bounds bounds=new Bounds(v[0],Vector3.zero);foreach(var point in v)bounds.Encapsulate(point);
    const int n=48;var surface=new Vector3[n*n];var valid=new bool[n*n];var variation=new float[n*n];
    for(int z=0;z<n;z++)for(int x=0;x<n;x++){
     int k=z*n+x;float px=Mathf.Lerp(bounds.min.x,bounds.max.x,x/(float)(n-1)),pz=Mathf.Lerp(bounds.min.z,bounds.max.z,z/(float)(n-1)),height=float.NegativeInfinity;
     for(int j=0;j<ids.Length;j+=3){Vector3 a=v[ids[j]],b=v[ids[j+1]],c=v[ids[j+2]];if(Vector3.Cross(b-a,c-a).normalized.y<.5f)continue;
      float d=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);if(Mathf.Abs(d)<.00001f)continue;
      float u=((b.z-c.z)*(px-c.x)+(c.x-b.x)*(pz-c.z))/d,w=((c.z-a.z)*(px-c.x)+(a.x-c.x)*(pz-c.z))/d;
      if(u>=0&&w>=0&&u+w<=1)height=Mathf.Max(height,u*a.y+w*b.y+(1-u-w)*c.y);
     }
     valid[k]=!float.IsNegativeInfinity(height);surface[k]=new Vector3(px,valid[k]?height:bounds.min.y,pz);variation[k]=Mathf.PerlinNoise(px*2.1f+19,pz*2.1f+7);
    }
    var mesh=new Mesh();var go=new GameObject("SteamBridge_RoundedCrystalSnow");go.layer=filter.gameObject.layer;go.transform.SetParent(filter.transform,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
    patches.Add(new Patch{mesh=mesh,surface=surface,variation=variation,valid=valid,renderer=renderer,source=source,n=n,toLocal=filter.transform.worldToLocalMatrix*transform.localToWorldMatrix,original=v,indices=ids,bmin=bounds.min,bmax=bounds.max});
   }
   if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter rounded snow: "+patches.Count+" continuous surface grids.");
  }
  void Update(){
   amount=Mathf.Min(1,amount+Time.deltaTime*(.25f+WinterWeather.Intensity)/240);
   foreach(var p in patches)p.renderer.enabled=p.source!=null&&p.source.enabled&&amount>.025f;
   // 0.3.31: the mesh was rebuilt for every shard once a second (seconds-long freezes). Now it is rebuilt only when the
   // snow depth crosses a 5% step, one shard per frame, and never again once the snow is fully grown.
   int step=(int)(Mathf.Min(1,amount)*20);
   if(step!=builtStep&&pending==0){builtStep=step;builtAmount=step/20f;pending=patches.Count;ApplyFrost(builtAmount);}
   if(pending==0)return;
   {WinterFrameWatch.RebuildFrame=Time.frameCount;var p=patches[patches.Count-pending];pending--;var vertices=new Vector3[p.surface.Length];var covered=new bool[vertices.Length];var triangles=new List<int>();
    for(int z=0;z<p.n;z++)for(int x=0;x<p.n;x++){
     int i=z*p.n+x;float growth=Mathf.SmoothStep(0,1,Mathf.Clamp01((builtAmount-p.variation[i]*.48f)*2.3f));
     float edge=1;for(int dz=-2;dz<=2;dz++)for(int dx=-2;dx<=2;dx++){int xx=x+dx,zz=z+dz;if(xx<0||zz<0||xx>=p.n||zz>=p.n||!p.valid[zz*p.n+xx])edge=Mathf.Min(edge,Mathf.Sqrt(dx*dx+dz*dz)/3f);}
     float thick=.008f+growth*(.05f+.08f*p.variation[i])*Mathf.SmoothStep(0,1,edge);bool inner=p.valid[i]&&Inner(p,x,z);Vector3 lift=inner?GridNormal(p,x,z):Vector3.up;vertices[i]=p.toLocal.MultiplyPoint3x4(p.surface[i]+lift*thick);covered[i]=inner&&growth>.015f;
    }
    for(int z=0;z<p.n-1;z++)for(int x=0;x<p.n-1;x++){int a=z*p.n+x,b=a+p.n;
     if(covered[a]&&covered[b]&&covered[a+1]){triangles.Add(a);triangles.Add(b);triangles.Add(a+1);}
     if(covered[a+1]&&covered[b]&&covered[b+1]){triangles.Add(a+1);triangles.Add(b);triangles.Add(b+1);}
    }
    SmoothRim(p,vertices,triangles);p.mesh.Clear();p.mesh.vertices=vertices;p.mesh.SetTriangles(triangles,0);p.mesh.RecalculateNormals();p.mesh.RecalculateBounds();
   }
  }
  void RegisterFrost(Renderer source){
   try{Material m=source.material;string prop=null;foreach(string name in new string[]{"_Color","_TintColor","_BaseColor"})if(m.HasProperty(name)){prop=name;break;}
    if(prop==null){if(frostMats.Count==0&&SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter frost: shard shader "+m.shader.name+" has no known colour property; tint skipped.");return;}
    if(frostMats.Count==0&&SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter frost: shard shader "+m.shader.name+", colour property "+prop);
    frostMats.Add(m);frostOrig.Add(m.GetColor(prop));frostProp.Add(prop);}catch(Exception e){if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter frost unavailable: "+e.Message);}
  }
  // Uncovered faces get a light icy tint as the snow grows (a lerp of the game colour, never a replacement).
  void ApplyFrost(float depth){
   Color ice=new Color(.80f,.92f,1f);float k=.38f*Mathf.SmoothStep(0,1,depth);
   for(int i=0;i<frostMats.Count;i++){Material m=frostMats[i];if(m==null)continue;Color o=frostOrig[i];Color c=Color.Lerp(o,ice,k);c.a=o.a;m.SetColor(frostProp[i],c);}
  }
  // 0.3.37: snow stays one grid cell inside the face edges and lifts along the surface normal, so it no longer overhangs or hangs when seen from the side.
  static bool Inner(Patch p,int x,int z){if(x<1||z<1||x>=p.n-1||z>=p.n-1)return false;for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)if(!p.valid[(z+dz)*p.n+x+dx])return false;return true;}
  static Vector3 GridNormal(Patch p,int x,int z){float sx=Mathf.Max(.0001f,(p.bmax.x-p.bmin.x)/(p.n-1)),sz=Mathf.Max(.0001f,(p.bmax.z-p.bmin.z)/(p.n-1));float hx=(p.surface[z*p.n+x+1].y-p.surface[z*p.n+x-1].y)/(2*sx),hz=(p.surface[(z+1)*p.n+x].y-p.surface[(z-1)*p.n+x].y)/(2*sz);return new Vector3(-hx,1,-hz).normalized;}
  static void SmoothRim(Patch p,Vector3[] vertices,List<int> triangles){
   var edges=new Dictionary<long,int>();
   for(int i=0;i<triangles.Count;i+=3)for(int j=0;j<3;j++){int a=triangles[i+j],b=triangles[i+(j+1)%3];long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);int count;edges.TryGetValue(key,out count);edges[key]=count+1;}
   var neighbors=new Dictionary<int,List<int>>();
   foreach(var e in edges)if(e.Value==1){int a=(int)(e.Key>>32),b=(int)e.Key;List<int> list;if(!neighbors.TryGetValue(a,out list)){list=new List<int>();neighbors[a]=list;}list.Add(b);if(!neighbors.TryGetValue(b,out list)){list=new List<int>();neighbors[b]=list;}list.Add(a);}
   Matrix4x4 inverse=p.toLocal.inverse;
   for(int pass=0;pass<3;pass++){var updates=new Dictionary<int,Vector3>();foreach(var entry in neighbors){Vector3 old=inverse.MultiplyPoint3x4(vertices[entry.Key]),average=Vector3.zero;foreach(int n in entry.Value)average+=inverse.MultiplyPoint3x4(vertices[n]);average/=entry.Value.Count;Vector3 candidate=Vector3.Lerp(old,average,.45f);float height=HeightAt(p,candidate.x,candidate.z),oldHeight=HeightAt(p,old.x,old.z);if(float.IsNegativeInfinity(height)||float.IsNegativeInfinity(oldHeight))continue;candidate.y=height+Mathf.Max(.008f,old.y-oldHeight);updates[entry.Key]=p.toLocal.MultiplyPoint3x4(candidate);}foreach(var entry in updates)vertices[entry.Key]=entry.Value;}
  }
  static float HeightAt(Patch p,float x,float z){float fx=(x-p.bmin.x)/Mathf.Max(.0001f,p.bmax.x-p.bmin.x)*(p.n-1),fz=(z-p.bmin.z)/Mathf.Max(.0001f,p.bmax.z-p.bmin.z)*(p.n-1);if(fx<0||fz<0||fx>=p.n-1||fz>=p.n-1)return float.NegativeInfinity;int ix=(int)fx,iz=(int)fz;float tx=fx-ix,tz=fz-iz;int k=iz*p.n+ix;if(!p.valid[k]||!p.valid[k+1]||!p.valid[k+p.n]||!p.valid[k+p.n+1])return float.NegativeInfinity;float a=Mathf.Lerp(p.surface[k].y,p.surface[k+1].y,tx),b=Mathf.Lerp(p.surface[k+p.n].y,p.surface[k+p.n+1].y,tx);return Mathf.Lerp(a,b,tz);}
  void OnDestroy(){for(int i=0;i<frostMats.Count;i++)if(frostMats[i]!=null){frostMats[i].SetColor(frostProp[i],frostOrig[i]);Destroy(frostMats[i]);}foreach(var p in patches){if(p.renderer!=null)Destroy(p.renderer.gameObject);Destroy(p.mesh);}if(material!=null)Destroy(material);}
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

