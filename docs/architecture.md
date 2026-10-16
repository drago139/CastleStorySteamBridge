# Architecture overview

`BridgeCore` carries Castle Story's local UNET datagrams through Steam P2P:

- channel **77** is the reliable bridge stream;
- channel **78** carries unit positions as an unreliable low-latency stream;
- the bridge keeps the game-facing local UDP side separate from the Steam transport side.

The mod is unofficial and requires a legitimate Steam copy of Castle Story. The Workshop item is available at:

<https://steamcommunity.com/sharedfiles/filedetails/?id=3818506669>
