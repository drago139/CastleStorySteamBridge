using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.93 HIDDEN TEST HELPER (needs [Test] Enabled = true, never on for a normal player). Fills stockpile crates with resources, like the game's own
    // disabled creative sidebar button "Fill selected stockpiles with": Recepteur.WipeAndFill(Ressource). A crate holds ONE resource type, so a stockpile
    // gets every type spread over its crates (or one type when a name is given). Only the host / single player (server) or the map editor does it;
    // crates that already hold something are left alone unless a type is named. Triggers: Ctrl+Shift+F11, Test/AutoFillStockpiles (every 60 s, default
    // off) or the virtual player command "fillstock [Resource]". A map editor stockpile placed in gameobjects.json is filled the same way.
    public sealed partial class SteamBridgePlugin
    {
        static readonly string[] sfTypes = { "StoneBlock", "Stones", "WoodBlock", "PlankBlock", "RawIron", "OrangeCrystal", "BlueCrystal", "Plant", "Rope", "Fabric", "Glass", "Cog", "Bomb" };
        static ConfigEntry<bool> sfAuto; static bool sfBound; static float sfNextAuto;

        void StockFillTick()
        {
            try
            {
                if (!TestOn) return;
                if (!sfBound) { sfBound = true; sfAuto = Config.Bind("Test", "AutoFillStockpiles", false, "Test only (needs Test/Enabled): every 60 s fill the own stockpile crates that are empty with all resource types. Ctrl+Shift+F11 does it once."); }
                float now = Time.realtimeSinceStartup;
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl), shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool combo = ctrl && shift && Input.GetKeyDown(KeyCode.F11);
                bool auto = sfAuto.Value && now >= sfNextAuto;
                if (!combo && !auto) return;
                sfNextAuto = now + 60f;
                if (!(NetworkServer.active || Architecte.editorMode)) { if (combo) status = "Склади наповнює лише хост або редактор."; return; }
                int piles; int crates = StockFill(null, out piles);
                PtLog("STOCKFILL " + (combo ? "hotkey" : "auto") + " stockpiles=" + piles + " crates filled=" + crates);
                if (combo) status = "Склади: " + piles + ", наповнено ящиків: " + crates + ".";
            }
            catch (Exception e) { PtLog("STOCKFILL failed: " + e.GetType().Name + " " + e.Message); }
        }

        // returns the number of crates filled; piles = stockpiles looked at
        static int StockFill(string only, out int piles)
        {
            piles = 0; int crates = 0, k = 0; bool editor = Architecte.editorMode;
            User u = User.LocalUser; Faction local = u == null ? null : u.faction;
            foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
            {
                if (loc == null || loc.Storage == null) continue;
                AffiliationComponent a = loc.GetComponent<AffiliationComponent>();
                if (!editor && local != null && a != null && a.faction != null && a.faction != local) continue;
                piles++;
                foreach (Recepteur r in loc.Storage.GetRecepteurs())
                {
                    if (r == null) continue;
                    string type = only ?? sfTypes[k++ % sfTypes.Length];
                    if (only == null && !r.IsEmpty()) continue;
                    Ressource res = Adjectif.StringToAdjectif(type) as Ressource;
                    if (res == null) { PtLog("STOCKFILL unknown resource '" + type + "'"); return crates; }
                    r.WipeAndFill(res); crates++;
                }
            }
            return crates;
        }
    }
}
