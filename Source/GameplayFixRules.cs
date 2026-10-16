internal static class GameplayFixRules
{
    internal static int CaptureIncrement(int original, bool owned, bool neutralDecay, bool defendedByOwner, float progress, float maximum)
    {
        return original == 0 && owned && neutralDecay && defendedByOwner && progress > 0 && progress < maximum ? -1 : original;
    }
}
