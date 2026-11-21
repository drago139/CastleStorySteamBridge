using System;
using CastleStorySteam;
class LoopbackAddressTests
{
    static int count;
    static void Check(string address, bool expected)
    {
        count++;
        if (LoopbackAddress.IsLoopback(address) != expected) throw new Exception("loopback mismatch: " + (address ?? "<null>"));
    }
    static void Main()
    {
        Check("127.0.0.1", true);
        Check("::ffff:127.0.0.1", true); // observed native UNET endpoint in A12b
        Check("::FFFF:127.42.3.2", true);
        Check("::ffff:7f00:1", true);
        Check("::1", true);
        Check("127.255.255.254", true);
        Check("26.66.123.105", false);
        Check("192.168.1.10", false);
        Check("::ffff:26.66.123.105", false);
        Check("2001:db8::7f00:1", false);
        Check("127.evil.example", false);
        Check("127.0.0.1.example", false);
        Check("localhost", false);
        Check("", false);
        Check(null, false);
        Console.WriteLine("PASS " + count + " loopback address checks; mapped UNET endpoint and direct-LAN exclusions.");
    }
}
