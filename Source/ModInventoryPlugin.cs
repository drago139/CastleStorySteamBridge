namespace CastleStorySteam
{
    // MOD-INV (0.4.14): read-only views of two private plugin flags for ModInventory's self-off line.
    public sealed partial class SteamBridgePlugin
    {
        internal static bool ModInvPerfTraceOn() { return ptOn; }
        internal static bool ModInvDiagOnly() { return diagOnly; }
    }
}