using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LargerInventory.Backend.Storage;

/// <summary>A renamed, fully staged directory is the commit point. Replay can be repeated after any interruption.</summary>
internal sealed class StoreRedoJournal
{
    private readonly string _root;
    private readonly string _pending;
    private readonly SemaphoreSlim _io = new(1);
    internal const string LedgerName = "transaction.ledger";
    internal const int MaxLedgerBytes = 32 * 1024 * 1024;

    public StoreRedoJournal(string root)
    {
        _root = root;
        _pending = Path.Combine(root, ".commit-pending");
    }

    internal readonly record struct Write(string Name, byte[] Bytes); // null bytes delete the target

    internal readonly struct CommitResult
    {
        private readonly IReadOnlyList<string> _warnings;

        internal CommitResult(Exception publicationError, IReadOnlyList<string> warnings)
        {
            PublicationError = publicationError;
            _warnings = warnings;
        }

        public Exception PublicationError { get; }
        public bool Published => PublicationError is null;

        // Invoke on the caller thread after retaining the commit result.
        public void ReportDiagnostics()
        {
            ReportWarnings(_warnings);
            if (PublicationError is not null)
            {
                try { Log.Error("Store commit is durable and awaits replay.", PublicationError); }
                catch (Exception) { } // A logger failure must not change the durable outcome.
            }
        }
    }

