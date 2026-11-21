using System;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.CatapultSpace;
using Brix.Transactions;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.97: infinite catapult in the game's debug / editor mode ("Режим налагоджування", Architecte.editorMode). The game's catapult fires ONE loaded explosive barrel
    // (CatapultControl.boulet), after that a worker has to fetch and load the next one. In debug mode the host loads a fresh barrel (the game's own ObjetsDynamiques.Baril,
    // spawned for the catapult's faction) into every ready, empty catapult once a second, through the game's own Catapult.Armer. The workers still aim and fire. Outside debug mode
    // nothing changes. Config Fixes/InfiniteCatapultInDebug (default true). Max 2 catapults per second; the first 20 loads are logged (INFINITE-CATAPULT).
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> infCatCfg; float infCatNext; int infCatLogged;

        void InfiniteCatapultTick()
        {
            try
            {
                if (infCatCfg == null) infCatCfg = Config.Bind("Fixes", "InfiniteCatapultInDebug", true, "Host only, only in the game's debug mode (Режим налагоджування): every empty catapult gets a new explosive barrel loaded automatically, so it never runs out of ammunition.");
                if (!infCatCfg.Value || !NetworkServer.active || !Architecte.editorMode || !HpInGame()) return;
                float now = Time.realtimeSinceStartup;
                if (now < infCatNext) return;
                infCatNext = now + 1f;
                int done = 0;
                foreach (Catapult c in UnityEngine.Object.FindObjectsOfType<Catapult>())
                {
                    if (done >= 2) break;
                    if (c == null || c.control == null || !c.control.initialisee || !c.PeutArmer()) continue;
                    AffiliationComponent a = c.GetComponent<AffiliationComponent>();
                    GameObject barrel = Transactor.SpawnUNet(ObjetsDynamiques.Baril, a != null ? a.faction : null, c.transform.position + Vector3.up * 2f, Quaternion.identity);
                    if (barrel == null) continue;
                    c.Armer(barrel); done++;
                    if (infCatLogged++ < 20) PtLog("INFINITE-CATAPULT loaded a barrel into " + c.name + " at " + c.transform.position.x.ToString("F1") + "," + c.transform.position.y.ToString("F1") + "," + c.transform.position.z.ToString("F1"));
                }
            }
            catch (Exception e) { if (infCatLogged++ < 20) PtLog("INFINITE-CATAPULT failed: " + e.GetType().Name + " " + e.Message); }
        }
    }
}
