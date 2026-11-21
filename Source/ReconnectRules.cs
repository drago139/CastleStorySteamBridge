using System;
using System.Text;

namespace CastleStorySteam
{
    // 0.3.58: the reconnect bookkeeping without Unity/Steam, so ReconnectTests can drive crash, clean exit, host leave and stale ids.
    // Stored as two config values: LastHost = "id;name" and LastHostOpen = true while a joined session is running.
    internal sealed class ReconnectState
    {
        internal ulong HostId;
        internal string HostName = "";
        internal bool Open;

        internal static bool LooksLikeSteamId(ulong id) { return id > 76561190000000000UL && id < 76561300000000000UL; }

        // Persisted names never contain the separator, control characters or '\' (BepInEx unescapes "\n"/"\t" when it reads the config back).
        internal static string CleanName(string name)
        {
            if (String.IsNullOrEmpty(name) || name == "[unknown]") return "";
            StringBuilder b = new StringBuilder(name.Length);
            foreach (char c in name) b.Append(c == ';' || c == '\\' || Char.IsControl(c) ? ' ' : c);
            return b.ToString().Trim();
        }

        internal static bool TryParse(string value, out ulong id, out string name)
        {
            id = 0; name = "";
            if (String.IsNullOrEmpty(value)) return false;
            int sep = value.IndexOf(';');
            ulong parsed;
            if (!UInt64.TryParse(sep >= 0 ? value.Substring(0, sep) : value, out parsed) || !LooksLikeSteamId(parsed)) return false;
            id = parsed; name = sep >= 0 ? CleanName(value.Substring(sep + 1)) : "";
            return true;
        }

        internal string Serialize() { return HostId == 0 ? "" : HostId + ";" + HostName; }

        // Start of the game. Returns true when the previous run died during a joined session (the open mark survived);
        // the mark is consumed so the prompt is shown once.
        internal bool Load(string value, bool openMark)
        {
            ulong id; string name;
            if (TryParse(value, out id, out name)) { HostId = id; HostName = name; } else { HostId = 0; HostName = ""; }
            Open = false;
            return HostId != 0 && openMark;
        }

        // The game really connected to this host through the bridge.
        internal bool Remember(ulong hostId, string name)
        {
            if (!LooksLikeSteamId(hostId)) return false;
            HostId = hostId; HostName = CleanName(name); Open = true;
            return true;
        }

        // Any orderly end of the session (leave, host closed, kicked, Steam failure seen, normal quit). Keeps the host for the button.
        internal void CleanEnd() { Open = false; }

        internal bool CanOffer(ulong self) { return HostId != 0 && HostId != self; }

        internal string Label(string livePersona)
        {
            string n = HostName;
            if (n.Length == 0) n = CleanName(livePersona);
            return n.Length == 0 ? HostId.ToString() : n;
        }
    }
}