    /// <summary>
    /// Copies the staged bytes before returning and runs the journal I/O on the thread pool.
    /// A completed result is durable, even when publication failed. I/O faults before the marker fault the task.
    /// </summary>
    public Task<CommitResult> CommitAsync(IReadOnlyList<Write> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var count = writes.Count;
        ValidateWriteCount(count);
        var snapshot = new Write[count];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var write = writes[i];
            snapshot[i] = new Write(write.Name, (byte[])write.Bytes?.Clone());
        }
        return CommitPreparedAsync(() => snapshot);
    }

    /// <summary>Prepares writes from owned snapshots after replay and under the same journal gate as publication.</summary>
    internal async Task<CommitResult> CommitPreparedAsync(Func<IReadOnlyList<Write>> prepare)
    {
        // Wait for the journal before scheduling disk work so queued callers use no worker thread.
        await _io.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => CommitCore(prepare, new List<string>())).ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    public bool Commit(IReadOnlyList<Write> writes)
    {
        ValidateWriteCount(writes.Count);
        return CommitPrepared(() => writes);
    }

    internal bool CommitPrepared(Func<IReadOnlyList<Write>> prepare)
    {
        var warnings = new List<string>();
        CommitResult result;
        try
        {
            _io.Wait();
            try { result = CommitCore(prepare, warnings); }
            finally { _io.Release(); }
        }
        catch
        {
            ReportWarnings(warnings);
            throw;
        }
        result.ReportDiagnostics();
        return result.Published;
    }

    private static void ValidateWriteCount(int count)
    {
        if (count < 1 || count > PageDirectory.MaxPageCount * 2 + 3)
            throw new ArgumentException("Invalid store commit entry count.", "writes");
    }

    private CommitResult CommitCore(Func<IReadOnlyList<Write>> prepare, List<string> warnings)
    {
        RecoverCore(warnings);
        var writes = prepare();
        if (writes.Count == 0)
            return new CommitResult(null, warnings); // Replay-only save.
        ValidateWriteCount(writes.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in writes)
        {
            ValidateTarget(write.Name);
            if (!names.Add(write.Name))
                throw new ArgumentException("Duplicate store commit target.", nameof(writes));
        }
        var stage = Path.Combine(_root, ".commit-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using (var manifest = new FileStream(Path.Combine(stage, "manifest"), FileMode.CreateNew, FileAccess.Write))
            using (var writer = new BinaryWriter(manifest))
            {
                writer.Write(1);
                writer.Write(writes.Count);
                for (var i = 0; i < writes.Count; i++)
                {
                    var write = writes[i];
                    ValidateTarget(write.Name);
                    writer.Write(write.Name);
                    writer.Write(write.Bytes is not null);
                    if (write.Bytes is not null)
                    {
                        writer.Write(write.Bytes.Length);
                        writer.Write(Crc32.Compute(write.Bytes, 0, write.Bytes.Length));
                        WriteNew(Path.Combine(stage, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), write.Bytes);
                    }
                }
                writer.Flush();
                manifest.Flush(true);
            }
            Directory.Move(stage, _pending);
        }
        catch
        {
            Cleanup(stage, warnings);
            throw;
        }

        // Once pending exists, the operation is committed even if a target is temporarily locked.
        try
        {
            RecoverCore(warnings);
            return new CommitResult(null, warnings);
        }
        catch (Exception e)
        {
            return new CommitResult(e, warnings);
        }
    }

    public void Recover()
    {
        var warnings = new List<string>();
        _io.Wait();
        try { RecoverCore(warnings); }
        finally
        {
            _io.Release();
            ReportWarnings(warnings);
        }
    }

    private void RecoverCore(List<string> warnings)
    {
        var manifestPath = Path.Combine(_pending, "manifest");
        try
        {
            if ((File.GetAttributes(_pending) & FileAttributes.Directory) == 0)
            {
                throw new IOException("Commit marker must be a directory.");
            }
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        using (var reader = new BinaryReader(File.OpenRead(manifestPath)))
        {
            if (reader.ReadInt32() != 1)
                throw new InvalidDataException("Unsupported store commit journal version.");
            var count = reader.ReadInt32();
            if (count < 1 || count > PageDirectory.MaxPageCount * 2 + 3)
                throw new InvalidDataException("Invalid store commit entry count.");
            var entries = new (string Target, string Payload, int Length, uint Crc)[count];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                ValidateTarget(name);
                if (!names.Add(name))
                    throw new InvalidDataException("Duplicate store commit target.");
                var target = Path.Combine(_root, name);
                entries[i] = reader.ReadBoolean()
                    ? (target, Path.Combine(_pending, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), reader.ReadInt32(), reader.ReadUInt32())
                    : (target, null, 0, 0);
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing store commit data.");

            // Validate the full set before applying any file, then atomically publish each target.
            foreach (var entry in entries)
            {
                if (entry.Payload is not null)
                    ReadPayload(entry.Payload, entry.Length, entry.Crc);
            }
            foreach (var entry in entries)
            {
                if (entry.Payload is null)
                    File.Delete(entry.Target);
                else
                    Replace(entry.Target, ReadPayload(entry.Payload, entry.Length, entry.Crc));
            }
        }

        // Never recursively delete the active marker: a partial deletion would destroy a replayable journal.
        var complete = Path.Combine(_root, ".commit-complete-" + Guid.NewGuid().ToString("N"));
        Directory.Move(_pending, complete);
        Cleanup(complete, warnings);
    }

    private static byte[] ReadPayload(string path, int length, uint crc)
    {
        using var stream = File.OpenRead(path);
        if (length < 0 || stream.Length != length)
            throw new InvalidDataException("Invalid staged store file length.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (Crc32.Compute(bytes, 0, bytes.Length) != crc)
            throw new InvalidDataException("Invalid staged store file checksum.");
        return bytes;
    }

    public static byte[] ReadOptional(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }

    public static void Replace(string target, byte[] bytes)
    {
        var staged = target + ".publish-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteNew(staged, bytes);
            File.Move(staged, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateTarget(string name)
    {
        if (name is "store.meta" or "store.meta.bak" or LedgerName)
            return;
        if (name.StartsWith("pages/", StringComparison.Ordinal))
        {
            var file = name.Substring(6);
            if (file.EndsWith(".bak", StringComparison.Ordinal))
                file = file[..^4];
            if (file.Length == 12 && file.EndsWith(".lipg", StringComparison.Ordinal) &&
                int.TryParse(file.AsSpan(0, 7), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index) && index < PageDirectory.MaxPageCount)
                return;
        }
        throw new InvalidDataException("Invalid store commit target.");
    }

    private static void Cleanup(string directory, List<string> warnings)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warnings.Add("Could not remove inactive store journal directory: " + e.Message);
        }
    }

    private static void ReportWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings is null)
            return;
        foreach (var warning in warnings)
        {
            try { Log.Warn(warning); }
            catch (Exception) { } // Inactive-directory cleanup is best effort.
        }
    }
}
