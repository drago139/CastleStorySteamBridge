using System;
using System.Collections.Generic;
using System.Text;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Factories;
using Brix.Game.Network;
using Brix.Legacy;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.93 virtual player door commands (test only, [Test] Enabled). They do what the player does with the remover tool on a door: the tool turns
    // the door's asset key into its blueprint key (BlueprintAssetKeyResolver.Resolve), holds that blueprint over the door and on click calls
    // UNetBlueprint.CmdPlaceAnti(factory, name, position, rotation, instant=false, buildProject): the host spawns an anti blueprint and adds it to the
    // build project, then a worker is sent to break it (the path DoorDemolitionFix patches). The project is an own BuildGoalProvider; if none exists
    // yet (the game creates one when the first blueprint is placed) the command says so and does nothing.
    public sealed partial class SteamBridgePlugin
    {
        static List<Porte> VpDoorsByDistance()
        {
            Vector3 an = VpAnchor(); List<Porte> list = new List<Porte>();
            foreach (Porte d in UnityEngine.Object.FindObjectsOfType<Porte>()) if (d != null && d.gameObject.activeInHierarchy) list.Add(d);
            list.Sort(delegate(Porte x, Porte y) { return (x.transform.position - an).sqrMagnitude.CompareTo((y.transform.position - an).sqrMagnitude); });
            return list;
        }

        static void VpListDoors()
        {
            List<Porte> doors = VpDoorsByDistance();
            for (int i = 0; i < doors.Count && i < 30; i++)
            {
                Porte d = doors[i]; AffiliationComponent a = d.GetComponent<AffiliationComponent>(); Vector3 p = d.transform.position;
                VpLog("#" + vpDone + " doors[" + i + "]=" + d.name + " pos=" + CsV(p) + " faction=" + (a != null && a.faction != null ? a.faction.name : "none") + " volumeRegistered=" + (d.volume != null && d.volume.Registered));
            }
            if (doors.Count == 0) VpLog("#" + vpDone + " doors: none in the world");
        }

        static void VpBreakDoor(int index)
        {
            List<Porte> doors = VpDoorsByDistance();
            if (index >= doors.Count) { VpLog("#" + vpDone + " breakdoor: door " + index + " does not exist (have " + doors.Count + ")"); return; }
            User u = User.LocalUser; UNetBlueprint bp = u == null ? null : u.GetComponent<UNetBlueprint>();
            if (bp == null) { VpLog("#" + vpDone + " breakdoor: no UNetBlueprint on the local user"); return; }
            Porte door = doors[index]; FactoryImprint imprint = door.GetComponentInParent<FactoryImprint>();
            if (imprint == null) { VpLog("#" + vpDone + " breakdoor: " + door.name + " has no FactoryImprint"); return; }
            Factory.AssetKey key = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
            if (key == null) { VpLog("#" + vpDone + " breakdoor: no blueprint key for " + door.name); return; }
            BuildGoalProvider project = null;
            foreach (BuildGoalProvider b in UnityEngine.Object.FindObjectsOfType<BuildGoalProvider>()) if (b != null && u.faction != null && b.faction == u.faction) { project = b; break; }
            if (project == null) { VpLog("#" + vpDone + " breakdoor: no own build project yet (place any blueprint once, then retry)"); return; }
            bp.CallCmdPlaceAnti(key.Factory, key.Name, imprint.transform.position, imprint.transform.rotation, false, project.gameObject);
            VpLog("#" + vpDone + " breakdoor sent: " + door.name + " at " + CsV(door.transform.position) + " key=" + key.Factory + "/" + key.Name + " project=" + project.name);
        }
    }
}
