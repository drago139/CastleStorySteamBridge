namespace CastleStorySteam
{
    // 0.4.19 JOIN-MENUS, pure part (no Unity; tested by JoinMenuTests.cs). The game part is RearmJoinMenus in ServerDiscovery.cs.
    // Right before NetworkWaitingModalMenu.OnJoinSuccess fires OnJoinSuccessSignal, every LegacyMultiplayerMenu of the scene (also inactive ones) is subscribed again,
    // because NetworkWaitingModalMenu.CloseSelf clears that signal after every join (its only listener is LegacyMultiplayerMenu.Connect, run from OnEnable).
    internal enum JoinMenuStep { Skip, Reconnect, Toggle, TempConnect }

    internal static class JoinMenuRules
    {
        internal const int DiagFirst = 10;   // the menu list is written on the first 10 join successes, later only when nothing was reconnected

        // live = isActiveAndEnabled; reflect = the private Connect/Disconnect were found
        internal static JoinMenuStep Step(bool live, bool reflect)
        {
            if (live) return reflect ? JoinMenuStep.Reconnect : JoinMenuStep.Toggle;   // Toggle = the 0.3.8 behaviour (OnDisable/OnEnable)
            return reflect ? JoinMenuStep.TempConnect : JoinMenuStep.Skip;             // inactive: connected for this one signal, disconnected again right after it
        }

        // after the signal: a temporarily connected menu is disconnected; connected again only if it became live meanwhile (its own OnEnable already connected it: avoid a double)
        internal static bool ReconnectAfter(bool liveNow) { return liveNow; }

        internal static bool WantDiag(int joinNumber, int reconnected) { return joinNumber <= DiagFirst || reconnected == 0; }
    }
}
