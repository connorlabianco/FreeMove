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
using System.Threading.Tasks;
using System.Windows.Forms;
using FreeMove.Dialogs;
using FreeMove.History;
using FreeMove.IO;

namespace FreeMove
{
    // Finishing or rolling back operations that were interrupted (FreeMove closed or the PC
    // stopped), from their write-ahead journals. Runs when FreeMove starts.
    public partial class Form1
    {
        private async Task RecoverInterruptedOperations()
        {
            List<MoveJournal> pending;
            try { pending = MoveJournal.LoadUnfinished(); }
            catch { return; }
            if (pending.Count == 0) return;

            var report = new List<string>();
            await RunOperation(new DelegateOperation(() => Task.Run(() =>
            {
                foreach (MoveJournal journal in pending)
                {
                    try
                    {
                        string message = journal.Kind == JournalKind.Move ? RecoverMove(journal) : RecoverUndo(journal);
                        if (message != null) report.Add(message);
                    }
                    catch (Exception ex)
                    {
                        // The journal stays, so the next start tries again
                        journal.Close();
                        report.Add(string.Format(R("Recovery_Failed", "{0}: couldn't be finished yet ({1}). FreeMove will try again next time."), journal.SourceRoot, ex.Message));
                    }
                }
            })));

            foreach (string line in report) Log.Info("Recovery: " + line);
            if (report.Count > 0)
            {
                ScrollableMessageBox.Show(this,
                    R("Recovery_Intro", "Some operations were interrupted the last time FreeMove ran. They have been finished or rolled back:") + "\n\n" + string.Join("\n\n", report),
                    R("Recovery_Title", "Interrupted operations"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            directoryBrowser1.RefreshBrowser();
        }

        static bool SameLocation(string a, string b)
        {
            string ra = IOHelper.GetSymbolicLinkTarget(a) ?? a;
            string rb = IOHelper.GetSymbolicLinkTarget(b) ?? b;
            return string.Equals(Path.GetFullPath(ra).TrimEnd('\\'), Path.GetFullPath(rb).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        /// <summary>Adds the move to History if it isn't there yet.</summary>
        static void EnsureInHistory(MoveJournal journal)
        {
            if (MoveHistory.ContainsEntry(journal.HistoryEntryId)) return;
            string destination = journal.DestinationRoot;
            long size = journal.SizeBytes;
            if (size <= 0) size = MeasureMoved(journal, destination);
            MoveHistory.AddEntry(journal.HistoryBatchId, new MoveEntry
            {
                Id = journal.HistoryEntryId,
                LinkPath = journal.SourceRoot,
                TargetPath = destination,
                IsFile = journal.IsFile,
                SizeBytes = size,
                MergeJournalPath = journal.IsMerge ? journal.FilePath : null
            }, journal.RecommendedBatch, isUndoBatch: false);
        }

        /// <summary>
        /// An interrupted move:
        /// - link already in place: only the bookkeeping was missing, so finish it;
        /// - interrupted while putting files back after a failure: keep putting them back;
        /// - copy finished (deleting the original, or a finished same-drive rename): finish the move;
        /// - copy not finished (the original is untouched): roll the destination back.
        /// </summary>
        string RecoverMove(MoveJournal journal)
        {
            string source = journal.SourceRoot;
            string destination = journal.DestinationRoot;

            if (NativeFs.IsLink(source) && SameLocation(source, destination))
            {
                EnsureInHistory(journal);
                journal.Complete();
                return string.Format(R("Recovery_MoveFinished", "Finished moving {0} to {1}."), source, destination);
            }

            bool sourceExists = Exists(source) && !NativeFs.IsLink(source);
            bool destinationExists = Exists(destination);

            if (journal.Phase == JournalPhase.PutBack)
            {
                // Bring every file back to the original (what is there is kept), then remove the copy
                if (journal.IsMerge)
                {
                    journal.CopyMovedFilesBack(source, null, CancellationToken.None);
                    journal.RemoveMovedFilesFromDestination();
                }
                else if (destinationExists)
                {
                    var copy = new CopyOperation(destination, source) { KeepExistingFiles = true };
                    copy.Run().GetAwaiter().GetResult();
                    IOHelper.DeleteOriginal(destination);
                }
                journal.DeleteFile();
                return string.Format(R("Recovery_MoveRolledBack", "Put {0} back where it was; it was not moved."), source);
            }

            // (A finished same-drive rename can only be a non-merge move: merges always copy)
            if (journal.Phase == JournalPhase.Delete || (!journal.IsMerge && !sourceExists && destinationExists))
            {
                // The copy completed and was verified before anything was deleted: finish the move
                if (!destinationExists)
                    throw new IOException(string.Format(R("Recovery_MissingBoth", "Neither {0} nor {1} could be found."), source, destination));
                if (sourceExists) IOHelper.DeleteOriginal(source);
                if (!IOHelper.MakeLink(destination, source))
                    throw new IOException(string.Format(R("Recovery_LinkFailed", "The files are safe in {0}, but the link at {1} couldn't be created ({2})."), destination, source, new System.ComponentModel.Win32Exception().Message));
                EnsureInHistory(journal);
                journal.Complete();
                return string.Format(R("Recovery_MoveFinished", "Finished moving {0} to {1}."), source, destination);
            }

            // The copy didn't finish: the original is complete, so undo what reached the destination
            if (journal.IsMerge)
            {
                List<string> errors = journal.RollBackDestination();
                if (errors.Count > 0) throw new IOException(string.Join("\n", errors));
            }
            else if (destinationExists && sourceExists)
            {
                IOHelper.DeleteOriginal(destination);
            }
            journal.DeleteFile();
            return string.Format(R("Recovery_MoveRolledBack", "Put {0} back where it was; it was not moved."), source);
        }

        /// <summary>
        /// An interrupted undo or restore:
        /// - still copying back: remove the partial copy and put the link back (still moved);
        /// - copy back finished: finish removing the moved copy (for an undo) and the old link.
        /// </summary>
        string RecoverUndo(MoveJournal journal)
        {
            string link = journal.SourceRoot;
            string target = journal.DestinationRoot;
            string parked = journal.ParkedLink;
            bool undo = journal.Kind == JournalKind.Undo;

            MoveJournal merge = null;
            if (journal.MergeJournalPath != null && File.Exists(journal.MergeJournalPath))
                merge = MoveJournal.Load(journal.MergeJournalPath);

            bool parkedExists = parked != null && Exists(parked);

            if (!parkedExists)
            {
                if (NativeFs.IsLink(link) || !Exists(link))
                {
                    // Interrupted before the link was set aside: nothing changed
                    journal.DeleteFile();
                    return null;
                }
                // The link was already removed, so the copy back had finished; finish the cleanup
                if (undo && journal.Phase == JournalPhase.Delete)
                {
                    RemoveMovedCopy(merge, target);
                    merge?.DeleteFile();
                }
                MoveHistory.MarkUndone(new[] { journal.HistoryEntryId });
                journal.DeleteFile();
                return string.Format(R("Recovery_UndoFinished", "Finished moving {0} back."), link);
            }

            if (journal.Phase == JournalPhase.Copy && !Exists(target) && Exists(link) && !NativeFs.IsLink(link))
            {
                // A same-drive move back (a rename) finished before the phase was recorded
                if (journal.IsFile) File.Delete(parked);
                else Directory.Delete(parked, false);
                MoveHistory.MarkUndone(new[] { journal.HistoryEntryId });
                journal.DeleteFile();
                return string.Format(R("Recovery_UndoFinished", "Finished moving {0} back."), link);
            }

            if (journal.Phase == JournalPhase.Copy)
            {
                string error = RestoreParkedLink(link, parked, journal.IsFile, target);
                if (error != null) throw new IOException(error);
                journal.DeleteFile();
                return string.Format(R("Recovery_UndoRolledBack", "{0} is still moved; the interrupted undo was rolled back."), link);
            }

            // The files were copied back and verified: finish
            if (undo) RemoveMovedCopy(merge, target);
            if (journal.IsFile) File.Delete(parked);
            else Directory.Delete(parked, false);
            MoveHistory.MarkUndone(new[] { journal.HistoryEntryId });
            if (undo) merge?.DeleteFile();
            journal.DeleteFile();
            return string.Format(R("Recovery_UndoFinished", "Finished moving {0} back."), link);
        }

        static void RemoveMovedCopy(MoveJournal merge, string target)
        {
            if (merge != null) merge.RemoveMovedFilesFromDestination();
            else if (Exists(target)) IOHelper.DeleteOriginal(target);
        }
    }
}
