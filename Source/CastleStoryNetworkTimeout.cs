// Recovered source of the 10-minute patcher shipped in 0.3.3 (runtime.zip / CastleStoryNetworkTimeout-10min.dll).
// The 0.3.3 binary contains exactly this constant (ldc.i4 600000) and log line; 0.3.2 had 30000 ms.
using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class CastleStoryNetworkTimeout
{
    public static IEnumerable<string> TargetDLLs
    {
        get { return new string[] { "UnityEngine.dll" }; }
    }

    public static void Patch(AssemblyDefinition assembly)
    {
        if (assembly.Name.Name != "UnityEngine")
            throw new InvalidOperationException("Unexpected assembly; refusing patch.");
        TypeDefinition config = null;
        foreach (TypeDefinition type in assembly.MainModule.Types)
            if (type.FullName == "UnityEngine.Networking.ConnectionConfig") config = type;
        if (config == null) throw new InvalidOperationException("ConnectionConfig not found.");
        MethodDefinition getter = null;
        foreach (MethodDefinition method in config.Methods)
            if (method.Name == "get_DisconnectTimeout" && method.Parameters.Count == 0) getter = method;
        if (getter == null || !getter.HasBody || getter.ReturnType.FullName != "System.UInt32")
            throw new InvalidOperationException("Unsupported DisconnectTimeout getter.");
        getter.Body = new MethodBody(getter);
        ILProcessor il = getter.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4, 600000));
        il.Append(il.Create(OpCodes.Ret));
        Console.WriteLine("[CastleStoryNetworkTimeout] DisconnectTimeout = 600000ms (both peers must use this patch)");
    }
}
