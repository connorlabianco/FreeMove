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
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace FreeMove.IO
{
    /// <summary>
    /// Copies a folder (or file) as faithfully as possible:
    /// - symbolic links and junctions inside it are recreated as links, never followed (following
    ///   them would copy whatever they point to, or loop forever on a link to a parent folder);
    /// - files hard-linked to each other inside the folder stay hard-linked;
    /// - sparse files stay sparse;
    /// - attributes, timestamps, alternate data streams and explicit permissions are kept;
    /// - every copied file is checked against the original's size;
    /// - a different file already at the destination is never silently kept: it is replaced
    ///   (backed up in the journal) when <see cref="Overwrite"/> is set, otherwise the copy fails.
    /// </summary>
    class CopyOperation : IOOperation
    {
        readonly string pathFrom;
        readonly string pathTo;
        long fileCount;
        long fileCopied = 0;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly Dictionary<string, string> hardLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProgressThrottle throttle;

        public override void Cancel() => cts.Cancel();

        /// <summary>
        /// When false (the default) a file already at the destination is kept only if it matches
        /// the original (same size and modification time); a different one makes the copy fail.
        /// When true it is replaced, and the replaced file is kept in the journal's backup.
        /// </summary>
        public bool Overwrite { get; set; }

        /// <summary>
        /// Keep any file already at the destination without comparing it. Used when putting files
        /// back into a partly deleted original: what is still there is the original (possibly
        /// changed since by the program that was using it), so it must not be replaced.
        /// </summary>
        public bool KeepExistingFiles { get; set; }

        /// <summary>Records what a merge copies, creates and replaces. Optional.</summary>
        public MoveJournal Journal { get; set; }

        /// <summary>Problems that didn't stop the copy (e.g. permissions that couldn't be copied).</summary>
        public List<string> Warnings { get; } = new List<string>();

        public CopyOperation(string pathFrom, string pathTo)
        {
            this.pathFrom = pathFrom;
            this.pathTo = pathTo;
        }

        string Rel(string path) =>
            path.Length <= pathTo.Length ? "" : path.Substring(pathTo.Length).TrimStart('\\');

        public override async Task Run()
        {
            OnStart(new EventArgs());
            throttle = new ProgressThrottle(e => OnProgressChanged(e));
            try
            {
                if (File.Exists(pathFrom))
                {
                    fileCount = 1;
                    throttle.Report(0, fileCount);
                    await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string destDir = Path.GetDirectoryName(pathTo);
                        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                        CopyEntry(pathFrom, pathTo);
                        throttle.Report(1, 1);
                    }, cts.Token);
                }
                else
                {
                    // Counting doesn't go into links either, so it can't loop
                    fileCount = await Task.Run(() => NativeFs.EnumerateFiles(pathFrom, skipUnreadable: false).LongCount(), cts.Token);
                    throttle.Report(0, fileCount);
                    await Task.Run(() => CopyDirectory(pathFrom, pathTo, cts.Token), cts.Token);
                    throttle.Report(fileCount, fileCount);
                }
            }
            finally
            {
                OnEnd(new EventArgs());
            }
        }

        private void CopyDirectory(string dirFrom, string dirTo, CancellationToken ct)
        {
            bool created = false;
            if (!Directory.Exists(dirTo))
            {
                Journal?.CreatedDir(Rel(dirTo));
                Directory.CreateDirectory(dirTo);
                created = true;
            }

            foreach (string file in Directory.GetFiles(dirFrom))
            {
                ct.ThrowIfCancellationRequested();
                CopyEntry(file, Path.Combine(dirTo, Path.GetFileName(file)));
                throttle.Report(++fileCopied, fileCount);
            }

            foreach (string folder in Directory.GetDirectories(dirFrom))
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.Combine(dirTo, Path.GetFileName(folder));
                LinkInfo link = NativeFs.GetLink(folder);
                if (link != null)
                    CopyLink(link, dest);
                else
                    CopyDirectory(folder, dest, ct);
            }

            // Only folders this copy created get the original's attributes, times and permissions;
            // a folder that was already there (when merging) is left as it was
            if (created) CopyDirectoryMetadata(dirFrom, dirTo);
        }

        /// <summary>Copies one file, or recreates it if it is a link.</summary>
        void CopyEntry(string from, string to)
        {
            LinkInfo link = NativeFs.GetLink(from);
            if (link != null)
            {
                CopyLink(link, to);
                return;
            }

            if (File.Exists(to))
            {
                if (KeepExistingFiles) return;
                if (!Overwrite)
                {
                    if (SameFile(from, to)) return;   // e.g. moving back into a partly deleted folder
                    throw new IOException(string.Format(Properties.Resources.ResourceManager.GetString("Copy_DifferentFileExists")
                        ?? "A different file already exists at {0}; it was not overwritten.", to));
                }

                if (Journal != null && Journal.IsMerge)
                {
                    Journal.BackUpBeforeReplace(Rel(to));
                }
                else
                {
                    File.SetAttributes(to, FileAttributes.Normal);
                    File.Delete(to);
                }
            }

            Journal?.WillCopy(Rel(to));

            // Keep hard links between files inside the copied folder
            string linkId = null;
            try { linkId = NativeFs.GetHardLinkId(from); } catch { }
            if (linkId != null && hardLinks.TryGetValue(linkId, out string firstCopy) && NativeFs.TryCreateHardLink(to, firstCopy))
                return;

            CopyFileWithMetadata(from, to);

            long expected = new FileInfo(from).Length;
            long actual = new FileInfo(to).Length;
            if (expected != actual)
                throw new IOException(string.Format(Properties.Resources.ResourceManager.GetString("Copy_VerifyFailed")
                    ?? "The copy of {0} doesn't match the original.", from));

            try { CopySecurity(from, to, isDirectory: false); }
            catch (Exception ex) { AddWarning(ex); }

            if (linkId != null) hardLinks[linkId] = to;
        }

        /// <summary>
        /// Copies a file with its attributes, timestamps and alternate data streams (File.Copy uses
        /// CopyFileEx, which keeps them). Sparse files are copied without filling their gaps.
        /// </summary>
        public static void CopyFileWithMetadata(string from, string to)
        {
            FileAttributes attributes = File.GetAttributes(from);
            if ((attributes & FileAttributes.SparseFile) != 0)
            {
                NativeFs.CopySparseFile(from, to);
            }
            else
            {
                File.Copy(from, to);
                try { File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(from)); } catch { }
            }
        }

        void CopyLink(LinkInfo link, string to)
        {
            if (File.Exists(to) || Directory.Exists(to))
            {
                LinkInfo existing = NativeFs.GetLink(to);
                if (existing != null && string.Equals(existing.Target, link.Target, StringComparison.OrdinalIgnoreCase)) return;
                throw new IOException(string.Format(Properties.Resources.ResourceManager.GetString("Copy_DifferentFileExists")
                    ?? "A different file already exists at {0}; it was not overwritten.", to));
            }
            Journal?.WillCreateLink(Rel(to));
            NativeFs.CreateLinkLike(link, to);
        }

        static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length &&
                   Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) <= 2;
        }

        void CopyDirectoryMetadata(string from, string to)
        {
            try
            {
                const FileAttributes keep = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly |
                                            FileAttributes.Archive | FileAttributes.NotContentIndexed;
                var source = new DirectoryInfo(from);
                var dest = new DirectoryInfo(to);
                dest.Attributes = (source.Attributes & keep) | FileAttributes.Directory;
                dest.CreationTimeUtc = source.CreationTimeUtc;
                dest.LastWriteTimeUtc = source.LastWriteTimeUtc;
                CopySecurity(from, to, isDirectory: true);
            }
            catch (Exception ex)
            {
                AddWarning(ex);
            }
        }

        void AddWarning(Exception ex)
        {
            lock (Warnings)
            {
                if (Warnings.Count < 20) Warnings.Add(ex.Message);
            }
        }

        /// <summary>
        /// Copies the permissions that were set on the item itself (not inherited from its folder).
        /// If the original doesn't inherit permissions at all, its full list is copied as-is.
        /// </summary>
        public static void CopySecurity(string from, string to, bool isDirectory)
        {
            if (isDirectory)
            {
                DirectorySecurity source = new DirectoryInfo(from).GetAccessControl(AccessControlSections.Access);
                bool isProtected = source.AreAccessRulesProtected;
                AuthorizationRuleCollection rules = source.GetAccessRules(true, isProtected, typeof(SecurityIdentifier));
                if (!isProtected && rules.Count == 0) return;

                DirectorySecurity dest = isProtected ? new DirectorySecurity() : new DirectoryInfo(to).GetAccessControl(AccessControlSections.Access);
                if (isProtected) dest.SetAccessRuleProtection(true, false);
                foreach (FileSystemAccessRule rule in rules)
                    dest.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights, rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
                new DirectoryInfo(to).SetAccessControl(dest);
            }
            else
            {
                FileSecurity source = new FileInfo(from).GetAccessControl(AccessControlSections.Access);
                bool isProtected = source.AreAccessRulesProtected;
                AuthorizationRuleCollection rules = source.GetAccessRules(true, isProtected, typeof(SecurityIdentifier));
                if (!isProtected && rules.Count == 0) return;

                FileSecurity dest = isProtected ? new FileSecurity() : new FileInfo(to).GetAccessControl(AccessControlSections.Access);
                if (isProtected) dest.SetAccessRuleProtection(true, false);
                foreach (FileSystemAccessRule rule in rules)
                    dest.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights, rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
                new FileInfo(to).SetAccessControl(dest);
            }
        }
    }
}
