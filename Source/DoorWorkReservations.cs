using System.Collections.Generic;
// A shared demolition goal has one worker, as DigGoal.HasOtherWorkerAssigned requires.
// Ownership tokens prevent an old cancelled task from releasing its replacement.
internal sealed class DoorWorkReservations
{
    readonly Dictionary<int,object> owners=new Dictionary<int,object>();
    internal object Owner(int target) { object owner;return owners.TryGetValue(target,out owner)?owner:null; }
    internal bool TryAcquire(int target,object token)
    {
        object owner;
        if(owners.TryGetValue(target,out owner))return object.ReferenceEquals(owner,token);
        owners.Add(target,token);return true;
    }
    internal void Release(int target,object token)
    {
        object owner;
        if(owners.TryGetValue(target,out owner)&&object.ReferenceEquals(owner,token))owners.Remove(target);
    }
    internal void Clear() { owners.Clear(); }
}
