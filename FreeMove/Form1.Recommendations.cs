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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FreeMove.Dialogs;
using FreeMove.History;
using FreeMove.InUse;
using FreeMove.IO;
using FreeMove.Recommendations;

namespace FreeMove
{
    /// <summary>One folder or file to move and link back.</summary>
    public class MoveJob
    {
        public string Source { get; set; }
        public string Destination { get; set; }
        /// <summary>Merge into an existing destination, replacing same-name files (backed up).</summary>
        public bool Overwrite { get; set; }
        /// <summary>Known size, or 0 to measure after the move.</summary>
        public long SizeBytes { get; set; }
        /// <summary>The pre-move checks already ran (manual moves run them interactively first).</summary>
        public bool ChecksDone { get; set; }
    }

    public class MoveBatchResult
    {
        public List<MoveJob> Moved { get; } = new List<MoveJob>();
        public long MovedBytes { get; set; }
        public List<string> Failed { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public bool Cancelled { get; set; }
    }

    // Moving folders: Recommended moves and manual moves share this path.
    public partial class Form1
    {
        static string R(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        private async void RecommendedMovesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<MoveRecommendation> accepted;
            string destinationRoot;

            using (var form = new RecommendationsForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                accepted = form.SelectedRecommendations;
                destinationRoot = form.DestinationRoot;
            }

            if (accepted.Count == 0) return;
            await MoveRecommendations(accepted, destinationRoot);
        }

        /// <summary>
        /// Moves the accepted recommendations into <paramref name="destinationRoot"/> (mirroring
        /// their paths). Destinations that already exist are listed first; each is skipped unless
        /// the person chooses to overwrite it.
        /// </summary>
        private async Task MoveRecommendations(List<MoveRecommendation> items, string destinationRoot)
        {
            var jobs = items.Select(i => new MoveJob
            {
                Source = i.SourcePath.TrimEnd('\\'),
                Destination = i.GetDestination(destinationRoot),
                SizeBytes = i.SizeBytes
            }).ToList();

            var skippedConflicts = new List<string>();
            List<MoveJob> conflicts = jobs.Where(j => PathExists(j.Destination)).ToList();
            if (conflicts.Count > 0)
            {
                List<int> chosen;
                using (var form = new ConflictForm(conflicts.Select(c => new ConflictItem
                {
                    SourcePath = c.Source,
                    DestinationPath = c.Destination
                }).ToList()))
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    chosen = form.OverwriteIndices;
                }

                for (int i = 0; i < conflicts.Count; i++)
                {
                    if (chosen.Contains(i)) conflicts[i].Overwrite = true;
                    else skippedConflicts.Add(conflicts[i].Source + "  " + R("Conflict_SkippedNote", "(already exists at the destination)"));
                }
                jobs = jobs.Where(j => !conflicts.Contains(j) || j.Overwrite).ToList();
            }

            MoveBatchResult result = await RunMoveJobs(jobs, recommendedBatch: true);
            result.Skipped.InsertRange(0, skippedConflicts);
            ShowBatchSummary(result, R("Rec_SummaryTitle", "Recommended moves"));
        }

        /// <summary>
        /// Moves the jobs in order with queue progress. Each finished move is written to History
        /// right away. Items programs are using are set aside and offered again at the end, with
        /// the option to end those programs. A failed or skipped item is put back as it was.
        /// </summary>
        private async Task<MoveBatchResult> RunMoveJobs(List<MoveJob> jobs, bool recommendedBatch)
        {
            var result = new MoveBatchResult();
            Guid batchId = Guid.NewGuid();

            Enabled = false;
            try
            {
                var inUse = new List<InUseItem>();
                result.Cancelled = await RunMoveQueue(jobs, batchId, recommendedBatch, result, inUse);

                while (!result.Cancelled && inUse.Count > 0)
                {
                    InUseChoice choice;
                    using (var form = new InUseForm(inUse))
                    {
                        form.ShowDialog(this);
                        choice = form.Choice;
                    }

                    if (choice == InUseChoice.Skip)
                    {
                        result.Skipped.AddRange(inUse.Select(i => i.DisplayPath + "  " + R("InUse_SkippedNote", "(in use)")));
                        break;
                    }
                    if (choice == InUseChoice.EndTasksAndRetry)
                        await EndTasks(inUse);

                    List<MoveJob> retry = inUse.Select(i => (MoveJob)i.Payload).ToList();
                    inUse = new List<InUseItem>();
                    result.Cancelled = await RunMoveQueue(retry, batchId, recommendedBatch, result, inUse);
                }
            }
            finally
            {
                Enabled = true;
                directoryBrowser1.RefreshBrowser();
            }
            return result;
        }

