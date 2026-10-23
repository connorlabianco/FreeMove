// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FreeMove.IO
{
    /// <summary>A symbolic link or junction: where it points and what kind it is.</summary>
    public class LinkInfo
    {
        public uint Tag { get; set; }
        /// <summary>The target as stored in the link (relative for relative symlinks).</summary>
        public string Target { get; set; }
        public bool IsRelative { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsJunction => Tag == NativeFs.IO_REPARSE_TAG_MOUNT_POINT;
    }

    /// <summary>
    /// File system operations .NET doesn't provide on both target frameworks: reading and creating
    /// symbolic links and junctions, hard link detection, and sparse file copies.
    /// </summary>
    public static class NativeFs
    {
        public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint FILE_SHARE_ALL = 0x7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;
        const int ERROR_MORE_DATA = 234;
        const int SYMLINK_FLAG_RELATIVE = 1;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateSymbolicLink(string link, string target, int flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

        #region Links
        /// <summary>
        /// Reads a symbolic link or junction. Returns null for anything else, including other kinds
        /// of reparse points (cloud placeholders, deduplicated files...), which behave like normal
        /// files and folders and are copied as such.
        /// </summary>
        public static LinkInfo GetLink(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch { return null; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) return null;

            using (SafeFileHandle handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                       FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;
                const int size = 16 * 1024;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buffer, size, out _, IntPtr.Zero))
                        return null;

                    uint tag = (uint)Marshal.ReadInt32(buffer, 0);
                    if (tag != IO_REPARSE_TAG_SYMLINK && tag != IO_REPARSE_TAG_MOUNT_POINT) return null;

                    int substituteOffset = (ushort)Marshal.ReadInt16(buffer, 8);
                    int substituteLength = (ushort)Marshal.ReadInt16(buffer, 10);
                    int printOffset = (ushort)Marshal.ReadInt16(buffer, 12);
                    int printLength = (ushort)Marshal.ReadInt16(buffer, 14);
                    bool relative = false;
                    int pathBuffer = 16;
                    if (tag == IO_REPARSE_TAG_SYMLINK)
                    {
                        relative = (Marshal.ReadInt32(buffer, 16) & SYMLINK_FLAG_RELATIVE) != 0;
                        pathBuffer = 20;
                    }

                    string print = Marshal.PtrToStringUni(IntPtr.Add(buffer, pathBuffer + printOffset), printLength / 2);
                    string substitute = Marshal.PtrToStringUni(IntPtr.Add(buffer, pathBuffer + substituteOffset), substituteLength / 2);
                    string target = !string.IsNullOrEmpty(print) ? print : substitute;
                    if (target.StartsWith(@"\??\")) target = target.Substring(4);

                    return new LinkInfo
                    {
                        Tag = tag,
                        Target = target,
                        IsRelative = relative,
                        IsDirectory = (attributes & FileAttributes.Directory) != 0
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        public static bool IsLink(string path) => GetLink(path) != null;

        /// <summary>
        /// Creates a link at <paramref name="newPath"/> pointing where <paramref name="link"/> points.
        /// Junctions are recreated as directory symbolic links to the same place.
        /// </summary>
        public static void CreateLinkLike(LinkInfo link, string newPath)
        {
            if (!CreateSymbolicLink(newPath, link.Target, link.IsDirectory ? 1 : 0))
                throw new IOException(string.Format("{0}: {1}", newPath, new Win32Exception().Message));
        }

        /// <summary>
        /// Every file under <paramref name="root"/>, without going into symbolic links or junctions
        /// (their contents live elsewhere). File links themselves are listed. When
        /// <paramref name="skipUnreadable"/> is false, an unreadable folder throws.
        /// </summary>
        public static IEnumerable<string> EnumerateFiles(string root, bool skipUnreadable)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception) when (skipUnreadable)
                {
                    continue;
                }

                foreach (string f in files) yield return f;
                foreach (string d in dirs)
                {
                    if (!IsLink(d)) pending.Push(d);
                }
            }
        }
        #endregion

        #region Hard links
        /// <summary>
        /// For a file with more than one hard link, an id shared by all its names; otherwise null.
        /// </summary>
        public static string GetHardLinkId(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info)) return null;
                if (info.NumberOfLinks <= 1) return null;
                return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }
        }

        public static bool TryCreateHardLink(string newFile, string existingFile) =>
            CreateHardLink(newFile, existingFile, IntPtr.Zero);
        #endregion

        #region Sparse files
        [StructLayout(LayoutKind.Sequential)]
        struct FILE_ALLOCATED_RANGE_BUFFER
        {
            public long FileOffset;
            public long Length;
        }

        /// <summary>
        /// Copies a sparse file without filling in its empty regions: the copy is marked sparse and
        /// only the allocated ranges are written. Attributes and times are copied too.
        /// </summary>
        public static void CopySparseFile(string from, string to)
        {
            using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dest = new FileStream(to, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (!DeviceIoControl(dest.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new Win32Exception();

                long length = source.Length;
                dest.SetLength(length);

                foreach (FILE_ALLOCATED_RANGE_BUFFER range in GetAllocatedRanges(source.SafeFileHandle, length))
                {
                    source.Seek(range.FileOffset, SeekOrigin.Begin);
                    dest.Seek(range.FileOffset, SeekOrigin.Begin);
                    var buffer = new byte[1024 * 1024];
                    long remaining = range.Length;
                    while (remaining > 0)
                    {
                        int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) break;
                        dest.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }

            File.SetAttributes(to, File.GetAttributes(from) & ~FileAttributes.ReparsePoint);
            File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(from));
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
        }

        static List<FILE_ALLOCATED_RANGE_BUFFER> GetAllocatedRanges(SafeFileHandle handle, long length)
        {
            var result = new List<FILE_ALLOCATED_RANGE_BUFFER>();
            int itemSize = Marshal.SizeOf(typeof(FILE_ALLOCATED_RANGE_BUFFER));
            IntPtr input = Marshal.AllocHGlobal(itemSize);
            const int maxItems = 512;
            IntPtr output = Marshal.AllocHGlobal(itemSize * maxItems);
            try
            {
                long offset = 0;
                while (offset < length)
                {
                    Marshal.StructureToPtr(new FILE_ALLOCATED_RANGE_BUFFER { FileOffset = offset, Length = length - offset }, input, false);
                    bool ok = DeviceIoControl(handle, FSCTL_QUERY_ALLOCATED_RANGES, input, itemSize, output, itemSize * maxItems, out int returned, IntPtr.Zero);
                    int error = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && error != ERROR_MORE_DATA) throw new Win32Exception(error);

                    int count = returned / itemSize;
                    if (count == 0) break;
                    for (int i = 0; i < count; i++)
                    {
                        var range = (FILE_ALLOCATED_RANGE_BUFFER)Marshal.PtrToStructure(IntPtr.Add(output, i * itemSize), typeof(FILE_ALLOCATED_RANGE_BUFFER));
                        result.Add(range);
                        offset = range.FileOffset + range.Length;
                    }
                    if (ok) break; // everything returned
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                Marshal.FreeHGlobal(output);
            }
            return result;
        }
        #endregion

        /// <summary>Resolves a path through all links to its final location, or null.</summary>
        public static string ResolveFinalPath(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;
                int capacity = 1024;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var sb = new StringBuilder(capacity);
                    uint result = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                    if (result == 0) return null;
                    if (result < sb.Capacity)
                    {
                        string target = sb.ToString();
                        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + target.Substring(8);
                        if (target.StartsWith(@"\\?\")) return target.Substring(4);
                        return target;
                    }
                    capacity = (int)result + 1; // buffer too small: retry with the size Windows asked for
                }
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    }
}
