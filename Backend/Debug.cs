using System;
using Terraria.ModLoader;

namespace LargerInventory.Backend;

public static class Debug
{
    [Flags]
    public enum DebugModeEnum
    {
        Conventional = 1,
        Strict = 2,
        Test = 4,
        Release = 8
    }

    public static DebugModeEnum DebugMode;

    public static bool IsStrict => (DebugMode & DebugModeEnum.Strict) != 0;

    static Debug()
    {
#if DEBUG
        DebugMode = DebugModeEnum.Test | DebugModeEnum.Strict;
#elif RELEASE
        DebugMode = DebugModeEnum.Release | DebugModeEnum.Conventional;
#else
        DebugMode = DebugModeEnum.Test | DebugModeEnum.Conventional;
#endif
    }

    /// <summary>
    /// 表示本模组内部出现了异常，但这不代表该问题一定是本模组导致的
    /// </summary>
    /// <param name="e"></param>
    internal static void Error_Internal(Exception e)
    {
        if (LargerInventory.Instance is null)
        {
            Console.Error.WriteLine("[LI][internal] " + e);
            return;
        }

        LargerInventory.Instance.Logger.Error(
            $"This exception was reported by this mod. but doesn't mean determined to be a problem of this mod.", e);
    }

    /// <summary>
    /// 允许其他模组呈报可能为本模组导致的异常，但这不代表该问题一定是本模组导致的
    /// </summary>
    /// <param name="mod"></param>
    /// <param name="e"></param>
    public static void Error(Mod mod, Exception e)
    {
        LargerInventory.Instance.Logger.Error(
            $"This exception was reported by {mod.DisplayName}. It maybe but cannot be directly determined to be a problem of this mod.",
            e);
    }
}
