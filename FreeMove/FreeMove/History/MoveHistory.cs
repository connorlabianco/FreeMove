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
using System.Threading;
using System.Xml.Serialization;

namespace FreeMove.History
{
    public enum MoveEntryState
    {
        Moved,
        Undone
    }

    /// <summary>What an entry looks like on disk right now.</summary>
    public enum MoveEntryLiveState
    {
        /// <summary>The link is in place and points at the moved files: can be undone.</summary>
        Moved,
        /// <summary>Moved back by FreeMove.</summary>
        Undone,
        /// <summary>The link or the moved files were changed or removed outside FreeMove.</summary>
        Changed
    }

    /// <summary>One folder or file that was moved and linked back.</summary>
    public class MoveEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>The original location, where the link now is.</summary>
        public string LinkPath { get; set; }
        /// <summary>Where the files were moved to.</summary>
        public string TargetPath { get; set; }
        public long SizeBytes { get; set; }
        public bool IsFile { get; set; }
        public MoveEntryState State { get; set; } = MoveEntryState.Moved;
        public DateTime? UndoneAt { get; set; }

        /// <summary>
        /// Set when this move was merged into a folder that already existed: the saved record of
        /// exactly which files the move added and which it replaced. Undo uses it to move back
        /// only those files and restore the replaced ones, leaving everything else in place.
        /// </summary>
        public string MergeJournalPath { get; set; }
    }

    /// <summary>
    /// One action in the history: a manual move, a Recommended moves batch, or an undo of one or
    /// more earlier moves. Every batch is shown as an expandable row.
    /// </summary>
    public class MoveBatch
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Time { get; set; } = DateTime.Now;
        public bool IsRecommendedBatch { get; set; }
        /// <summary>True for a record of moving items back; these entries can't be undone again.</summary>
        public bool IsUndoBatch { get; set; }
        public List<MoveEntry> Entries { get; set; } = new List<MoveEntry>();
    }

    /// <summary>The result of a health check on one moved item.</summary>
    public enum MoveHealth
    {
        /// <summary>The link is in place and the moved files are there.</summary>
        Healthy,
        /// <summary>The link is there but the moved files are not: their drive is missing or they were deleted.</summary>
        TargetMissing,
        /// <summary>A real folder or file is at the original location again (often after a reinstall).</summary>
        Replaced,
        /// <summary>Nothing is at the original location, but the moved files are there: the link can be recreated.</summary>
        LinkMissing,
        /// <summary>The link points somewhere else.</summary>
        PointsElsewhere,
        /// <summary>Neither the link nor the moved files exist.</summary>
        Gone
    }

    public class MoveHistoryData
    {
        public List<MoveBatch> Batches { get; set; } = new List<MoveBatch>();
    }

    /// <summary>
    /// Keeps a log of moves in %APPDATA%\FreeMove\History.xml, next to Settings.xml.
    /// Each completed move is written as soon as it finishes, so the log survives crashes.
    /// Writes go to a temporary file that atomically replaces the log, and a system-wide lock
    /// keeps two FreeMove windows from overwriting each other's changes.
    /// </summary>
    public static class MoveHistory
    {
        static readonly object Sync = new object();
        const string MutexName = @"Local\FreeMove.History";

        static string GetSavePath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeMove", "History.xml");

        /// <summary>Runs <paramref name="action"/> while holding the history lock (this process and others).</summary>
        static T WithLock<T>(Func<T> action)
        {
            lock (Sync)
            {
                using (var mutex = new Mutex(false, MutexName))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                        catch (AbandonedMutexException) { acquired = true; }   // the other instance crashed; the file is still consistent
                        return action();
                    }
                    finally
                    {
                        if (acquired) mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static MoveHistoryData Load() => WithLock(LoadUnlocked);

        static MoveHistoryData LoadUnlocked()
        {
            string path = GetSavePath();
            if (!File.Exists(path))
            {
                // The log was deleted: fall back to a backup if there is one
                return TryRead(path + ".bak") ?? TryRead(DailyBackupPath) ?? new MoveHistoryData();
            }

            MoveHistoryData data = TryRead(path);
            if (data != null) return data;

            // Keep a copy of an unreadable log rather than losing it: a new name each time,
            // so an older copy is never overwritten. Then use the newest readable backup.
            try { File.Copy(path, path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".corrupt", false); } catch { }
            Log.Warn("History.xml could not be read; using a backup");
            return TryRead(path + ".bak") ?? TryRead(DailyBackupPath) ?? new MoveHistoryData();
        }

        /// <summary>A copy of the log made at most once a day, in addition to History.xml.bak (the previous version).</summary>
        static string DailyBackupPath => GetSavePath() + ".daily";

        static MoveHistoryData TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (FileStream fs = File.OpenRead(path))
                {
                    var ser = new XmlSerializer(typeof(MoveHistoryData));
                    return ser.Deserialize(fs) as MoveHistoryData;
                }
            }
            catch
            {
                return null;
            }
        }

        static void SaveUnlocked(MoveHistoryData data)
        {
            string path = GetSavePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Write a complete new file, then swap it in atomically: a crash leaves either the
            // old log or the new one, never neither
            string temp = path + ".tmp";
            var ser = new XmlSerializer(typeof(MoveHistoryData));
            using (FileStream fs = File.Create(temp))
            {
                ser.Serialize(fs, data);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                // Once a day, also keep a copy of the current log before replacing it
                try
                {
                    if (!File.Exists(DailyBackupPath) || File.GetLastWriteTime(DailyBackupPath) < DateTime.Now.AddDays(-1))
                        File.Copy(path, DailyBackupPath, true);
                }
                catch { }
                File.Replace(temp, path, path + ".bak");
            }
            else
                File.Move(temp, path);
        }

        static void Modify(Action<MoveHistoryData> change)
        {
            try
            {
                WithLock(() =>
                {
                    MoveHistoryData data = LoadUnlocked();
                    change(data);
                    SaveUnlocked(data);
                    return true;
                });
            }
            catch { }   // the log is a convenience: failing to write it never interrupts a move
        }

        /// <summary>
        /// Records one finished move (or undo) in the batch <paramref name="batchId"/>, creating
        /// the batch if it doesn't exist yet. Called right after each item, not at the end of a batch.
        /// </summary>
        public static void AddEntry(Guid batchId, MoveEntry entry, bool isRecommendedBatch, bool isUndoBatch)
        {
            Modify(data =>
            {
                MoveBatch batch = data.Batches.FirstOrDefault(b => b.Id == batchId);
                if (batch == null)
                {
                    batch = new MoveBatch { Id = batchId, IsRecommendedBatch = isRecommendedBatch, IsUndoBatch = isUndoBatch };
                    data.Batches.Add(batch);
                }
                if (!batch.Entries.Any(e => e.Id == entry.Id)) batch.Entries.Add(entry);
            });
        }

        public static bool ContainsEntry(Guid entryId) =>
            Load().Batches.Any(b => b.Entries.Any(e => e.Id == entryId));

        public static MoveEntry FindEntry(Guid entryId) =>
            Load().Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries).FirstOrDefault(e => e.Id == entryId);

        /// <summary>The not-yet-undone move whose link is at <paramref name="linkPath"/>, if any.</summary>
        public static MoveEntry FindMovedEntryByLink(string linkPath)
        {
            string normalized = Normalize(linkPath);
            return Load().Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries)
                .LastOrDefault(e => e.State == MoveEntryState.Moved && Normalize(e.LinkPath) == normalized);
        }

        public static void MarkUndone(IEnumerable<Guid> entryIds)
        {
            var ids = new HashSet<Guid>(entryIds);
            if (ids.Count == 0) return;
            Modify(data =>
            {
                foreach (MoveEntry e in data.Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries).Where(e => ids.Contains(e.Id)))
                {
                    e.State = MoveEntryState.Undone;
                    e.UndoneAt = DateTime.Now;
                }
            });
        }

        /// <summary>
        /// Removes batches whose entries are all undone or no longer valid. Merge records and
        /// backups of entries that changed outside FreeMove are kept: they may hold the only copy
        /// of a replaced file.
        /// </summary>
        public static void ClearFinished()
        {
            Modify(data => data.Batches.RemoveAll(b => b.Entries.All(e => GetLiveState(e) != MoveEntryLiveState.Moved)));
        }

        /// <summary>Checks the disk: is the link still in place and pointing at the moved files?</summary>
        public static MoveEntryLiveState GetLiveState(MoveEntry entry)
        {
            if (entry.State == MoveEntryState.Undone) return MoveEntryLiveState.Undone;
            try
            {
                bool linkExists = entry.IsFile ? File.Exists(entry.LinkPath) : Directory.Exists(entry.LinkPath);
                bool targetExists = entry.IsFile ? File.Exists(entry.TargetPath) : Directory.Exists(entry.TargetPath);
                if (!linkExists || !targetExists || !IO.NativeFs.IsLink(entry.LinkPath))
                    return MoveEntryLiveState.Changed;

                string resolved = IOHelper.GetSymbolicLinkTarget(entry.LinkPath);
                if (resolved == null || Normalize(resolved) != Normalize(IOHelper.GetSymbolicLinkTarget(entry.TargetPath) ?? entry.TargetPath))
                    return MoveEntryLiveState.Changed;

                return MoveEntryLiveState.Moved;
            }
            catch
            {
                return MoveEntryLiveState.Changed;
            }
        }

        /// <summary>Checks a moved item on disk in more detail than <see cref="GetLiveState"/>.</summary>
        public static MoveHealth Diagnose(MoveEntry entry)
        {
            try
            {
                bool linkPresent = entry.IsFile ? File.Exists(entry.LinkPath) || IO.NativeFs.IsLink(entry.LinkPath)
                                                : Directory.Exists(entry.LinkPath) || IO.NativeFs.IsLink(entry.LinkPath);
                bool isLink = linkPresent && IO.NativeFs.IsLink(entry.LinkPath);
                bool targetExists = entry.IsFile ? File.Exists(entry.TargetPath) : Directory.Exists(entry.TargetPath);

                if (!linkPresent) return targetExists ? MoveHealth.LinkMissing : MoveHealth.Gone;
                if (!isLink) return MoveHealth.Replaced;
                if (!targetExists) return MoveHealth.TargetMissing;

                string resolved = IOHelper.GetSymbolicLinkTarget(entry.LinkPath);
                if (resolved == null || Normalize(resolved) != Normalize(IOHelper.GetSymbolicLinkTarget(entry.TargetPath) ?? entry.TargetPath))
                    return MoveHealth.PointsElsewhere;
                return MoveHealth.Healthy;
            }
            catch
            {
                return MoveHealth.Gone;
            }
        }

        /// <summary>Moves that haven't been undone (whatever their state on disk now).</summary>
        public static List<MoveEntry> ActiveMoves() =>
            Load().Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries)
                .Where(e => e.State == MoveEntryState.Moved).ToList();

        /// <summary>
        /// Adds links found on disk (see <see cref="FindUnrecordedLinks"/>) as one batch, so they
        /// can be checked and undone like any other move. Returns how many were added.
        /// </summary>
        public static int AddRecoveredEntries(List<MoveEntry> entries)
        {
            if (entries.Count == 0) return 0;
            Guid batchId = Guid.NewGuid();
            Modify(data =>
            {
                var batch = new MoveBatch { Id = batchId };
                batch.Entries.AddRange(entries);
                data.Batches.Add(batch);
            });
            return entries.Count;
        }

        /// <summary>
        /// Looks for links that FreeMove (or someone) made from the system drive to another drive
        /// and that History doesn't know about, e.g. after History was lost. Only the places
        /// programs and their data live are searched, a few levels deep. Windows' own
        /// compatibility links point to the same drive, so they are not included.
        /// </summary>
        public static List<MoveEntry> FindUnrecordedLinks(CancellationToken token)
        {
            var known = new HashSet<string>(
                Load().Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries)
                    .Where(e => e.State == MoveEntryState.Moved).Select(e => Normalize(e.LinkPath)));
            var found = new List<MoveEntry>();
            string systemDrive = Recommendations.RecommendationScanner.SystemDrive;

            void Scan(string folder, int depth)
            {
                if (depth < 0 || token.IsCancellationRequested || string.IsNullOrEmpty(folder)) return;
                IEnumerable<string> children;
                try { children = Directory.EnumerateFileSystemEntries(folder).ToList(); }
                catch { return; }
                foreach (string child in children)
                {
                    if (token.IsCancellationRequested) return;
                    if (IO.NativeFs.IsLink(child))
                    {
                        string target = IOHelper.GetSymbolicLinkTarget(child);
                        if (target == null || known.Contains(Normalize(child))) continue;
                        string targetRoot = Path.GetPathRoot(target);
                        if (string.Equals(targetRoot, systemDrive, StringComparison.OrdinalIgnoreCase)) continue;
                        bool isFile = File.Exists(target);
                        long size = 0;
                        try { size = isFile ? new FileInfo(target).Length : Recommendations.RecommendationScanner.GetDirectorySize(target, token); }
                        catch { }
                        found.Add(new MoveEntry { LinkPath = child.TrimEnd('\\'), TargetPath = target.TrimEnd('\\'), IsFile = isFile, SizeBytes = size });
                    }
                    else if (Directory.Exists(child))
                    {
                        Scan(child, depth - 1);
                    }
                }
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Scan(Path.Combine(profile, "AppData", "Local"), 2);
            Scan(Path.Combine(profile, "AppData", "Roaming"), 2);
            Scan(Path.Combine(profile, "AppData", "LocalLow"), 2);
            Scan(profile, 1);
            Scan(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 2);
            Scan(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), 2);
            Scan(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 2);
            Scan(systemDrive, 1);

            return found
                .GroupBy(e => Normalize(e.LinkPath))
                .Select(g => g.First())
                .ToList();
        }

        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd('\\').ToUpperInvariant();
    }
}
