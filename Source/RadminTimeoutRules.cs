using System;
namespace CastleStorySteam
{
    // 0.4.26: which UNet disconnect timeout a direct (non-bridge) join uses. A mod host hosts with the long timeout and UNet refuses a guest whose
    // connection config differs (NetworkError.CRCMismatch, B-035). An address typed into the F8 Radmin join box gets the long timeout by itself
    // (typed host = mod host, any IPv4, no cfg edit, a new host no longer fails); the "host without the mod" tick keeps the game's own value.
    // Any other 26.x (Radmin) join, e.g. from the game's own LAN list, uses the long timeout only for hosts listed in [Network] RadminModHosts.
    // The typed choice is consumed by the first connect after it was armed (UseLongOnce), so it cannot leak into a later join (review 2026-11-09:
    // the statics were never cleared). The caller canonicalises the typed address (IPAddress.ToString) because the game hands RakNet's print of
    // the address to the connect hook and the compare is exact. Pure logic: tested without the game.
    internal static class RadminTimeoutRules
    {
        internal static bool UseLong(string address, string joinTarget, bool joinVanilla, string listedHosts)
        {
            if (address == null) return false;
            if (joinTarget != null && String.Equals(joinTarget, address, StringComparison.Ordinal)) return !joinVanilla;   // typed in the F8 box: the tick decides
            if (!address.StartsWith("26.", StringComparison.Ordinal)) return false;
            return IsListed(address, listedHosts);
        }

        // One connect only: reads and clears the typed choice whatever the address is (the F8 button arms it right before the join it starts).
        internal static bool UseLongOnce(string address, ref string joinTarget, ref bool joinVanilla, string listedHosts)
        {
            string target = joinTarget; bool vanilla = joinVanilla;
            joinTarget = null; joinVanilla = false;
            return UseLong(address, target, vanilla, listedHosts);
        }

        internal static bool IsListed(string address, string listedHosts)
        {
            if (String.IsNullOrEmpty(address) || String.IsNullOrEmpty(listedHosts)) return false;
            foreach (string part in listedHosts.Split(',', ' ', ';'))
            {
                string ip = part.Trim(); int colon = ip.IndexOf(':'); if (colon > 0) ip = ip.Substring(0, colon);
                if (ip.Length > 0 && String.Equals(ip, address, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
