// FreeMove -- Move directories without breaking shortcuts or installations 
//    Copyright(C) 2020  Luca De Martini

//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//    GNU General Public License for more details.

//    You should have received a copy of the GNU General Public License
//    along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FreeMove
{
    class IOHelper
    {
        public class ReadOnlyPrecheckException : Exception
        {
            public string FilePath { get; }

            public ReadOnlyPrecheckException(string filePath, Exception innerException)
                : base(string.Format(CultureInfo.CurrentUICulture, Properties.Resources.ResourceManager.GetString("ReadOnlyPrecheck_FileMessage"), filePath), innerException)
            {
                FilePath = filePath;
            }
        }

        #region SymLink
        //External dll functions
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, SymbolicLink dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern uint GetFinalPathNameByHandle(IntPtr hFile, [Out] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        const uint VOLUME_NAME_DOS = 0x0;
        const uint FILE_NAME_NORMALIZED = 0x0;

        enum SymbolicLink
        {
            File = 0,
            Directory = 1
        }

        public static bool MakeLink(string targetPath, string symlink)
        {
            if (File.Exists(targetPath))
                return CreateSymbolicLink(symlink, targetPath, SymbolicLink.File);
            else
                return CreateSymbolicLink(symlink, targetPath, SymbolicLink.Directory);
        }

        /// <summary>
        /// 判断路径是否为重解析点（符号链接或挂载点）
        /// </summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取符号链接或挂载点的目标路径 (the final location, following every link; null if it doesn't exist)
        /// </summary>
        public static string GetSymbolicLinkTarget(string path) => IO.NativeFs.ResolveFinalPath(path);
        #endregion

        public static IO.MoveOperation MoveDir(string source, string destination, bool createDestination, bool overwrite = false, IO.MoveJournal journal = null, bool keepExisting = false)
        {
            return new IO.MoveOperation(source, destination, createDestination, overwrite, journal, keepExisting);
        }

        /// <summary>Creates a folder and any missing parents; returns the folders it created, outermost first.</summary>
        public static List<string> CreateDirectoryTracked(string path)
        {
            var missing = new List<string>();
            string current = Path.GetFullPath(path).TrimEnd('\\');
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                missing.Insert(0, current);
                current = Path.GetDirectoryName(current);
            }
            foreach (string dir in missing) Directory.CreateDirectory(dir);
            return missing;
        }

        /// <summary>Removes the given folders, deepest first, but only those that are empty.</summary>
        public static void RemoveEmptyDirectories(IEnumerable<string> directories)
        {
            foreach (string dir in directories.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }

        /// <summary>
        /// Deletes the original after a verified copy. Read-only flags are cleared first (they make
        /// deletes fail); links inside are removed without touching what they point to.
        /// </summary>
        public static void DeleteOriginal(string path)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, true);
        }

        static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        static bool IsSameOrUnder(string path, string folder)
        {
            string p = NormalizePath(path);
            string f = NormalizePath(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Why <paramref name="source"/> must not be moved, or null. Windows' own folders are
        /// protected with everything inside them; the folders that hold programs and profiles are
        /// protected themselves (moving something inside them is fine) and, in safe mode, the same
        /// goes for program and app-data folders.
        /// </summary>
        static string GetProtectionReason(string source, bool safeMode)
        {
            string windowsPath = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string drive = Path.GetPathRoot(windowsPath);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string usersPath = Path.GetDirectoryName(userProfile) ?? Path.Combine(drive, "Users");
            string tempPath = Path.GetTempPath();

            string cannotMove = Properties.Resources.ResourceManager.GetString("Error_DirectoryCannotMove");
            string safeModeBlocked = Properties.Resources.ResourceManager.GetString("Error_SafeModeProgramFiles");

            // Windows and its system areas, including everything inside them
            string[] protectedTrees =
            {
                windowsPath,
                Path.Combine(drive, "boot"),
                Path.Combine(drive, "System Volume Information"),
                Path.Combine(drive, "$Recycle.Bin"),
                Path.Combine(drive, "Recovery"),
                Path.Combine(drive, "$WinREAgent"),
                Path.Combine(programData, "Microsoft"),
                Path.Combine(programFiles, "WindowsApps"),
                tempPath
            };
            foreach (string tree in protectedTrees)
            {
                if (!string.IsNullOrEmpty(tree) && IsSameOrUnder(source, tree))
                    return string.Format(CultureInfo.CurrentUICulture, cannotMove, source);
            }

            // The containers themselves
            string[] protectedFolders = { drive, usersPath, programData, Path.Combine(drive, "Documents and Settings") };
            foreach (string folder in protectedFolders)
            {
                if (NormalizePath(source).Equals(NormalizePath(folder), StringComparison.OrdinalIgnoreCase))
                    return string.Format(CultureInfo.CurrentUICulture, cannotMove, source);
            }

            // Any profile folder (C:\Users\<anyone>, Public, Default): moving one breaks sign-in
            string parent = Path.GetDirectoryName(NormalizePath(source));
            if (parent != null && NormalizePath(parent).Equals(NormalizePath(usersPath), StringComparison.OrdinalIgnoreCase))
                return string.Format(CultureInfo.CurrentUICulture, cannotMove, source);

            if (safeMode)
            {
                string[] safeTrees =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
                };
                foreach (string tree in safeTrees)
                {
                    if (!string.IsNullOrEmpty(tree) && IsSameOrUnder(source, tree))
                        return string.Format(CultureInfo.CurrentUICulture, safeModeBlocked, source);
                }

                string[] safeFolders =
                {
                    programFiles, programFilesX86,
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                };
                foreach (string folder in safeFolders)
                {
                    if (!string.IsNullOrEmpty(folder) && NormalizePath(source).Equals(NormalizePath(folder), StringComparison.OrdinalIgnoreCase))
                        return string.Format(CultureInfo.CurrentUICulture, safeModeBlocked, source);
                }
            }
            return null;
        }

        /// <summary>
        /// 根据当前界面状态和指定的权限检查级别执行移动前预检查。
        /// </summary>
        public static void CheckDirectories(string source, string destination, bool safeMode, bool createDestination, Settings.PermissionCheckLevel? permissionCheckLevelOverride = null, bool skipAllChecks = false, bool allowExistingDestination = false)
        {
            // 如果禁用所有安全检查，直接返回
            if (skipAllChecks)
            {
                return;
            }

            var rm = Properties.Resources.ResourceManager;
            List<Exception> exceptions = new List<Exception>();
            Settings.PermissionCheckLevel permissionCheckLevel = permissionCheckLevelOverride ?? Settings.PermCheck;

            //Check for correct file path format
            try
            {
                Path.GetFullPath(source);
                Path.GetFullPath(destination);
            }
            catch (Exception e)
            {
                exceptions.Add(new Exception(rm.GetString("Error_InvalidPath"), e));
            }
            string pattern = @"^[A-Za-z]:\\{1,2}";
            if (!Regex.IsMatch(source, pattern) || !Regex.IsMatch(destination, pattern))
            {
                exceptions.Add(new Exception(rm.GetString("Error_InvalidPathFormat")));
            }
            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);

            // Moving something into itself would copy forever
            if (IsSameOrUnder(destination, source))
                exceptions.Add(new Exception(string.Format(rm.GetString("Error_DestInsideSource") ?? "Can't move {0} into itself.", source)));

            // A link has nothing to move (use Restore symbolic link instead)
            if (IO.NativeFs.IsLink(source))
                exceptions.Add(new Exception(string.Format(rm.GetString("Error_SourceIsLink") ?? "{0} is already a link, so there is nothing to move.", source)));

            string protection = GetProtectionReason(source, safeMode);
            if (protection != null) exceptions.Add(new Exception(protection));

            //Check for existence of directories
            if (!Directory.Exists(source) && !File.Exists(source))
                exceptions.Add(new Exception(rm.GetString("Error_SourceNotExist")));

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                // Merging into an existing destination is allowed only when the person chose to
                // overwrite, and only into the same kind of item (folder into folder, file over file)
                bool sameKind = Directory.Exists(source) ? Directory.Exists(destination) : File.Exists(destination);
                if (!allowExistingDestination || !sameKind || IsReparsePoint(destination))
                    exceptions.Add(new Exception(rm.GetString("Error_DestNameExists")));
            }

            string destinationParent = Path.GetDirectoryName(destination);
            if (!createDestination && (destinationParent == null || !Directory.Exists(destinationParent)))
                exceptions.Add(new Exception(rm.GetString("Error_DestParentNotExist")));

            // Next checks rely on the previous so if there was any exception return
            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);

            // Check privileges: create a file, then a link, next to the source. Unique names, and
            // both are removed even if a check fails.
            string sourceParent = Path.GetDirectoryName(NormalizePath(source));
            string testFile = Path.Combine(sourceParent, ".freemove-check-" + Guid.NewGuid().ToString("N"));
            string testLink = testFile + "-link";
            try
            {
                File.Create(testFile).Close();
            }
            catch (UnauthorizedAccessException e)
            {
                exceptions.Add(new Exception(rm.GetString("Error_NoPrivileges"), e));
            }
            finally
            {
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            }

            string linkTarget = Directory.Exists(destinationParent) ? destinationParent : Path.GetPathRoot(destination);
            try
            {
                if (!CreateSymbolicLink(testLink, linkTarget, SymbolicLink.Directory))
                    exceptions.Add(new Exception(rm.GetString("Error_CannotCreateSymlinkTest")));
            }
            finally
            {
                try { if (Directory.Exists(testLink)) Directory.Delete(testLink); } catch { }
            }

            // Next checks rely on the previous so if there was any exception return
            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);

            // One pass over the files: total size, and the files the permission check will open.
            // Links inside the folder are not followed (their contents live elsewhere).
            long size = 0;
            var filesToCheck = new List<string>();
            try
            {
                IEnumerable<string> files = File.Exists(source) ? new[] { source } : IO.NativeFs.EnumerateFiles(source, skipUnreadable: false);
                foreach (string file in files)
                {
                    FileInfo info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && IO.NativeFs.IsLink(file)) continue;
                    size += info.Length;

                    if (permissionCheckLevel == Settings.PermissionCheckLevel.Full)
                    {
                        filesToCheck.Add(file);
                    }
                    else if (permissionCheckLevel == Settings.PermissionCheckLevel.Fast)
                    {
                        string ext = info.Extension;
                        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                            filesToCheck.Add(file);
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                exceptions.Add(new Exception(string.Format(rm.GetString("Error_CannotRead") ?? "Can't read everything inside {0}: {1}", source, e.Message), e));
                throw new AggregateException(exceptions);
            }

            try
            {
                DriveInfo dstDrive = new(Path.GetPathRoot(destination));
                if (dstDrive.AvailableFreeSpace < size)
                    exceptions.Add(new Exception(string.Format(CultureInfo.CurrentUICulture, rm.GetString("Error_InsufficientDiskSpace"), dstDrive.Name, size / 1000000, dstDrive.AvailableFreeSpace / 1000000)));
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }

            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);

            // Open the selected files for exclusive write access: catches files in use before
            // anything is copied. (Read-only files no longer block a move: the flag is cleared
            // before the original is deleted.)
            if (permissionCheckLevel != Settings.PermissionCheckLevel.None && filesToCheck.Count > 0)
            {
                var exceptionBag = new ConcurrentBag<Exception>();
                Parallel.ForEach(filesToCheck, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
                {
                    Exception failure = TryCreateAccessCheckException(file);
                    if (failure != null)
                    {
                        exceptionBag.Add(failure);
                    }
                });

                exceptions.AddRange(exceptionBag);
            }
            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }

        /// <summary>
        /// 判断本次预检查失败是否全部由只读属性引起。
        /// </summary>
        public static bool TryGetReadOnlyPrecheckFailures(AggregateException aggregateException, out List<ReadOnlyPrecheckException> readOnlyFailures)
        {
            readOnlyFailures = new List<ReadOnlyPrecheckException>();
            foreach (Exception exception in aggregateException.InnerExceptions)
            {
                if (exception is ReadOnlyPrecheckException readOnlyException)
                {
                    readOnlyFailures.Add(readOnlyException);
                    continue;
                }

                readOnlyFailures.Clear();
                return false;
            }

            return readOnlyFailures.Count > 0;
        }

        /// <summary>
        /// 递归清除目录树上的只读属性，并跳过重解析点。
        /// </summary>
        public static void ClearReadOnlyAttributes(string rootPath)
        {
            ClearReadOnlyAttribute(rootPath);

            foreach (string file in Directory.GetFiles(rootPath))
            {
                ClearReadOnlyAttribute(file);
            }

            foreach (string directory in Directory.GetDirectories(rootPath))
            {
                ClearReadOnlyAttribute(directory);
                if (IO.NativeFs.IsLink(directory))
                {
                    continue;   // a link: what it points to isn't part of this folder
                }

                ClearReadOnlyAttributes(directory);
            }
        }

        /// <summary>
        /// 清除单个路径上的只读属性，保留其他属性不变。
        /// </summary>
        private static void ClearReadOnlyAttribute(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                return;
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        /// <summary>
        /// 将访问失败区分为只读失败和其他失败，供界面决定是否允许自动修复。
        /// </summary>
        private static Exception TryCreateAccessCheckException(string file)
        {
            FileInfo fileInfo = new FileInfo(file);
            FileStream fileStream = null;
            try
            {
                fileStream = fileInfo.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return null;
            }
            catch (Exception ex)
            {
                if (IsReadOnlyOnlyFailure(fileInfo))
                {
                    return new ReadOnlyPrecheckException(file, ex);
                }

                return ex;
            }
            finally
            {
                if (fileStream != null)
                {
                    fileStream.Dispose();
                }
            }
        }

        /// <summary>
        /// 仅当独占只读打开成功、但读写打开失败时，才视为纯只读导致的失败。
        /// </summary>
        private static bool IsReadOnlyOnlyFailure(FileInfo fileInfo)
        {
            try
            {
                if ((fileInfo.Attributes & FileAttributes.ReadOnly) == 0)
                {
                    return false;
                }

                using FileStream fileStream = fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
