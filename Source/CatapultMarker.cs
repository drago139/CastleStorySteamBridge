using System;
using Brix.Game.Components.CatapultSpace;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // Per-control state dies with the catapult. No polling, RPC or SyncVar writes.
    public sealed class ClientCatapultMarker : MonoBehaviour
    {
        bool seen, pending;
        Vector3 target, normal;
        static bool Guest { get { return NetworkClient.active && !NetworkServer.active; } }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(CatapultControl), "OnDeserialize", "Deserialized");
            Patch(harmony, typeof(CatapultBody), "Trans_Initializing_Healthy", "Initialized");
            Patch(harmony, typeof(CatapultBody), "React_Healthy_DeltaTime", "HealthyTick");
        }
        static void Patch(Harmony h, Type type, string method, string callback)
        {
            var m = AccessTools.Method(type, method);
            if (m == null) throw new MissingMethodException(type.FullName, method);
            h.Patch(m, null, new HarmonyMethod(typeof(ClientCatapultMarker), callback));
        }
        static ClientCatapultMarker State(CatapultControl c)
        {
            var state = c.GetComponent<ClientCatapultMarker>();
            return state != null ? state : c.gameObject.AddComponent<ClientCatapultMarker>();
        }
        internal static void Deserialized(CatapultControl __instance)
        {
            if (!Guest || __instance == null) return;
            var s = State(__instance);
            if (!s.seen || s.target != __instance.cible || s.normal != __instance.cibleNormale)
            {
                s.seen = true; s.target = __instance.cible; s.normal = __instance.cibleNormale; s.pending = true;
                if (s.target == Vector3.zero) Hide(__instance.body);
            }
        }
        internal static void Initialized(CatapultBody __instance, bool __result)
        {
            // The transition predicate runs before entry to Healthy. Defer to its next reaction.
            if (Guest && __result && __instance != null && __instance.control != null)
                State(__instance.control).pending = true;
        }
        internal static void HealthyTick(CatapultBody __instance)
        {
            if (!Guest || __instance == null || __instance.control == null) return;
            var c = __instance.control;
            var s = c.GetComponent<ClientCatapultMarker>();
            if (s == null || !s.pending) return;
            s.pending = false;
            if (c.cible == Vector3.zero) Hide(__instance);
            else __instance.HandleMessage(new CatapultState.MessageViser());
        }
        static void Hide(CatapultBody b)
        {
            if (b == null) return;
            b.afficherCible = false;
            if (b.trajetCible != null) b.trajetCible.SetActif(false);
        }
    }
}
