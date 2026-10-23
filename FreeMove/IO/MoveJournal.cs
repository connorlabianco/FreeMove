// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeMove.IO
{
    public enum JournalKind
    {
        /// <summary>Moving a folder or file to another location and linking it back.</summary>
        Move,
        /// <summary>Moving a moved item back and removing the link.</summary>
        Undo,
        /// <summary>Copying a moved item back and removing the link, keeping the moved copy.</summary>
        RestoreCopy
    }

    public enum JournalPhase
    {
        /// <summary>Copying: the original is still complete.</summary>
        Copy,
        /// <summary>The copy is complete and verified; the original is being removed.</summary>
        Delete,
        /// <summary>
        /// The move failed after its copy completed, and the files are being put back at the
        /// original location. Recovery finishes putting them back; it never deletes the original.
        /// </summary>
        PutBack,
        /// <summary>Finished.</summary>
        Done
    }

    /// <summary>
    /// Write-ahead record of one move, undo or restore, kept in %APPDATA%\FreeMove\Journals.
    /// Each step is written before it happens, so if FreeMove or the PC stops halfway, the next
    /// start can finish the operation or roll it back.
    ///
    /// For a move merged into a folder that already existed, it also lists every file the move
    /// added, every existing file it replaced (each kept in <see cref="BackupRoot"/>), the folders
    /// and links it created, and the files that were there before. That lets the merge be rolled
    /// back or undone precisely, without touching anything that was there before. Merge journals
    /// are kept after the move finishes because undo needs them; other journals are deleted.
    /// List paths are relative to the roots ("" is the root itself, for a single file).
    /// </summary>
    public class MoveJournal : IDisposable
    {
        public Guid Id { get; private set; } = Guid.NewGuid();
        public JournalKind Kind { get; private set; }
        /// <summary>Move: the original location. Undo/restore: the link's location.</summary>
        public string SourceRoot { get; private set; }
        /// <summary>Where the files were moved to.</summary>
        public string DestinationRoot { get; private set; }
        public bool IsMerge { get; private set; }
        public bool IsFile { get; private set; }
        public string BackupRoot { get; private set; }
        public bool BackupsRemoved { get; private set; }
        /// <summary>Undo/restore: where the link was set aside while the files are copied back.</summary>
        public string ParkedLink { get; private set; }
        /// <summary>Undo/restore of a merge: the merge's own journal.</summary>
        public string MergeJournalPath { get; private set; }
        public Guid HistoryEntryId { get; private set; }
        public Guid HistoryBatchId { get; private set; }
        public bool RecommendedBatch { get; private set; }
        public long SizeBytes { get; private set; }
        public JournalPhase Phase { get; private set; } = JournalPhase.Copy;

        public List<string> PreExisting { get; } = new List<string>();
        public List<string> Copied { get; } = new List<string>();
        public List<string> Replaced { get; } = new List<string>();
        public List<string> CreatedDirs { get; } = new List<string>();
        public List<string> CreatedLinks { get; } = new List<string>();

        public string FilePath { get; private set; }

        readonly object sync = new object();
        StreamWriter writer;

        static string JournalFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeMove", "Journals");

        static string Combine(string root, string rel) => rel.Length == 0 ? root : Path.Combine(root, rel);

        #region Creating and writing
        /// <summary>Starts a journal for moving <paramref name="source"/> to <paramref name="destination"/>.</summary>
        public static MoveJournal BeginMove(string source, string destination, Guid historyEntryId, Guid historyBatchId, bool recommendedBatch, long sizeBytes)
        {
            bool isMerge = Directory.Exists(destination) || File.Exists(destination);
            var journal = new MoveJournal
            {
                Kind = JournalKind.Move,
                SourceRoot = source,
                DestinationRoot = destination,
                IsMerge = isMerge,
                IsFile = File.Exists(source),
                HistoryEntryId = historyEntryId,
                HistoryBatchId = historyBatchId,
                RecommendedBatch = recommendedBatch,
                SizeBytes = sizeBytes
            };
            if (isMerge)
                journal.BackupRoot = Path.Combine(Path.GetPathRoot(destination), ".FreeMoveReplaced", journal.Id.ToString("N"));

            journal.Open();
            if (isMerge) journal.RecordPreExisting();
            journal.Append("X", JournalPhase.Copy.ToString());
            return journal;
        }

        /// <summary>Starts a journal for moving or copying a moved item back.</summary>
        public static MoveJournal BeginUndo(bool keepMovedCopy, string linkPath, string target, string parkedLink, bool isFile,
            string mergeJournalPath, Guid historyEntryId, Guid historyBatchId)
        {
            var journal = new MoveJournal
            {
                Kind = keepMovedCopy ? JournalKind.RestoreCopy : JournalKind.Undo,
                SourceRoot = linkPath,
                DestinationRoot = target,
                ParkedLink = parkedLink,
                IsFile = isFile,
                MergeJournalPath = mergeJournalPath,
                HistoryEntryId = historyEntryId,
                HistoryBatchId = historyBatchId
            };
            journal.Open();
            journal.Append("X", JournalPhase.Copy.ToString());
            return journal;
        }

        void Open()
        {
            Directory.CreateDirectory(JournalFolder);
            FilePath = Path.Combine(JournalFolder, Id.ToString("N") + ".txt");
            writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            Append("K", Kind.ToString());
            Append("S", SourceRoot);
            Append("D", DestinationRoot);
            Append("M", IsMerge ? "1" : "0");
            Append("F", IsFile ? "1" : "0");
            if (BackupRoot != null) Append("B", BackupRoot);
            if (ParkedLink != null) Append("L", ParkedLink);
            if (MergeJournalPath != null) Append("J", MergeJournalPath);
            Append("H", HistoryEntryId.ToString("N"));
            Append("G", HistoryBatchId.ToString("N"));
            Append("R", RecommendedBatch ? "1" : "0");
            Append("Z", SizeBytes.ToString());
        }

        void Append(string code, string value)
        {
            lock (sync)
            {
                if (writer == null)
                {
                    // A finished merge journal re-opened to add entries (e.g. files moved back on undo)
                    writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                }
                writer.Write(code);
                writer.Write('\t');
                writer.WriteLine(value);
            }
        }

        void RecordPreExisting()
        {
            if (File.Exists(DestinationRoot))
            {
                Add(PreExisting, "p", "");
                return;
            }
            foreach (string f in NativeFs.EnumerateFiles(DestinationRoot, skipUnreadable: true))
                Add(PreExisting, "p", f.Substring(DestinationRoot.Length).TrimStart('\\'));
        }

        void Add(List<string> list, string code, string rel)
        {
            lock (sync)
            {
                Append(code, rel);   // written first: the record always covers what happens next
                list.Add(rel);
            }
        }

        public void SetPhase(JournalPhase phase)
        {
            Phase = phase;
            Append("X", phase.ToString());
        }

        /// <summary>Records a file about to be written by the move. Call before copying it.</summary>
        public void WillCopy(string rel) { if (IsMerge) Add(Copied, "c", rel); }
        public void CreatedDir(string rel) { if (IsMerge) Add(CreatedDirs, "d", rel); }
        public void WillCreateLink(string rel) { if (IsMerge) Add(CreatedLinks, "l", rel); }

        /// <summary>
        /// Moves an existing destination file into the backup folder before it is replaced. The
        /// intent is written first, so a crash at any point leaves the file either in place or in
        /// the backup, and recovery knows where.
        /// </summary>
        public void BackUpBeforeReplace(string rel)
        {
            string existing = Combine(DestinationRoot, rel);
            string backup = Combine(BackupRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            try
            {
                var hiddenRoot = new DirectoryInfo(Path.GetDirectoryName(BackupRoot));
                hiddenRoot.Attributes |= FileAttributes.Hidden;
            }
            catch { }

            Add(Replaced, "r", rel);
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(existing, backup);   // same drive: a rename, no copying
        }

        /// <summary>Marks the operation finished. Non-merge journals are deleted; merge journals are kept for undo.</summary>
        public void Complete()
        {
            SetPhase(JournalPhase.Done);
            Close();
            if (!(Kind == JournalKind.Move && IsMerge)) DeleteFile();
        }

        public void Close()
        {
            lock (sync)
            {
                writer?.Dispose();
                writer = null;
            }
        }

        public void Dispose() => Close();

        public void DeleteFile()
        {
            Close();
            try { if (FilePath != null && File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
        #endregion

        #region Loading
        public static MoveJournal Load(string path)
        {
            var journal = new MoveJournal { FilePath = path };
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid id)) journal.Id = id;

            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string code = line.Substring(0, tab);
                string value = line.Substring(tab + 1);
                switch (code)
                {
                    case "K": journal.Kind = (JournalKind)Enum.Parse(typeof(JournalKind), value); break;
                    case "S": journal.SourceRoot = value; break;
                    case "D": journal.DestinationRoot = value; break;
                    case "M": journal.IsMerge = value == "1"; break;
                    case "F": journal.IsFile = value == "1"; break;
                    case "B": journal.BackupRoot = value; break;
                    case "L": journal.ParkedLink = value; break;
                    case "J": journal.MergeJournalPath = value; break;
                    case "H": journal.HistoryEntryId = Guid.ParseExact(value, "N"); break;
                    case "G": journal.HistoryBatchId = Guid.ParseExact(value, "N"); break;
                    case "R": journal.RecommendedBatch = value == "1"; break;
                    case "Z": long.TryParse(value, out long size); journal.SizeBytes = size; break;
                    case "X": journal.Phase = (JournalPhase)Enum.Parse(typeof(JournalPhase), value); break;
                    case "BACKUPS_REMOVED": journal.BackupsRemoved = true; break;
                    case "p": journal.PreExisting.Add(value); break;
                    case "c": journal.Copied.Add(value); break;
                    case "r": journal.Replaced.Add(value); break;
                    case "d": journal.CreatedDirs.Add(value); break;
                    case "l": journal.CreatedLinks.Add(value); break;
                }
            }
            return journal;
        }

        static bool IsInUse(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Journals of operations that didn't finish (FreeMove or the PC stopped).</summary>
        public static List<MoveJournal> LoadUnfinished()
        {
            var result = new List<MoveJournal>();
            if (!Directory.Exists(JournalFolder)) return result;
            foreach (string path in Directory.GetFiles(JournalFolder, "*.txt"))
            {
                // A journal that is open for writing belongs to an operation another FreeMove
                // window is running right now: leave it alone
                if (IsInUse(path)) continue;
                try
                {
                    MoveJournal journal = Load(path);
                    if (journal.Phase != JournalPhase.Done) result.Add(journal);
                }
                catch { }
            }
            return result;
        }
        #endregion

        #region Rolling back and undoing a merge
        static void DeleteFileIfExists(string path)
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        static void DeleteLinkIfExists(string path)
        {
            if (Directory.Exists(path) && NativeFs.IsLink(path)) Directory.Delete(path, false);
            else DeleteFileIfExists(path);
        }

        /// <summary>
        /// Puts the destination back exactly as it was before the merge: removes what the move
        /// added and restores what it replaced. The source isn't touched. Returns problems; when
        /// any replaced file couldn't be restored, the backup folder is kept and named in the result.
        /// </summary>
        public List<string> RollBackDestination()
        {
            var errors = new List<string>();
            foreach (string rel in Copied.Distinct())
            {
                try { DeleteFileIfExists(Combine(DestinationRoot, rel)); }
                catch (Exception ex) { errors.Add($"{Combine(DestinationRoot, rel)}: {ex.Message}"); }
            }
            foreach (string rel in CreatedLinks)
            {
                try { DeleteLinkIfExists(Combine(DestinationRoot, rel)); }
                catch (Exception ex) { errors.Add($"{Combine(DestinationRoot, rel)}: {ex.Message}"); }
            }
            errors.AddRange(RestoreReplaced());
            RemoveCreatedDirs();
            return errors;
        }

        /// <summary>
        /// Puts each replaced file back from the backup. The backup folder is deleted only when
        /// every file was restored, so a failure never loses the only copy.
        /// </summary>
        List<string> RestoreReplaced()
        {
            var errors = new List<string>();
            if (BackupRoot == null) return errors;

            foreach (string rel in Replaced.Distinct())
            {
                string backup = Combine(BackupRoot, rel);
                string original = Combine(DestinationRoot, rel);
                try
                {
                    if (!File.Exists(backup)) continue;   // never moved (crash before) or already restored
                    string dir = Path.GetDirectoryName(original);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    DeleteFileIfExists(original);
                    File.Move(backup, original);
                }
                catch (Exception ex)
                {
                    errors.Add($"{original}: {ex.Message}");
                }
            }

            if (errors.Count == 0)
            {
                RemoveBackupFolder();
            }
            else
            {
                errors.Add(string.Format(Properties.Resources.ResourceManager.GetString("Journal_BackupKept")
                    ?? "Some replaced files could not be put back. Their originals are still in {0}.", BackupRoot));
            }
            return errors;
        }

        void RemoveCreatedDirs()
        {
            // Deepest first, and only if empty: never remove a folder that has other files in it
            foreach (string rel in CreatedDirs.Distinct().OrderByDescending(r => r.Length))
            {
                try
                {
                    string dir = Combine(DestinationRoot, rel);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }

        void RemoveBackupFolder()
        {
            try
            {
                if (BackupRoot == null) return;
                if (Directory.Exists(BackupRoot)) Directory.Delete(BackupRoot, true);
                else if (File.Exists(BackupRoot)) DeleteFileIfExists(BackupRoot);   // single-file merge
                string parent = Path.GetDirectoryName(BackupRoot);
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch { }
        }

        /// <summary>
        /// Deletes the backups of replaced files to free space. After this, undo leaves the
        /// replacing files at the destination (it copies them back instead of moving them).
        /// </summary>
        public void RemoveBackups()
        {
            RemoveBackupFolder();
            BackupsRemoved = true;
            Append("BACKUPS_REMOVED", "1");
            Close();
        }

        public long BackupSizeBytes()
        {
            try
            {
                if (BackupsRemoved || BackupRoot == null) return 0;
                if (File.Exists(BackupRoot)) return new FileInfo(BackupRoot).Length;
                if (!Directory.Exists(BackupRoot)) return 0;
                return NativeFs.EnumerateFiles(BackupRoot, true).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        /// <summary>
        /// Files in the destination that are neither from before the merge nor added by it: saved
        /// into the folder after the move (through the link or directly).
        /// </summary>
        public List<string> FindFilesAddedSinceMove()
        {
            if (IsFile || !Directory.Exists(DestinationRoot)) return new List<string>();
            var known = new HashSet<string>(PreExisting.Concat(Copied).Concat(CreatedLinks), StringComparer.OrdinalIgnoreCase);
            return NativeFs.EnumerateFiles(DestinationRoot, true)
                .Select(f => f.Substring(DestinationRoot.Length).TrimStart('\\'))
                .Where(rel => !known.Contains(rel))
                .ToList();
        }

        /// <summary>Includes files saved after the move in this merge's undo.</summary>
        public void IncludeInUndo(IEnumerable<string> rels)
        {
            foreach (string rel in rels) Add(Copied, "c", rel);
            Close();
        }

        /// <summary>
        /// Undo, step 1: copies the files the merge added back to <paramref name="sourceRoot"/>.
        /// Nothing is removed yet, so this can be cancelled safely.
        /// </summary>
        public void CopyMovedFilesBack(string sourceRoot, Action<long, long> progress, CancellationToken token)
        {
            List<string> files = Copied.Distinct().ToList();
            long done = 0;
            progress?.Invoke(0, files.Count);
            if (!IsFile) Directory.CreateDirectory(sourceRoot);

            foreach (string rel in CreatedDirs)
            {
                try { Directory.CreateDirectory(Combine(sourceRoot, rel)); } catch { }
            }

            foreach (string rel in files)
            {
                token.ThrowIfCancellationRequested();
                string from = Combine(DestinationRoot, rel);
                string to = Combine(sourceRoot, rel);
                string dir = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(from) && !File.Exists(to))
                {
                    CopyOperation.CopyFileWithMetadata(from, to);
                    if (new FileInfo(to).Length != new FileInfo(from).Length)
                        throw new IOException(string.Format(Properties.Resources.ResourceManager.GetString("Copy_VerifyFailed") ?? "The copy of {0} doesn't match the original.", from));
                }
                progress?.Invoke(++done, files.Count);
            }

            foreach (string rel in CreatedLinks)
            {
                string from = Combine(DestinationRoot, rel);
                string to = Combine(sourceRoot, rel);
                LinkInfo link = NativeFs.GetLink(from);
                if (link != null && !File.Exists(to) && !Directory.Exists(to)) NativeFs.CreateLinkLike(link, to);
            }
        }

        /// <summary>
        /// Undo, step 2 (after every file is back at the source): removes the files the merge added
        /// from the destination, restores the files they replaced and removes the folders and links it
        /// created. Files that were at the destination before stay. When the backups were deleted,
        /// the replacing files are left in place instead, so the destination never loses a file.
        /// </summary>
        public void RemoveMovedFilesFromDestination()
        {
            var keep = BackupsRemoved ? new HashSet<string>(Replaced, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            var errors = new List<string>();
            foreach (string rel in Copied.Distinct())
            {
                if (keep.Contains(rel)) continue;
                try { DeleteFileIfExists(Combine(DestinationRoot, rel)); }
                catch (Exception ex) { errors.Add($"{Combine(DestinationRoot, rel)}: {ex.Message}"); }
            }
            foreach (string rel in CreatedLinks)
            {
                try { DeleteLinkIfExists(Combine(DestinationRoot, rel)); }
                catch (Exception ex) { errors.Add($"{Combine(DestinationRoot, rel)}: {ex.Message}"); }
            }
            if (!BackupsRemoved) errors.AddRange(RestoreReplaced());
            RemoveCreatedDirs();
            if (errors.Count > 0)
                throw new IOException(string.Join("\n", errors.Distinct().Take(20)));
        }
        #endregion
    }

    /// <summary>
    /// Copies or moves a merged move back using its journal, with progress. Step 1 copies the files
    /// back (cancellable); step 2, for an undo, removes them from the destination and restores
    /// what they replaced. <see cref="CopyCompleted"/> fires between the steps.
    /// </summary>
    class JournalUndoOperation : IOOperation
    {
        readonly MoveJournal merge;
        readonly string sourceRoot;
        readonly bool keepMovedCopy;
        readonly CancellationTokenSource cts = new CancellationTokenSource();

        public event EventHandler CopyCompleted;

        public JournalUndoOperation(MoveJournal merge, string sourceRoot, bool keepMovedCopy)
        {
            this.merge = merge;
            this.sourceRoot = sourceRoot;
            this.keepMovedCopy = keepMovedCopy;
        }

        public override void Cancel() => cts.Cancel();

        public override async Task Run()
        {
            OnStart(EventArgs.Empty);
            try
            {
                var throttle = new ProgressThrottle(e => OnProgressChanged(e));
                try
                {
                    await Task.Run(() => merge.CopyMovedFilesBack(sourceRoot, throttle.Report, cts.Token), cts.Token);
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    throw new MoveOperation.CopyFailedException(Properties.Resources.ResourceManager.GetString("MoveOp_CopyFailed"), e);
                }

                cts.Token.ThrowIfCancellationRequested();
                CopyCompleted?.Invoke(this, EventArgs.Empty);
                if (keepMovedCopy) return;

                try
                {
                    await Task.Run(() => merge.RemoveMovedFilesFromDestination());
                }
                catch (Exception e)
                {
                    throw new MoveOperation.DeleteFailedException(Properties.Resources.ResourceManager.GetString("MoveOp_DeleteOldFailed"), e);
                }
            }
            finally
            {
                OnEnd(EventArgs.Empty);
            }
        }
    }

    /// <summary>Runs a piece of work as an operation, for the progress window.</summary>
    class DelegateOperation : IOOperation
    {
        readonly Func<Task> work;
        public DelegateOperation(Func<Task> work) { this.work = work; }
        public override void Cancel() { }
        public override async Task Run()
        {
            OnStart(EventArgs.Empty);
            try { await work(); }
            finally { OnEnd(EventArgs.Empty); }
        }
    }

    /// <summary>
    /// Limits progress updates to about ten a second, so copying many small files doesn't flood
    /// the window with updates. The final value is always reported.
    /// </summary>
    class ProgressThrottle
    {
        readonly Action<IOOperation.ProgressChangedEventArgs> report;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -1000;

        public ProgressThrottle(Action<IOOperation.ProgressChangedEventArgs> report) { this.report = report; }

        public void Report(long progress, long max)
        {
            long now = clock.ElapsedMilliseconds;
            if (progress < max && progress != 0 && now - lastReport < 100) return;
            lastReport = now;
            report(new IOOperation.ProgressChangedEventArgs(progress, max));
        }
    }
}
