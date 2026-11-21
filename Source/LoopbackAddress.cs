using System.Net;
namespace CastleStorySteam
{
    internal static class LoopbackAddress
    {
        internal static bool IsLoopback(string address)
        {
            IPAddress ip;
            if (address == null || !IPAddress.TryParse(address, out ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            // UNET on this machine returns IPv4-mapped IPv6. Mono 2.0 has no MapToIPv4.
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 16 || bytes[10] != 255 || bytes[11] != 255 || bytes[12] != 127) return false;
            for (int i = 0; i < 10; i++) if (bytes[i] != 0) return false;
            return true;
        }
    }
}
