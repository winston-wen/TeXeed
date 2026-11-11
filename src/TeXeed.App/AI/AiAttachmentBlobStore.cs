using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TeXeed.App.AI;

// Uploaded bytes are immutable and can be shared by saved turns and branches.
internal static class AiAttachmentBlobStore
{
    private static string Root => Path.Combine(AiStorage.Home, "attachments");

    internal static (string Id, long Length) Store(string sourcePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
            {
                var expected = source.Length;
                var buffer = new byte[131072];
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, count);
                    destination.Write(buffer, 0, count);
                    total += count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (total != expected || source.Length != expected) throw new IOException("读取时文件大小发生变化：" + sourcePath);
                destination.Flush(flushToDisk: true);
            }
            var id = Convert.ToHexString(hash.GetHashAndReset());
            var final = BlobPath(id);
            try { File.Move(temporary, final); }
            catch (IOException) when (File.Exists(final))
            {
                using var existing = OpenRead(id, cancellationToken);
            }
            return (id, total);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static FileStream OpenRead(string id, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(BlobPath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("附件快照内容已损坏：" + id);
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static byte[] ReadAllBytes(string id, CancellationToken cancellationToken = default)
    {
        using var stream = OpenRead(id, cancellationToken);
        return ReadAllBytes(stream, cancellationToken);
    }

    internal static byte[] ReadAllBytes(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new IOException("无法读取附件：输入流不支持定位。");
        var length = stream.Length - stream.Position;
        EnsureBufferCapacity(length, AvailableMemory());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(bytes, offset, Math.Min(131072, bytes.Length - offset));
                if (count == 0) throw new EndOfStreamException("读取时附件内容发生变化。");
                offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1) throw new IOException("读取时附件内容发生变化。");
            return bytes;
        }
        catch (OutOfMemoryException ex)
        { throw new IOException("当前可用内存不足，无法准备此附件；附件仍已保留。", ex); }
    }

    internal static void EnsureBufferCapacity(long length, long availableMemory)
    {
        // The request also needs base64, JSON and network buffers alongside the file bytes.
        if (length > Array.MaxLength || length < 0 || availableMemory < 0 || length > availableMemory / 8)
            throw new IOException("当前可用内存不足，无法准备此附件；附件仍已保留。");
    }

    internal static void EnsureBufferCapacity(long length) => EnsureBufferCapacity(length, AvailableMemory());

    private static long AvailableMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var physical = GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue) : long.MaxValue;
        var gcLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (gcLimit <= 0) return physical;
        using var process = Process.GetCurrentProcess();
        return Math.Min(physical, Math.Max(0, gcLimit - process.PrivateMemorySize64));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static string BlobPath(string id)
    {
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("附件快照标识无效。");
        return Path.Combine(Root, id.ToUpperInvariant());
    }
}
