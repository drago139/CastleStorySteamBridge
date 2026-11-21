namespace CastleStorySteam
{
    // 0.3.77 B-043 host-side filter, pure decision part (unit-tested). The host simulates every faction's workers, so the base game draws the
    // yellow "to be broken/repaired" outline of EVERY faction on the host. A marker is skipped only when this machine is the server, the filter is
    // on, the local faction and the order's faction are both known, and the order belongs to neither the local faction nor an ally.
    public static class HostMarkerRules
    {
        public static bool ShouldSkip(bool enabled, bool serverActive, bool haveLocal, bool haveOwner, bool ownerIsLocal, bool ownerIsAllied)
        {
            if (!enabled || !serverActive || !haveLocal || !haveOwner) return false;
            return !(ownerIsLocal || ownerIsAllied);
        }
    }
}
