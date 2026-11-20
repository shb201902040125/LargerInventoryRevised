using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ModLoader.IO;
using Terraria.Utilities;

namespace LargerInventory.Backend.Integration;

internal static class StoreIdentity
{
    internal const string PlayerIdKey = "CharacterId";
    private const string MarkerFile = "li-identity";

    internal static Guid ReadPlayerId(TagCompound tag) =>
        Guid.TryParse(tag.GetString(PlayerIdKey), out var id) ? id : Guid.Empty;

    // Read the persisted ModPlayer ID without loading the character or invoking any backup restoration.
    internal static Guid ReadSavedPlayerId(string savePath, bool cloud, string modName)
    {
        var path = Path.ChangeExtension(savePath, ".tplr");
        if (!FileUtilities.Exists(path, cloud))
        {
            return Guid.Empty;
        }

        using var stream = new MemoryStream(FileUtilities.ReadAllBytes(path, cloud), writable: false);
        var root = TagIO.FromStream(stream);
        foreach (var entry in root.GetList<TagCompound>("modData"))
        {
            if (entry.GetString("mod") == modName && entry.GetString("name") == nameof(InventoryPlayer))
            {
                return ReadPlayerId(entry.GetCompound("data"));
            }
        }

        return Guid.Empty;
    }

    internal static string OpenDirectory(string savePath, bool cloud, StoreScope scope, Guid id, Action<string> ensureClosed)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(savePath))
        {
            throw new IOException("Cannot resolve the save path and UUID.");
        }

        if (cloud)
        {
            // Independent local storage for a cloud save. The game alone handles Steam migrations.
            var cloudPath = CloudDirectory(scope, id);
            ensureClosed(cloudPath);
            BindDirectory(cloudPath, scope, id);
            return cloudPath;
        }

        savePath = Path.GetFullPath(savePath);
        var expected = Path.ChangeExtension(savePath, ".LI");
        var existing = FindLocalDirectory(savePath, scope, id);
        ensureClosed(existing ?? expected);
        if (existing is not null && !PathsEqual(existing, expected))
        {
            Directory.Move(existing, expected);
            Log.Info($"{scope} store followed renamed save: {existing} -> {expected}");
        }

        BindDirectory(expected, scope, id);
        return expected;
    }

    private static string CloudDirectory(StoreScope scope, Guid id) =>
        Path.Combine(Main.SavePath, "LargerInventory", "Cloud", scope.ToString(), $"{id:N}.LI");

    internal static string FindCloudDirectory(StoreScope scope, Guid id)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        var directory = CloudDirectory(scope, id);
        if (IsMissing(directory))
        {
            return null;
        }

        RejectReparsePoint(directory);
        return ReadMarker(directory) == IdentityText(scope, id) ? directory : null;
    }

    internal static string ServerPlayerDirectory(string worldStoreDirectory, Guid playerId) =>
        Path.Combine(worldStoreDirectory, "Players", playerId.ToString("N"));

    internal static string FindLocalDirectory(string savePath, StoreScope scope, Guid id)
    {
        var expected = Path.ChangeExtension(savePath, ".LI");
        var knownIdentity = id != Guid.Empty;
        if (!IsMissing(expected))
        {
            RejectReparsePoint(expected);
            if (!Directory.Exists(expected))
            {
                throw new IOException($"Store path is not a directory: {expected}");
            }

            var marker = ReadMarker(expected);
            if (knownIdentity && marker == IdentityText(scope, id))
            {
                return expected;
            }

            throw new IOException($"Store directory has a missing or mismatched identity: {expected}");
        }

        string renamed = null;
        if (knownIdentity)
        {
            foreach (var candidate in Directory.EnumerateDirectories(Path.GetDirectoryName(savePath), "*.LI"))
            {
                if (PathsEqual(candidate, expected) || IsReparsePoint(candidate) || ReadMarker(candidate) != IdentityText(scope, id))
                {
                    continue;
                }

                // A still-present save may be a copy with the same UUID. Preserve its local store.
                var extension = scope == StoreScope.Player ? ".plr" : ".wld";
                var modExtension = scope == StoreScope.Player ? ".tplr" : ".twld";
                if (!IsMissing(Path.ChangeExtension(candidate, extension)) || !IsMissing(Path.ChangeExtension(candidate, modExtension)))
                {
                    continue;
                }

                if (renamed is not null)
                {
                    throw new IOException($"Multiple store directories match {scope} UUID {id:N}.");
                }

                renamed = candidate;
            }
        }

        return renamed;
    }

    internal static bool IsMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string IdentityText(StoreScope scope, Guid id) => $"{scope}:{id:N}";

    private static string ReadMarker(string directory)
    {
        var marker = Path.Combine(directory, MarkerFile);
        if (IsMissing(marker))
        {
            return null;
        }

        RejectReparsePoint(marker);
        return File.ReadAllText(marker).Trim();
    }

    private static void BindDirectory(string directory, StoreScope scope, Guid id)
    {
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        var marker = ReadMarker(directory);
        var identity = IdentityText(scope, id);
        if (marker == identity)
        {
            return;
        }

        if (marker is not null || existed)
        {
            throw new IOException($"Cannot claim store directory {directory} for {identity}.");
        }

        // Publish a complete marker atomically. The final marker is only visible after the rename.
        var temporary = Path.Combine(directory, $"{MarkerFile}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, identity);
        File.Move(temporary, Path.Combine(directory, MarkerFile));
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectReparsePoint(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"Refusing linked store path: {path}");
        }
    }

    internal static void RecycleDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!directory.EndsWith(".LI", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Refusing to recycle unexpected directory: {directory}");
        }

        RejectReparsePoint(directory);
        if (OperatingSystem.IsWindows())
        {
            RecycleWindows(directory);
        }
        else if (OperatingSystem.IsLinux())
        {
            TrashWithTool("gio", "trash", "--", directory);
        }
        else if (OperatingSystem.IsMacOS())
        {
            TrashWithTool("osascript", "-e", "on run argv", "-e",
                "tell application \"Finder\" to delete (POSIX file (item 1 of argv) as alias)", "-e", "end run", directory);
        }
        else
        {
            throw new PlatformNotSupportedException("No system trash API is available.");
        }

        if (!IsMissing(directory))
        {
            throw new IOException($"System trash did not move {directory}.");
        }
    }

    private static void TrashWithTool(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"{executable} failed with exit code {process.ExitCode}.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RecycleWindows(string directory)
    {
        const uint flags = 0x00080000 | 0x00100000 | 0x0400 | 0x0004; // RECYCLEONDELETE, EARLYFAILURE, NOERRORUI, SILENT
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
        IShellItem item = null;
        try
        {
            operation.SetOperationFlags(flags);
            var shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, IntPtr.Zero, ref shellItemId, out item));
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            if (operation.GetAnyOperationsAborted())
            {
                throw new IOException("The recycle operation was aborted.");
            }
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            Marshal.ReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Only the methods we invoke need signatures. Keep the intervening COM slots in vtable order.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, IntPtr sink);
        void DeleteItems();
        void NewItem();
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }
}
