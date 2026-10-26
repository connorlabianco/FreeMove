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
using System.Windows.Forms;
using FreeMove.Dialogs;
using FreeMove.History;
using FreeMove.InUse;
using FreeMove.IO;
using FreeMove.Recommendations;

namespace FreeMove
{
    // Undoing moves (from History, and "Restore symbolic link" in the folder browser).
    public partial class Form1
    {
        private void HistoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowHistory();
        }

        HistoryForm historyWindow;

        /// <summary>
        /// Opens History as its own window (with its own taskbar button), so you can switch between
        /// it and the main window. Only one is open at a time.
        /// </summary>
        private void ShowHistory()
        {
            if (historyWindow != null && !historyWindow.IsDisposed)
            {
                ActivateWindow(historyWindow);
                return;
            }

            var form = new HistoryForm { IsBusy = () => IsBusy };
            form.UndoAccepted += async (s, entries) =>
            {
                if (IsBusy) { ShowBusyMessage(this); return; }
                if (entries.Count > 0) await UndoEntries(entries);
            };
            form.FormClosed += (s, e) => { if (ReferenceEquals(historyWindow, s)) historyWindow = null; };
            historyWindow = form;
            ShowSideWindow(form);
        }

        /// <summary>
        /// Moves each entry back to its original location and removes the link, or, with
        /// <paramref name="keepMovedCopy"/>, copies it back and leaves the moved copy in place.
        /// Items programs are using are set aside and offered again at the end, with the option to
        /// end those programs. Failed or skipped items are left as they were and the rest continue;
        /// "Skip remaining" stops before the next item; cancelling stops. A summary is shown at the end.
        /// </summary>
        private async Task UndoEntries(List<MoveEntry> entries, bool keepMovedCopy = false)
        {
            if (IsBusy) { ShowBusyMessage(this); return; }
            SetBusy(true);   // held until after the summary, so closing the window waits for all of it
            try
            {
                await UndoEntriesCore(entries, keepMovedCopy);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task UndoEntriesCore(List<MoveEntry> entries, bool keepMovedCopy)
        {
            var undone = new List<MoveEntry>();
            var failed = new List<string>();
            var warnings = new List<string>();
            var skipped = new List<string>();
            bool cancelled = false;
            Guid batchId = Guid.NewGuid();

            try
            {
                var inUse = new List<InUseItem>();
                cancelled = await RunUndoQueue(entries, keepMovedCopy, batchId, undone, failed, warnings, skipped, inUse);

                while (!cancelled && inUse.Count > 0)
                {
                    if (closeWhenIdle)
                    {
                        skipped.AddRange(inUse.Select(i => i.DisplayPath + "  " + R("InUse_SkippedNote", "(in use)")));
                        break;
                    }

                    InUseChoice choice;
                    using (var form = new InUseForm(inUse))
                    {
                        PrepareDialog(form);
                        form.ShowDialog(this);
                        choice = form.Choice;
                    }

                    if (choice == InUseChoice.Skip)
                    {
                        skipped.AddRange(inUse.Select(i => i.DisplayPath + "  " + R("InUse_SkippedNote", "(in use)")));
                        break;
                    }
                    if (choice == InUseChoice.EndTasksAndRetry)
                        await EndTasks(inUse);

                    List<MoveEntry> retry = inUse.Select(i => (MoveEntry)i.Payload).ToList();
                    inUse = new List<InUseItem>();
                    cancelled = await RunUndoQueue(retry, keepMovedCopy, batchId, undone, failed, warnings, skipped, inUse);
                }
            }
            finally
            {
                directoryBrowser1.RefreshBrowser();
            }

            if (closeWhenIdle) return;   // closing: the results are in History

            var summary = new StringBuilder();
            summary.AppendFormat(R("Hist_SummaryUndone", "Moved {0} item(s), {1} in total, back to their original location."),
                undone.Count, RecommendationScanner.FormatSize(undone.Sum(u => u.SizeBytes)));
            AppendSection(summary, R("Hist_SummaryWarnings", "Moved back, with these notes:"), warnings);
            AppendSection(summary, R("Hist_SummaryFailed", "These were not moved back and were left as they were:"), failed);
            AppendSection(summary, R("Hist_SummarySkipped", "Skipped (still moved, links unchanged):"), skipped);
            if (cancelled)
            {
                summary.AppendLine().AppendLine();
                summary.Append(R("Hist_SummaryCancelled", "Undo was cancelled; remaining items were not touched."));
            }

            BringMainWindowForward();
            ScrollableMessageBox.Show(this, summary.ToString(), R("Hist_Title", "Move history"),
                MessageBoxButtons.OK, failed.Count > 0 || warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>Returns true if the person cancelled.</summary>
        private async Task<bool> RunUndoQueue(List<MoveEntry> entries, bool keepMovedCopy, Guid batchId, List<MoveEntry> undone,
            List<string> failed, List<string> warnings, List<string> skipped, List<InUseItem> inUse)
        {
            var queue = new BatchQueue { Count = entries.Count, SkipRemaining = closeWhenIdle };
            currentQueue = queue;
            for (int i = 0; i < entries.Count; i++)
            {
                MoveEntry entry = entries[i];
                if (queue.SkipRemaining)
                {
                    skipped.AddRange(entries.Skip(i).Select(x => x.LinkPath));
                    break;
                }

                queue.Position = i + 1;
                queue.CurrentItem = entry.LinkPath;
                queue.SkipCurrent = false;

                // Quick check: a program running from the folder (through the link or directly)
                List<LockingProcess> running = await Task.Run(() => RunningFrom(entry.LinkPath, entry.TargetPath));
                if (running.Count > 0)
                {
                    inUse.Add(new InUseItem { DisplayPath = entry.LinkPath, CheckPath = entry.TargetPath, Processes = running, Payload = entry });
                    continue;
                }

                UndoResult result = await UndoOne(entry, queue, batchId, keepMovedCopy);
                if (result.Cancelled)
                    Log.Info($"Undo {(queue.SkipCurrent ? "skipped" : "cancelled")}: {entry.LinkPath}" + (result.Error != null ? ": " + result.Error : ""));
                else if (result.Error != null)
                    Log.Error($"Undo failed: {entry.TargetPath} -> {entry.LinkPath}: {result.Error}");
                else
                    Log.Info($"Moved back {entry.TargetPath} -> {entry.LinkPath}" + (keepMovedCopy ? " (kept the moved copy)" : "") +
                             (result.Warning != null ? "; note: " + result.Warning : ""));

                if (result.Cancelled && queue.SkipCurrent)
                {
                    skipped.Add(entry.LinkPath + (result.Error != null ? "\n    " + result.Error : ""));
                    continue;
                }
                if (result.Cancelled)
                {
                    if (result.Error != null) failed.Add($"{entry.LinkPath}\n    {result.Error}");
                    skipped.AddRange(entries.Skip(i + 1).Select(x => x.LinkPath));
                    return true;
                }
                if (result.Error != null)
                {
                    List<LockingProcess> users = await Task.Run(() => ProcessFinder.FindUsers(entry.TargetPath, PathsInMessage(result.Error)));
                    if (users.Count > 0 && result.LinkRestored)
                        inUse.Add(new InUseItem { DisplayPath = entry.LinkPath, CheckPath = entry.TargetPath, Processes = users, Payload = entry, LastError = result.Error });
                    else
                        failed.Add($"{entry.LinkPath}\n    {result.Error}");
                    continue;
                }

                undone.Add(entry);
                if (result.Warning != null) warnings.Add($"{entry.LinkPath}\n    {result.Warning}");
            }
            return false;
        }

        private class UndoResult
        {
            public string Error;
            public string Warning;
            public bool Cancelled;
            /// <summary>After a failure: the link is back in place, so nothing changed.</summary>
            public bool LinkRestored = true;
        }

        /// <summary>
        /// Undoes one move. The link is set aside, the files are copied back to the original path
        /// and verified, and only then is the moved copy removed (unless it is kept) and the link
        /// deleted. Every step is journaled, so an interruption is finished or rolled back on the
        /// next start. A merged move brings back only the files it added and restores what it replaced.
        /// </summary>
        private async Task<UndoResult> UndoOne(MoveEntry entry, BatchQueue queue, Guid batchId, bool keepMovedCopy)
        {
            string link = entry.LinkPath.TrimEnd('\\');
            string target = entry.TargetPath.TrimEnd('\\');
            bool isFile = entry.IsFile;

            if (MoveHistory.GetLiveState(entry) != MoveEntryLiveState.Moved)
                return new UndoResult { Error = R("Hist_ErrChanged", "The link or the moved files changed since the move, so it can't be undone automatically.") };

            // A merged move is undone from its journal, so only the files it added go back.
            // Without the journal that can't be done safely, so stop.
            MoveJournal merge = null;
            if (entry.MergeJournalPath != null)
            {
                try { merge = MoveJournal.Load(entry.MergeJournalPath); }
                catch (Exception ex)
                {
                    return new UndoResult
                    {
                        Error = string.Format(R("Hist_ErrMergeRecord", "This move was merged into an existing folder, and its record of which files were moved can't be read ({0}), so it can't be undone without disturbing the files that were already there."), ex.Message)
                    };
                }

                // Files saved into the folder after the move belong to neither side's record: ask
                List<string> added = await Task.Run(() => merge.FindFilesAddedSinceMove());
                if (added.Count > 0)
                {
                    string list = string.Join("\n", added.Select(a => Path.Combine(target, a)));
                    string question = string.Format(R("Hist_AddedSinceQuestion",
                        "{0} file(s) were saved into {1} after it was moved:\n\n{2}\n\nBring them back to {3} too?\n\nYes: bring them back with the rest\nNo: leave them in {1}"),
                        added.Count, target, list, link);
                    if (ScrollableMessageBox.Show(this, question, R("Hist_UndoTitle", "Undo moves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        try { merge.IncludeInUndo(added); }
                        catch (Exception ex) { return new UndoResult { Error = ex.Message }; }
                    }
                }
            }

            // Make sure the original drive has room
            try
            {
                long size = entry.SizeBytes;
                if (size <= 0)
                    size = isFile ? new FileInfo(target).Length
                                  : await Task.Run(() => RecommendationScanner.GetDirectorySize(target, CancellationToken.None));
                var drive = new DriveInfo(Path.GetPathRoot(link));
                if (drive.AvailableFreeSpace < size)
                    return new UndoResult
                    {
                        Error = string.Format(R("Hist_ErrSpace", "Not enough free space on {0}: needs {1}, {2} free."),
                            drive.Name, RecommendationScanner.FormatSize(size), RecommendationScanner.FormatSize(drive.AvailableFreeSpace))
                    };
            }
            catch (Exception ex)
            {
                return new UndoResult { Error = ex.Message };
            }

            // Journal first, then set the link aside so the original path is free for the files
            string parkedLink = link + ".freemove_link_" + DateTime.Now.Ticks;
            MoveJournal journal;
            try
            {
                journal = MoveJournal.BeginUndo(keepMovedCopy, link, target, parkedLink, isFile, entry.MergeJournalPath, entry.Id, batchId);
            }
            catch (Exception ex)
            {
                return new UndoResult { Error = string.Format(R("Journal_CannotWrite", "Couldn't write the safety record, so nothing was moved: {0}"), ex.Message) };
            }

            try
            {
                if (isFile) File.Move(link, parkedLink);
                else Directory.Move(link, parkedLink);
            }
            catch (Exception ex)
            {
                journal.DeleteFile();
                return new UndoResult { Error = ex.Message };
            }

            string warning = null;
            try
            {
                if (merge != null)
                {
                    var op = new JournalUndoOperation(merge, link, keepMovedCopy);
                    op.CopyCompleted += (s, e) => journal.SetPhase(JournalPhase.Delete);
                    await RunOperation(op, queue);
                }
                else if (keepMovedCopy)
                {
                    await RunOperation(new CopyOperation(target, link), queue);
                    journal.SetPhase(JournalPhase.Delete);
                }
                else
                {
                    await BeginMove(target, link, createDestination: true, queue: queue,
                        created: o => o.CopyCompleted += (s, e) => journal.SetPhase(JournalPhase.Delete));
                }
            }
            catch (MoveOperation.DeleteFailedException ex)
            {
                // Everything was copied back and verified; only removing the moved copy failed
                warning = (ex.InnerException?.Message ?? ex.Message) + " " +
                          string.Format(R("Hist_WarnLeftover", "Leftover files may remain in {0}."), target);
            }
            catch (Exception ex) when (!Exists(target) && Exists(link) && !NativeFs.IsLink(link))
            {
                // The moved copy is gone and the files are at the original path: a same-drive move back
                // (a rename) finished and something failed afterwards. The undo is done; finish it.
                warning = ex.Message;
            }
            catch (Exception ex)
            {
                // Cancelled, skipped or the copy failed: the moved files are intact, so put the link back
                bool cancelled = ex is OperationCanceledException;
                string restoreError = RestoreParkedLink(link, parkedLink, isFile, target);
                if (restoreError == null) journal.DeleteFile();
                else journal.Close();   // kept: the next start puts the link back

                string message = cancelled ? null : ex.Message + " " + ex.InnerException?.Message;
                if (restoreError != null)
                    message = (message == null ? "" : message + " ") + string.Format(R("Hist_ErrLinkNotRestored", "The link couldn't be put back ({0}); FreeMove will do it the next time it starts."), restoreError);
                return new UndoResult { Cancelled = cancelled, Error = message, LinkRestored = restoreError == null };
            }

            // Files are back at the original path: remove the parked link (not what it points to)
            try
            {
                if (isFile) File.Delete(parkedLink);
                else Directory.Delete(parkedLink, false);
            }
            catch (Exception ex)
            {
                warning = (warning == null ? "" : warning + " ") +
                          string.Format(R("Hist_WarnLinkLeft", "The old link {0} could not be removed ({1}); you can delete it manually."), parkedLink, ex.Message);
            }

            // Record it: the move is undone, and the undo itself is logged as its own entry
            MoveHistory.MarkUndone(new[] { entry.Id });
            MoveHistory.AddEntry(batchId, new MoveEntry
            {
                LinkPath = entry.LinkPath,
                TargetPath = entry.TargetPath,
                SizeBytes = entry.SizeBytes,
                IsFile = entry.IsFile,
                State = MoveEntryState.Undone,
                UndoneAt = DateTime.Now
            }, isRecommendedBatch: false, isUndoBatch: true);

            // A fully undone merge no longer needs its journal (its backups were restored)
            if (merge != null && !keepMovedCopy && warning == null) merge.DeleteFile();
            journal.Complete();

            return new UndoResult { Warning = warning };
        }

        /// <summary>
        /// Removes a partial copy at the original path and puts the link back. Returns null on
        /// success, or what went wrong. The partial copy is only removed while the moved copy at
        /// <paramref name="target"/> still exists, so this can never delete the only copy.
        /// </summary>
        private static string RestoreParkedLink(string link, string parkedLink, bool isFile, string target)
        {
            if (!Exists(target))
                return string.Format(R("Hist_ErrTargetGone", "{0} no longer exists, so the files at {1} were left untouched."), target, link);
            try
            {
                if (isFile)
                {
                    if (File.Exists(link) && !NativeFs.IsLink(link)) { File.SetAttributes(link, FileAttributes.Normal); File.Delete(link); }
                    File.Move(parkedLink, link);
                }
                else
                {
                    if (Directory.Exists(link) && !NativeFs.IsLink(link)) IOHelper.DeleteOriginal(link);
                    Directory.Move(parkedLink, link);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
