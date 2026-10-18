using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.FlatWorld;
using Brix.Engine.Environment;
using Brix.Game.Components;
using Brix.Lua;
using Brix.External.Factories;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace CastleStorySteam {
 internal sealed class WinterEvents:MonoBehaviour {
  static Material priorTerrain;static FlatWorldInitializer terrainOwner;
  Harmony patch;float next;WinterWeather weather;
  readonly List<WinterHat> hats=new List<WinterHat>();
  internal static bool Enabled {get{return SeasonalEvents.WinterActive&&VisualOptions.WinterSnow;}}
  void Awake(){patch=new Harmony("local.castlestory.wintervisuals");patch.Patch(AccessTools.Method(typeof(FlatWorldInitializer),"Init"),null,new HarmonyMethod(typeof(WinterEvents),"WinterTerrain"));}
  static void WinterTerrain(){
   if(!Enabled)return;
   try{
    var source=Factory.Peek(Brix.External.Factories.Environment.BiomeIndex).GetComponent<BiomeIndex>();
    foreach(Biome biome in source.Biomes)if(biome.Name=="Winter"){
     // Only the terrain render material changes. Map metadata, voxels and nature colliders remain intact.
     priorTerrain=Voxel.Engine.RenderMaterial;terrainOwner=FlatWorldInitializer.Instance;Voxel.Engine.RenderMaterial=biome.BiomeMat;
     if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter: snow terrain material applied; map data unchanged.");break;
    }
   }catch(Exception e){if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter terrain unavailable: "+e.Message);}
  }
  void Update(){
   // 0.3.34: FindObjectsOfType walks every object of the big world; once the crystal and the hats exist, look for newcomers rarely.
   if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+(hats.Count==0?2:30);
   if(!Enabled||SceneManager.GetActiveScene().name!="ScenePrincipale"){Clear();return;}
   if(weather==null){weather=gameObject.AddComponent<WinterWeather>();}
   hats.RemoveAll(t=>t==null);
   long scanStart=System.Diagnostics.Stopwatch.GetTimestamp();
   foreach(var bag in UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Toolbag>()){
    if(bag.Head==null||bag.Head.Limb==null||bag.GetComponent<Brix.Game.AI.Labor>()==null||bag.GetComponent<WinterHat>()!=null)continue;
    WinterHat h=bag.gameObject.AddComponent<WinterHat>();h.Init(bag);hats.Add(h);
   }
   double scanMs=(System.Diagnostics.Stopwatch.GetTimestamp()-scanStart)*1000.0/System.Diagnostics.Stopwatch.Frequency;
   if(scanMs>=30&&scanLogs++<20&&SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter scan took "+scanMs.ToString("F0")+" ms");
  }
  int scanLogs;
  void Clear(){
   if(terrainOwner!=null&&FlatWorldInitializer.Instance==terrainOwner&&priorTerrain!=null){Voxel.Engine.RenderMaterial=priorTerrain;priorTerrain=null;terrainOwner=null;}
   foreach(var h in hats)if(h!=null)Destroy(h);hats.Clear();
   if(weather!=null)Destroy(weather);
  }
  void OnDestroy(){Clear();if(patch!=null)patch.UnpatchSelf();}
 }

 internal sealed class WinterHomeTree:MonoBehaviour {
  FireflyNest nest;LocalFactionVision vision;GameObject visual;Mesh treeMesh;Vector3[] treePoints;
  readonly List<Renderer> hidden=new List<Renderer>();readonly List<bool> states=new List<bool>();
  readonly List<Renderer> visible=new List<Renderer>();readonly List<Mesh> meshes=new List<Mesh>();readonly List<Material> materials=new List<Material>();
  readonly Dictionary<Mesh,Geometry> shapes=new Dictionary<Mesh,Geometry>();
  Material garlandMaterial;Color appliedTeamColor;bool teamColorApplied;
  const float Height=3.3f;float nextGroundCheck;bool grounded;
  public void Init(FireflyNest target){
   nest=target;vision=GetComponent<LocalFactionVision>();
   foreach(var r in GetComponentsInChildren<Renderer>(true)){hidden.Add(r);states.Add(r.enabled);}
   visual=new GameObject("SteamBridge_NewYearTree");visual.layer=gameObject.layer;visual.transform.SetParent(transform,false);
   GameObject prefab=Factory.Peek(Brix.External.Factories.Environment.Sapin);
   Mesh nativeMesh=prefab.GetComponent<LodMeshFilter>().lod0Mesh;
   Material nativeMaterial=prefab.GetComponent<LodMaterialList>().lod0Mat;
   treeMesh=UnityEngine.Object.Instantiate(nativeMesh);meshes.Add(treeMesh);
   Bounds bounds=treeMesh.bounds;float scale=Height/Mathf.Max(.01f,bounds.size.y);
   treePoints=treeMesh.vertices;
   // Uniform scale preserves the native silhouette, UVs and texture proportions.
   for(int i=0;i<treePoints.Length;i++)treePoints[i]=(treePoints[i]-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z))*scale;
   treeMesh.vertices=treePoints;treeMesh.RecalculateBounds();
   Shader foliageShader=Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");if(foliageShader==null)foliageShader=Shader.Find("Brix/Cutout/VertexLitDoubleSided");
   Material needles=new Material(foliageShader);materials.Add(needles);needles.color=Color.white;
   needles.mainTexture=nativeMaterial.GetTexture("_MainTex");needles.mainTextureScale=nativeMaterial.GetTextureScale("_MainTex");needles.mainTextureOffset=nativeMaterial.GetTextureOffset("_MainTex");
   needles.SetFloat("_Mode",1);needles.SetFloat("_Cutoff",nativeMaterial.GetFloat("_Cutoff"));needles.EnableKeyword("_ALPHATEST_ON");needles.SetOverrideTag("RenderType","TransparentCutout");needles.renderQueue=2450;
   Render("Native pine",treeMesh,needles);
   var wire=new Geometry();var bulbs=new Geometry();var red=new Geometry();var gold=new Geometry();var blue=new Geometry();
   // A continuous physical cable, with bulbs attached to it rather than floating particles.
   const int segments=480;Vector3 previous=Vector3.zero;
   for(int i=0;i<=segments;i++){
    float t=(float)i/segments,y=.62f+t*2.48f,a=t*Mathf.PI*2*4.2f;
    float radius=GarlandRadius(y);
    Vector3 point=new Vector3(Mathf.Cos(a)*radius,y,Mathf.Sin(a)*radius);
    if(i>0)wire.Tube(previous,point,.012f,6);
    if(i%10==0){bulbs.Sphere(point,.035f,8,6);gold.Sphere(point+Vector3.up*.028f,.018f,6,4,point);}
    previous=point;
   }
   for(int tier=0;tier<3;tier++)for(int i=0;i<8-tier;i++){
    float y=.86f+tier*.73f,a=i*Mathf.PI*2/(8-tier)+tier*.61f;
    float r=Radius(y)+.07f;Vector3 center=new Vector3(Mathf.Cos(a)*r,y-.055f,Mathf.Sin(a)*r);
    Geometry body=(i+tier)%3==0?gold:((i+tier)%3==1?red:blue);
    body.Sphere(center,.105f,12,8);gold.Sphere(center+Vector3.up*.105f,.026f,8,4,center);
    Vector3 anchor=new Vector3(Mathf.Cos(a)*(Radius(y+.16f)-.04f),y+.16f,Mathf.Sin(a)*(Radius(y+.16f)-.04f));
    gold.Tube(center+Vector3.up*.11f,anchor,.009f,6,center);
   }
   gold.Tube(new Vector3(0,Height-.14f,0),new Vector3(0,Height+.10f,0),.025f,8);
   gold.Star(new Vector3(0,Height+.08f,0),.26f,.115f,.07f);
   Render("Garland cable",wire,Mat(new Color(.11f,.19f,.09f),.2f,.2f));
   Material light=Mat(new Color(1,.76f,.32f),.05f,.45f);light.EnableKeyword("_EMISSION");light.SetColor("_EmissionColor",new Color(1,.60f,.16f)*1.6f);
   garlandMaterial=light;Render("Team garland bulbs",bulbs,light);
   Render("Red baubles",red,Mat(new Color(.67f,.025f,.035f),.45f,.72f));
   Render("Gold baubles and star",gold,Mat(new Color(1,.64f,.12f),.65f,.62f));
   Render("Blue baubles",blue,Mat(new Color(.035f,.25f,.64f),.45f,.7f));
   FitCrystalSize();
   if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter tree redesign: native Sapin uniform scale, bounds="+treeMesh.bounds.size+"; alpha-cut foliage; team-colored continuous garland, 49 bulbs, 21 round baubles, 3D star.");
  }
  void FitCrystalSize(){
   Bounds crystal=new Bounds();bool found=false;
   foreach(var r in hidden){
    if(!(r is MeshRenderer)||!r.gameObject.activeInHierarchy||r.sharedMaterial==null||r.sharedMaterial.renderQueue>2500)continue;
    if(!found){crystal=r.bounds;found=true;}else crystal.Encapsulate(r.bounds);
    if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter size source: "+r.name+" bounds="+r.bounds.size);
   }
   Bounds decorated=new Bounds();bool first=true;
   foreach(var r in visible){if(first){decorated=r.bounds;first=false;}else decorated.Encapsulate(r.bounds);}
   float wantedWidth=7.5f;
   const float targetHeight=15f,ornamentSize=1.3f;
   float vertical=(targetHeight-.26f*ornamentSize)/(Height+.08f);
   // Solve width with round ornaments intact; only their attachment positions stretch.
   float low=.1f,high=8f;
   for(int iteration=0;iteration<16;iteration++){
    float horizontal=(low+high)*.5f;ResizeVisual(new Vector3(horizontal,vertical,horizontal),ornamentSize);
    Bounds fitted=visible[0].bounds;foreach(var r in visible)fitted.Encapsulate(r.bounds);
    if(Mathf.Max(fitted.size.x,fitted.size.z)<wantedWidth)low=horizontal;else high=horizontal;
   }
   ResizeVisual(new Vector3((low+high)*.5f,vertical,(low+high)*.5f),ornamentSize);
   Bounds final=visible[0].bounds;foreach(var r in visible)final.Encapsulate(r.bounds);
   if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter final measured size: "+final.size+" targetWidth="+wantedWidth+"; round ornaments preserved");
  }
  void ResizeVisual(Vector3 stretch,float ornamentSize){
   Vector3[] points=new Vector3[treePoints.Length];for(int i=0;i<points.Length;i++)points[i]=Vector3.Scale(treePoints[i],stretch);
   treeMesh.vertices=points;treeMesh.RecalculateNormals();treeMesh.RecalculateBounds();
   foreach(var pair in shapes)pair.Value.Resize(pair.Key,stretch,ornamentSize);
  }
  float GarlandRadius(float y){
   // The cable follows a smooth envelope over branch tiers, instead of stepping
   // in/out at each polygon boundary. Keep it close to the visible needle edge.
   float sum=0,weights=0;
   for(int i=-12;i<=12;i++){float offset=i*.018f,w=Mathf.Exp(-offset*offset/(2*.105f*.105f));sum+=SectionRadius(Mathf.Clamp(y+offset,.35f,Height-.02f))*w;weights+=w;}
   return Mathf.Max(.025f,sum/weights*.96f+.012f);
  }
  float Radius(float y){return (SectionRadius(y-.045f)+SectionRadius(y)*2+SectionRadius(y+.045f))*.25f;}
  float SectionRadius(float y){
   float radius=.025f;int[] indices=treeMesh.triangles;
   for(int i=0;i<indices.Length;i+=3)for(int edge=0;edge<3;edge++){
    Vector3 a=treePoints[indices[i+edge]],b=treePoints[indices[i+(edge+1)%3]];
    if(Mathf.Abs(a.y-b.y)<.0001f||y<Mathf.Min(a.y,b.y)||y>Mathf.Max(a.y,b.y))continue;
    Vector3 p=Vector3.Lerp(a,b,(y-a.y)/(b.y-a.y));radius=Mathf.Max(radius,new Vector2(p.x,p.z).magnitude);
   }
   return radius;
  }
  Material Mat(Color color,float metal,float smooth){var m=new Material(Shader.Find("Standard"));m.color=color;m.SetFloat("_Metallic",metal);m.SetFloat("_Glossiness",smooth);materials.Add(m);return m;}
  void Render(string name,Geometry shape,Material material){Mesh mesh=shape.Build();shapes.Add(mesh,shape);Render(name,mesh,material);}
  void Render(string name,Mesh mesh,Material material){if(!meshes.Contains(mesh))meshes.Add(mesh);var o=new GameObject(name);o.layer=visual.layer;o.transform.SetParent(visual.transform,false);o.AddComponent<MeshFilter>().sharedMesh=mesh;var r=o.AddComponent<MeshRenderer>();r.sharedMaterial=material;visible.Add(r);}
  void UpdateTeamColor(){
   if(garlandMaterial==null||nest==null||nest.faction==null)return;
   Color team=nest.faction.FactionColor;team.a=1;
   if(teamColorApplied&&team==appliedTeamColor)return;
   appliedTeamColor=team;teamColorApplied=true;
   garlandMaterial.color=team;garlandMaterial.SetColor("_EmissionColor",team*1.6f);
   if(SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter garland team color: "+team);
  }
  void GroundVisual(){
   if(visual==null||Time.realtimeSinceStartup<nextGroundCheck)return;nextGroundCheck=Time.realtimeSinceStartup+1;
   RaycastHit hit;
   // Layer 10 is the terrain mask used by the native camera ground check.
   // Ignore crystal/NPC colliders; never move the gameplay object itself.
   if(Physics.Raycast(transform.position+Vector3.up*2,Vector3.down,out hit,10,1024,QueryTriggerInteraction.Ignore)){
    visual.transform.position=new Vector3(transform.position.x,hit.point.y-.04f,transform.position.z);
    if(!grounded&&SeasonalEvents.Log!=null)SeasonalEvents.Log("Winter tree grounded: rootY="+transform.position.y+" groundY="+hit.point.y+" baseY="+visual.transform.position.y);
    grounded=true;
   }
  }
  void LateUpdate(){GroundVisual();UpdateTeamColor();if(nest==null||!nest.isHome||!WinterEvents.Enabled){Destroy(this);return;}foreach(var r in hidden)if(r!=null)r.enabled=false;bool show=vision!=null&&vision.IsVisibleForMe();foreach(var r in visible)if(r!=null)r.enabled=show;}
  void OnDestroy(){for(int i=0;i<hidden.Count;i++)if(hidden[i]!=null)hidden[i].enabled=states[i];if(visual!=null)Destroy(visual);foreach(var m in meshes)if(m!=null)Destroy(m);foreach(var m in materials)if(m!=null)Destroy(m);}
  sealed class Geometry {
   readonly List<Vector3> v=new List<Vector3>();readonly List<Vector3> centers=new List<Vector3>();readonly List<int> triangles=new List<int>();
   void Tri(int a,int b,int c){triangles.Add(a);triangles.Add(b);triangles.Add(c);}
   public void Sphere(Vector3 center,float radius,int sides,int rings,Vector3? attachmentCenter=null){int first=v.Count;for(int j=0;j<=rings;j++){float phi=Mathf.PI*j/rings;for(int i=0;i<=sides;i++){float a=Mathf.PI*2*i/sides;centers.Add(attachmentCenter??center);v.Add(center+radius*new Vector3(Mathf.Sin(phi)*Mathf.Cos(a),Mathf.Cos(phi),Mathf.Sin(phi)*Mathf.Sin(a)));}}for(int j=0;j<rings;j++)for(int i=0;i<sides;i++){int a=first+j*(sides+1)+i,b=a+sides+1;Tri(a,a+1,b);Tri(a+1,b+1,b);}}
   public void Tube(Vector3 a,Vector3 b,float radius,int sides,Vector3? attachmentCenter=null){Vector3 axis=(b-a).normalized,u=Vector3.Cross(axis,Vector3.up).normalized;if(u.sqrMagnitude<.01f)u=Vector3.right;Vector3 w=Vector3.Cross(axis,u);int first=v.Count;for(int k=0;k<2;k++)for(int i=0;i<sides;i++){float angle=2*Mathf.PI*i/sides;centers.Add(k==0?(attachmentCenter??a):b);v.Add((k==0?a:b)+(u*Mathf.Cos(angle)+w*Mathf.Sin(angle))*radius);}for(int i=0;i<sides;i++){int n=(i+1)%sides;Tri(first+i,first+n,first+sides+i);Tri(first+n,first+sides+n,first+sides+i);}}
   public void Star(Vector3 p,float outer,float inner,float depth){int first=v.Count;Quaternion turn=Quaternion.Euler(0,35,0);centers.Add(p);centers.Add(p);v.Add(p+turn*Vector3.forward*depth);v.Add(p-turn*Vector3.forward*depth);for(int i=0;i<10;i++){float a=Mathf.PI/2+i*Mathf.PI/5,r=i%2==0?outer:inner;centers.Add(p);v.Add(p+turn*new Vector3(Mathf.Cos(a)*r,Mathf.Sin(a)*r,0));}for(int i=0;i<10;i++){int a=first+2+i,b=first+2+(i+1)%10;Tri(first,a,b);Tri(first+1,b,a);}}
   public void Resize(Mesh mesh,Vector3 stretch,float size){Vector3[] points=new Vector3[v.Count];for(int i=0;i<v.Count;i++)points[i]=Vector3.Scale(centers[i],stretch)+(v[i]-centers[i])*size;mesh.vertices=points;mesh.RecalculateNormals();mesh.RecalculateBounds();}
   public Mesh Build(){var m=new Mesh();m.SetVertices(v);m.SetTriangles(triangles,0);m.RecalculateNormals();m.RecalculateBounds();return m;}
  }
 }
}
