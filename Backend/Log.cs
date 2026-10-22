using System;
using Terraria.ModLoader;

namespace LargerInventory.Backend;

/// <summary>
/// 统一日志入口：使用 tML 原生 Mod.Logger（log4net.ILog）。
/// 前缀 [LI]；Debug 默认关闭（由 log4net 配置决定）。
/// </summary>
public static class Log
{
    private const string Prefix = "[LI] ";

    private static log4net.ILog Logger => LargerInventory.Instance?.Logger;

    public static bool DebugEnabled => Logger?.IsDebugEnabled ?? false;

    public static void Debug(string message)
    {
        Logger?.Debug(Prefix + message);
    }

    public static void Info(string message)
    {
        Logger?.Info(Prefix + message);
    }

    public static void Warn(string message)
    {
        Logger?.Warn(Prefix + message);
    }

    public static void Error(string message, Exception exception = null)
    {
        if (exception is null)
        {
            Logger?.Error(Prefix + message);
        }
        else
        {
            Logger?.Error(Prefix + message, exception);
        }
    }

    public static void Fatal(string message, Exception exception = null)
    {
        if (exception is null)
        {
            Logger?.Fatal(Prefix + message);
        }
        else
        {
            Logger?.Fatal(Prefix + message, exception);
        }
    }
}
