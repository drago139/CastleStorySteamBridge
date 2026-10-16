using System;
internal static class DoorApproachRules
{
    internal static bool FaceAdjacent(int x,int y,int z,int tx,int ty,int tz)
    {
        return y==ty&&Math.Abs(x-tx)+Math.Abs(z-tz)==1;
    }
}