        private async Task EndTasks(List<InUseItem> inUse)
        {
            List<string> endErrors = await Task.Run(() => ProcessFinder.EndProcesses(inUse.SelectMany(i => i.Processes)));
            if (endErrors.Count > 0)
                ScrollableMessageBox.Show(this, string.Join("\n", endErrors), R("InUse_EndFailed", "Some programs could not be ended"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowBatchSummary(MoveBatchResult result, string title)
        {
            var summary = new StringBuilder();
            summary.AppendFormat(R("Rec_SummaryMoved", "Moved {0} folder(s), freeing about {1} on {2}."),
                result.Moved.Count, RecommendationScanner.FormatSize(result.MovedBytes), RecommendationScanner.SystemDrive);
            AppendSection(summary, R("Rec_SummaryFailed", "These were not moved and were left as they were:"), result.Failed);
            AppendSection(summary, R("Rec_SummarySkipped", "Skipped (left as they were):"), result.Skipped);
            AppendSection(summary, R("Rec_SummaryWarnings", "Moved, with these notes:"), result.Warnings);
            if (result.Moved.Count > 0)
            {
                summary.AppendLine().AppendLine();
                summary.Append(R("Rec_SummaryUndoHint", "You can undo any of these moves from History."));
            }
            if (result.Cancelled)
            {
                summary.AppendLine().AppendLine();
                summary.Append(R("Rec_SummaryCancelled", "The batch was cancelled; remaining folders were not touched."));
            }

            ScrollableMessageBox.Show(this, summary.ToString(), title, MessageBoxButtons.OK,
                result.Failed.Count > 0 || result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        static void AppendSection(StringBuilder sb, string heading, List<string> lines)
        {
            if (lines.Count == 0) return;
            sb.AppendLine().AppendLine();
            sb.AppendLine(heading);
            foreach (string line in lines) sb.AppendLine(line);
        }

        static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

        /// <summary>Paths named in an error message, e.g. "The process cannot access the file 'C:\x'".</summary>
        static IEnumerable<string> PathsInMessage(string message) =>
            Regex.Matches(message ?? "", @"'([A-Za-z]:\\[^']+)'").Cast<Match>().Select(m => m.Groups[1].Value);

        /// <summary>Programs running from inside any of the given folders (a quick check).</summary>
        static List<LockingProcess> RunningFrom(params string[] folders) =>
            ProcessFinder.GetProcessImagePaths()
                .Where(kv => folders.Any(f => ProcessFinder.IsUnder(kv.Value, f)))
                .Select(kv => ProcessFinder.Describe(kv.Key, kv.Value))
                .ToList();

        /// <summary>Returns true if the person cancelled.</summary>
        private async Task<bool> RunMoveQueue(List<MoveJob> jobs, Guid batchId, bool recommendedBatch,
            MoveBatchResult result, List<InUseItem> inUse)
        {
            var queue = new BatchQueue { Count = jobs.Count };
            for (int i = 0; i < jobs.Count; i++)
            {
                MoveJob job = jobs[i];
                if (queue.SkipRemaining)
                {
                    result.Skipped.AddRange(jobs.Skip(i).Select(x => x.Source));
                    break;
                }

                queue.Position = i + 1;
                queue.CurrentItem = job.Source;
                queue.SkipCurrent = false;

                // A program running from inside the folder will block the move: set it aside now
                // instead of copying gigabytes first
                List<LockingProcess> running = await Task.Run(() => RunningFrom(job.Source));
                if (running.Count > 0)
                {
                    inUse.Add(new InUseItem { DisplayPath = job.Source, CheckPath = job.Source, Processes = running, Payload = job });
                    continue;
                }

                MoveOutcome outcome = await MoveAndLinkOne(job, queue, batchId, recommendedBatch);
                if (outcome.Success)
                {
                    result.Moved.Add(job);
                    result.MovedBytes += outcome.SizeBytes;
                    result.Warnings.AddRange(outcome.Warnings.Select(w => $"{job.Source}\n    {w}"));
                }
                else if (outcome.Cancelled && queue.SkipCurrent)
                {
                    result.Skipped.Add(job.Source + (outcome.Error != null ? "\n    " + outcome.Error : ""));
                }
                else if (outcome.Cancelled)
                {
                    if (outcome.Error != null) result.Failed.Add($"{job.Source}\n    {outcome.Error}");
                    result.Skipped.AddRange(jobs.Skip(i + 1).Select(x => x.Source));
                    return true;
                }
                else
                {
                    // Was it failing because something has its files open?
                    List<LockingProcess> users = await Task.Run(() => ProcessFinder.FindUsers(job.Source, PathsInMessage(outcome.Error)));
                    if (users.Count > 0 && outcome.RestoredOriginal)
                        inUse.Add(new InUseItem { DisplayPath = job.Source, CheckPath = job.Source, Processes = users, Payload = job, LastError = outcome.Error });
                    else
                        result.Failed.Add($"{job.Source}\n    {outcome.Error}");
                }
            }
            return false;
        }

        /// <summary>Outcome of moving one item.</summary>
        private class MoveOutcome
        {
            public bool Success;
            public bool Cancelled;
            public string Error;
            /// <summary>The item is back exactly as it was before (so it can simply be retried).</summary>
            public bool RestoredOriginal = true;
            public long SizeBytes;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Moves one folder or file and links it back, recorded in a write-ahead journal so an
        /// interruption can be finished or rolled back on the next start. When the destination
        /// exists and <see cref="MoveJob.Overwrite"/> is set, files are merged into it (replaced
        /// files are backed up). On any failure everything is put back as it was, at both ends,
        /// and the folders created to hold the destination are removed again.
        /// </summary>
        private async Task<MoveOutcome> MoveAndLinkOne(MoveJob job, BatchQueue queue, Guid batchId, bool recommendedBatch)
        {
            string source = job.Source.TrimEnd('\\');
            string destination = job.Destination.TrimEnd('\\');

            if (PathExists(destination) && !job.Overwrite)
                return new MoveOutcome { Error = R("Error_DestNameExists", "Destination folder already contains a folder with the same name.") };

            List<string> createdParents = new List<string>();
            try
            {
                createdParents = IOHelper.CreateDirectoryTracked(Path.GetDirectoryName(destination));
                if (!job.ChecksDone)
                {
                    // Same checks as a manual move (protected folders, safe mode, permissions, free space)
                    IOHelper.CheckDirectories(source, destination, safeMode, createDestination: true,
                        skipAllChecks: skipSecurityChecks, allowExistingDestination: job.Overwrite);
                }
            }
            catch (AggregateException ae) when (!ae.InnerExceptions.All(x => x is IOHelper.ReadOnlyPrecheckException))
            {
                IOHelper.RemoveEmptyDirectories(createdParents);
                return new MoveOutcome { Error = string.Join("; ", ae.InnerExceptions.Select(x => x.Message)) };
            }
            catch (AggregateException)
            {
                // Only read-only files were reported: they move fine (the flag is cleared before
                // the original is deleted), so carry on
            }
            catch (Exception ex)
            {
                IOHelper.RemoveEmptyDirectories(createdParents);
                return new MoveOutcome { Error = ex.Message };
            }

            var entry = new MoveEntry { LinkPath = source, TargetPath = destination, IsFile = File.Exists(source), SizeBytes = job.SizeBytes };
            MoveJournal journal;
            try
            {
                journal = MoveJournal.BeginMove(source, destination, entry.Id, batchId, recommendedBatch, job.SizeBytes);
            }
            catch (Exception ex)
            {
                IOHelper.RemoveEmptyDirectories(createdParents);
                return new MoveOutcome { Error = string.Format(R("Journal_CannotWrite", "Couldn't write the safety record, so nothing was moved: {0}"), ex.Message) };
            }

            MoveOperation op = null;
            try
            {
                await BeginMove(source, destination, createDestination: true, queue: queue, overwrite: job.Overwrite, journal: journal, created: o => op = o);
            }
            catch (MoveOperation.DeleteFailedException ex)
            {
                // The copy is complete, but part of the original couldn't be deleted (usually a file
                // in use). Put the original back together so the program works again.
                string back = await PutBack(journal, destination, source);
                if (back == null) CleanUpAfterRollback(journal, op, createdParents);
                return new MoveOutcome
                {
                    Error = ex.Message + " " + ex.InnerException?.Message +
                            (back == null ? "" : " " + string.Format(R("Rec_MoveBackFailed", "Moving back also failed ({0}); the files are split between {1} and {2}. FreeMove will finish this the next time it starts."), back, source, destination)),
                    RestoredOriginal = back == null
                };
            }
            catch (Exception ex)
            {
                // Copy failed, was cancelled or skipped, or a same-drive move failed: the original is
                // still complete, so only the destination needs undoing
                bool cancelled = ex is OperationCanceledException;
                string rollbackProblem = RollBackCopy(journal, source, destination);
                if (rollbackProblem == null) CleanUpAfterRollback(journal, op, createdParents);
                else journal.Close();   // kept: the next start retries the rollback

                string message = cancelled ? null : ex.Message + " " + ex.InnerException?.Message;
                if (rollbackProblem != null) message = (message == null ? "" : message + " ") + rollbackProblem;
                return new MoveOutcome { Cancelled = cancelled, Error = message };
            }

            // Leave a link in the original location. If that fails, the program would find nothing
            // there, so move everything back.
            if (!IOHelper.MakeLink(destination, source))
            {
                string linkError = new System.ComponentModel.Win32Exception().Message;
                string back = await PutBack(journal, destination, source);
                if (back != null)
                    return new MoveOutcome { RestoredOriginal = false, Error = string.Format(R("Rec_LinkFailedStranded", "Could not create the link ({0}) and could not move the folder back ({1}). Its files are now in {2}. FreeMove will finish this the next time it starts."), linkError, back, destination) };
                CleanUpAfterRollback(journal, op, createdParents);
                return new MoveOutcome { Error = string.Format(R("Rec_LinkFailed", "Could not create the link ({0}); the folder was moved back."), linkError) };
            }

            if (chkBox_originalHidden.Checked)
            {
                try
                {
                    var link = new DirectoryInfo(source);
                    link.Attributes |= FileAttributes.Hidden;
                }
                catch { }
            }

            // Record it, then mark the journal done (a crash in between is completed on the next start)
            long size = job.SizeBytes > 0 ? job.SizeBytes : await Task.Run(() => MeasureMoved(journal, destination));
            entry.SizeBytes = size;
            entry.MergeJournalPath = journal.IsMerge ? journal.FilePath : null;
            MoveHistory.AddEntry(batchId, entry, recommendedBatch, isUndoBatch: false);
            journal.Complete();

            var outcome = new MoveOutcome { Success = true, SizeBytes = size };
            if (op != null) outcome.Warnings.AddRange(op.Warnings.Distinct().Select(w => R("Copy_PermissionsWarning", "Some permissions weren't copied: ") + w));
            if (journal.IsMerge && journal.Replaced.Count > 0)
                outcome.Warnings.Add(string.Format(R("Journal_BackupNote", "{0} replaced file(s) are kept in {1} so the move can be undone. You can delete these backups from History."), journal.Replaced.Count, journal.BackupRoot));
            return outcome;
        }

        /// <summary>Size of what a move added: for a merge only its own files, otherwise the whole destination.</summary>
        static long MeasureMoved(MoveJournal journal, string destination)
        {
            try
            {
                if (File.Exists(destination)) return new FileInfo(destination).Length;
                if (!journal.IsMerge) return RecommendationScanner.GetDirectorySize(destination, CancellationToken.None);
                return journal.Copied.Distinct().Sum(rel =>
                {
                    try { return new FileInfo(rel.Length == 0 ? destination : Path.Combine(destination, rel)).Length; }
                    catch { return 0L; }
                });
            }
            catch { return 0; }
        }

        /// <summary>
        /// After a failed or cancelled copy (the original is still complete): removes what was
        /// copied. For a merge, only the files this move added are removed and replaced files are
        /// restored. Returns a problem description, or null when everything was undone.
        /// </summary>
        static string RollBackCopy(MoveJournal journal, string source, string destination)
        {
            if (journal.IsMerge)
            {
                List<string> errors = journal.RollBackDestination();
                return errors.Count == 0 ? null : string.Join("\n", errors);
            }
            try
            {
                // Only while the original is still there: if it's gone (a finished same-drive
                // rename), the destination is the only copy and must stay
                if (!PathExists(source) || NativeFs.IsLink(source))
                    return string.Format(R("Rec_OnlyCopyKept", "The files are now only in {0}; they were left there."), destination);
                if (PathExists(destination)) IOHelper.DeleteOriginal(destination);
                return null;
            }
            catch (Exception ex)
            {
                return string.Format(R("Rec_PartialCopyLeft", "The partial copy at {0} couldn't be removed ({1}); the original is unchanged."), destination, ex.Message);
            }
        }

        static void CleanUpAfterRollback(MoveJournal journal, MoveOperation op, List<string> createdParents)
        {
            journal.DeleteFile();
            op?.RemoveCreatedParents();
            IOHelper.RemoveEmptyDirectories(createdParents);
        }

        /// <summary>
        /// Moves a finished copy back to <paramref name="source"/> (after the original was partly
        /// or fully deleted). For a merge, only the files this move added go back and replaced
        /// files are restored at the destination. Returns null on success, or the error.
        /// </summary>
        private async Task<string> PutBack(MoveJournal journal, string destination, string source)
        {
            try
            {
                // Recorded first: if this is interrupted, the next start keeps putting the files back
                // instead of finishing the move (which would delete the original)
                journal.SetPhase(JournalPhase.PutBack);
                if (journal.IsMerge)
                    await RunOperation(new JournalUndoOperation(journal, source, keepMovedCopy: false));
                else
                    await BeginMove(destination, source, createDestination: true, keepExisting: true);
                return null;
            }
            catch (Exception ex)
            {
                journal.Close();   // kept unfinished: the next start finishes the move
                return ex.InnerException?.Message ?? ex.Message;
            }
        }
    }
}
