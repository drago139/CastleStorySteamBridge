using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Signals;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Legacy;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

// Extra approaches exist only while a worker executes a native door demolition node.
// The original work node retains its damage, animation, navigation and cleanup rules.
internal static class DoorDemolitionFix
{
    sealed class Work
    {
        internal Labor Worker; internal GameComponent Target;
        internal int WorkerId,TargetId;
        internal bool TargetReleased;
        internal SignalConnection ReleaseConnection=SignalConnection.Empty;
    }
    static readonly Dictionary<int, Work> active = new Dictionary<int, Work>();
    static readonly DoorWorkReservations reservations=new DoorWorkReservations();
    [ThreadStatic] static GameObject boundsTarget;
    static Func<bool> enabled;
    static Action<string> note;
    static readonly HashSet<int> detailed = new HashSet<int>();
    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> logger)
    {
        enabled=isEnabled; note=logger;
        harmony.Patch(AccessTools.Method(typeof(CombatNodes),"BreakObject",new Type[]{typeof(Labor),typeof(GameComponent)}),null,H("AfterBreak"),null,null,null);
        harmony.Patch(AccessTools.Method(typeof(ObjectLocation),"TargetReached"),null,H("AfterTargetReached"),null,null,null);
        harmony.Patch(AccessTools.Method(typeof(Labor),"CanInteract",new Type[]{typeof(GameComponent)}),null,H("AfterCanInteract"),null,null,null);
        harmony.Patch(AccessTools.Method(typeof(SearchPathRequest),"InitDirectSearch"),H("BeforeSearchBounds"),null,null,H("AfterSearchBounds"),null);
        harmony.Patch(AccessTools.Method(typeof(IVolumeExtender),"ComputeBounds"),null,H("AfterBounds"),null,null,null);
    }
    static HarmonyMethod H(string name) { return new HarmonyMethod(AccessTools.Method(typeof(DoorDemolitionFix),name)); }
    static bool Eligible(GameComponent target)
    {
        return enabled!=null&&enabled()&&NetworkServer.active&&target!=null&&target.GetComponent<Porte>()!=null&&
            target.volume!=null&&target.volume.Registered&&target.gameObject.activeInHierarchy;
    }
    static bool InWork(GameObject worker,GameObject target)
    {
        if(worker==null||target==null)return false;
        Work work;
        return active.TryGetValue(worker.GetInstanceID(),out work)&&work.Worker!=null&&work.Target!=null&&
            work.Target.gameObject==target&&Eligible(work.Target);
    }
    static Node Begin(Work work)
    {
        if(TargetGone(work))return Node.Empty;
        if(work.Worker==null)return Node.FailWork<Exceptions.NullLabor>();
        if(!work.Worker.Profession.CanBreakVoxels())return Node.FailWork<Exceptions.CharacterCantDig>();
        Work old=reservations.Owner(work.TargetId) as Work;
        if(old!=null&&(TargetGone(old)||old.Worker==null||old.Worker.IsNullOrReleased()))End(old);
        if(!reservations.TryAcquire(work.TargetId,work))
        {
            if(note!=null)note("Door demolition: already assigned to another worker; extra order rejected before movement.");
            return Node.FailWork<Exceptions.GoalTooCrowded>();
        }
        active[work.WorkerId]=work;
        work.ReleaseConnection=work.Target.volume.OnReleaseOrDestroySignal.ConnectOnce((GameObject released)=>work.TargetReleased=true);
        if(note!=null)note("Door demolition: native work can approach free side cells for "+work.Target.name);
        DescribeSides(work);
        return Node.Empty;
    }
    static void End(Work work)
    {
        work.ReleaseConnection.Disconnect();
        reservations.Release(work.TargetId,work);
        Work current;
        if(active.TryGetValue(work.WorkerId,out current)&&ReferenceEquals(current,work))active.Remove(work.WorkerId);
    }
    static void AfterBreak(Labor labor,GameComponent gameComponent,ref Node __result)
    {
        if(__result==null||labor==null||!Eligible(gameComponent))return;
        Work work=new Work{Worker=labor,Target=gameComponent,WorkerId=labor.gameObject.GetInstanceID(),TargetId=gameComponent.gameObject.GetInstanceID()};
        Node nativeWork=new ConditionNode.ConditionalNodeBuilder(__result,true,false,null).Condition((Func<Work,bool>)TargetGone,work).ToNode;
        __result=Node.Sequence.Do((Func<Work,Node>)Begin,work).Do((Func<Node,Node>)Identity,nativeWork).Finally((Action<Work>)End,work).ToNode;
    }
    static Node Identity(Node node) { return node; }
    static bool TargetGone(Work work) { return work.TargetReleased||work.Target==null||work.Target.IsNullOrReleased(); }
    static bool SideCell(GameObject worker,GameObject target,XYZ cell)
    {
        if(!InWork(worker,target))return false;
        IVolume volume=target.GetComponent<IVolume>();
        if(volume==null||volume.HasPropertyAt(VProperty.occupy,cell))return false;
        int bottom=int.MaxValue;
        foreach(XYZ occupied in volume.GetCases(VProperty.occupy))if(occupied.y<bottom)bottom=occupied.y;
        if(cell.y!=bottom)return false;
        bool adjacent=false;
        foreach(XYZ occupied in volume.GetCases(VProperty.occupy))
            if(DoorApproachRules.FaceAdjacent(cell.x,cell.y,cell.z,occupied.x,occupied.y,occupied.z)){adjacent=true;break;}
        // Native walkability allows standing on partial blocks (e.g. steps); a full stone cell still fails.
        // Use the same actor-aware blocker test as navigation rather than rejecting the worker's own volume.
        if(!adjacent||!Voxel.IsOpenAndWalkable(cell))return false;
        if(SearchPathRequest.HasBlockerAt(worker,cell)||SearchPathRequest.HasBlockerAt(worker,cell+XYZ.up))return false;
        return true;
    }
    static void DescribeSides(Work work)
    {
        if(note==null||!detailed.Add(work.Target.GetInstanceID()))return;
        if(detailed.Count>32)detailed.Clear();
        IVolume volume=work.Target.volume;
        int bottom=int.MaxValue;
        foreach(XYZ occupied in volume.GetCases(VProperty.occupy))if(occupied.y<bottom)bottom=occupied.y;
        HashSet<XYZ> cells=new HashSet<XYZ>();
        XYZ[] directions=new XYZ[]{new XYZ(1,0,0),new XYZ(-1,0,0),new XYZ(0,0,1),new XYZ(0,0,-1)};
        foreach(XYZ occupied in volume.GetCases(VProperty.occupy))if(occupied.y==bottom)
            foreach(XYZ direction in directions)if(!volume.HasPropertyAt(VProperty.occupy,occupied+direction))cells.Add(occupied+direction);
        string text="Door side candidates: "+work.Target.name+" bottom="+bottom+" worker="+work.Worker.voxel.position;
        foreach(XYZ cell in cells)text+=" ["+cell+" open="+Voxel.IsOpenAndWalkable(cell)+" blocker="+SearchPathRequest.HasBlockerAt(work.Worker.gameObject,cell)+" allowed="+SideCell(work.Worker.gameObject,work.Target.gameObject,cell)+"]";
        note(text);
    }
    static void AfterTargetReached(ObjectLocation __instance,GameObject source,Vector3 position,ref bool __result)
    {
        if(!__result&&__instance!=null)__result=SideCell(source,__instance.Object,XYZ.FromVector3(position));
    }
    static void AfterCanInteract(Labor __instance,GameComponent b,ref bool __result)
    {
        if(!__result&&__instance!=null&&b!=null)__result=SideCell(__instance.gameObject,b.gameObject,__instance.voxel.position);
    }
    static void BeforeSearchBounds(SearchPathRequest __instance,out GameObject __state)
    {
        __state=boundsTarget; boundsTarget=null;
        if(__instance!=null&&__instance.Location is ObjectLocation&&__instance.Navigation!=null&&
            InWork(__instance.Navigation.gameObject,__instance.Location.Object))boundsTarget=__instance.Location.Object;
    }
    static Exception AfterSearchBounds(Exception __exception,GameObject __state) { boundsTarget=__state; return __exception; }
    static void AfterBounds(IVolume v,ref VoxelBounds __result)
    {
        if(boundsTarget==null||v==null||v.GameObject!=boundsTarget)return;
        // Keep the added approaches in the finest pathfinding level, including both ends of the two-cell door.
        XYZ margin=new XYZ(1,0,1);
        __result=new VoxelBounds(__result.Min-margin,__result.Max+margin);
    }
    internal static void Clear()
    {
        foreach(Work work in active.Values)work.ReleaseConnection.Disconnect();
        active.Clear();reservations.Clear();detailed.Clear();boundsTarget=null;enabled=null;note=null;
    }
}
