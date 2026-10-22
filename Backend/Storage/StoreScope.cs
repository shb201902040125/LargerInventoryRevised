using System;
using System.IO;
using Terraria;

namespace LargerInventory.Backend.Storage;

public enum StoreScope
{
    Player,
    World
}

/// <summary>
/// 作用域 → .LI 文件夹路径。路径跟随对应存档文件（同目录、同名 stem + .LI）。
/// </summary>
internal static class StoreLocator
{
    public static string GetPlayerStoreDirectory(string playerFilePath)
    {
        return GetStoreDirectory(playerFilePath);
    }

    public static string GetWorldStoreDirectory(string worldFilePath)
    {
        return GetStoreDirectory(worldFilePath);
    }

    public static string GetActivePlayerStoreDirectory()
    {
        var path = Main.ActivePlayerFileData?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Main.playerPathName;
        }

        return GetStoreDirectory(path);
    }

    public static string GetActiveWorldStoreDirectory()
    {
        var path = Main.ActiveWorldFileData?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Main.worldPathName;
        }

        return GetStoreDirectory(path);
    }

    private static string GetStoreDirectory(string saveFilePath)
    {
        if (string.IsNullOrWhiteSpace(saveFilePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(saveFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Main.SavePath;
        }

        var stem = Path.GetFileNameWithoutExtension(saveFilePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        return Path.Combine(directory, stem + ".LI");
    }
}
