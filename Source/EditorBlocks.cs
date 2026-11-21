using System;
using System.Reflection;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.98: map editor panel "Блоки" (left column, button B). The game's own block menu only creates blueprints that builders have to build; in the editor there are no builders, so this places a finished
// block at once: Lua calls SB_PlaceBlock("Stone_Brick") (any field name of Brix.External.Factories.Blocks) and the block appears on the cell in front of the surface the screen centre looks at.
// Host / editor only (needs NetworkServer.active). Placed through the game's own BlockEngine.CreateBlock, so it is saved and synchronised like any block.
internal static class EditorBlocks
{
    internal static void Register(Script script)
    {
        script.Globals.Set("SB_PlaceBlock", DynValue.NewCallback((ScriptExecutionContext exec, CallbackArguments args) =>
        {
            bool ok = false;
            try { if (args.Count > 0 && args[0].Type == DataType.String) ok = Place(args[0].String); } catch (Exception) { }
            return DynValue.NewBoolean(ok);
        }, "SB_PlaceBlock"));
    }

    static bool Place(string name)
    {
        if (!NetworkServer.active || string.IsNullOrEmpty(name)) return false;
        FieldInfo f = typeof(Blocks).GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (f == null || f.FieldType != typeof(Factory.AssetKey)) return false;
        Factory.AssetKey key = (Factory.AssetKey)f.GetValue(null);
        BlockInfo info = key.ToBlockInfo(); if (info == null || BrixSingleton<BlockEngine>.Instance == null) return false;
        Camera cam = Camera.main; if (cam == null) return false;
        Vector3 at = cam.transform.position + cam.transform.forward * 30f; RaycastHit hit;
        if (Physics.Raycast(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out hit, 2000f)) at = hit.point + hit.normal * 0.5f;
        BrixSingleton<BlockEngine>.Instance.CreateBlock(XYZ.FromVector3(at), Quaternion.identity, info);
        return true;
    }
}
