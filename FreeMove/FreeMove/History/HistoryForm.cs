// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FreeMove.Recommendations;

namespace FreeMove.History
{
    /// <summary>
    /// Shows past actions, newest first. Every action (a manual move, a Recommended moves batch,
    /// an undo) is a row that expands to its items, so a batch can be undone as a whole or only
    /// some of its folders.
    /// On OK the caller undoes <see cref="SelectedEntries"/>.
    /// </summary>
    public class HistoryForm : Form
    {
        readonly TreeView tree;
        readonly Label label_Status;
        readonly Button button_Undo;
        readonly Button button_Clear;
        readonly Button button_Close;
        readonly Button button_DeleteBackups;
        readonly Button button_Health;
        readonly Button button_UndoAll;
        readonly Button button_FindMissing;
        readonly Label label_Saved;
        bool updatingChecks;

        // Disk checks are done once when the list loads, not on every click
        readonly Dictionary<Guid, MoveEntryLiveState> states = new Dictionary<Guid, MoveEntryLiveState>();
        readonly Dictionary<Guid, long> backupSizes = new Dictionary<Guid, long>();

        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        /// <summary>The ticked entries that can be undone.</summary>
        public List<MoveEntry> SelectedEntries
        {
            get
            {
                var result = new List<MoveEntry>();
                foreach (TreeNode node in AllNodes(tree.Nodes))
                {
                    if (node.Checked && node.Tag is MoveEntry entry && IsUndoable(entry))
                        result.Add(entry);
                }
                return result;
            }
        }

        /// <summary>Asked before undoing: true while the main window is already moving files.</summary>
        public Func<bool> IsBusy { get; set; }

        /// <summary>Raised (after this window closes) with the entries the person chose to undo.</summary>
        public event EventHandler<List<MoveEntry>> UndoAccepted;

        public HistoryForm()
        {
            Text = T("Hist_Title", "Move history");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(640, 380);
            Size = new Size(820, 520);
            ShowIcon = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            tree = new TreeView
            {
                Dock = DockStyle.Fill,
                CheckBoxes = true,
                HideSelection = false,
                ShowNodeToolTips = true
            };
            tree.BeforeCheck += Tree_BeforeCheck;
            tree.AfterCheck += Tree_AfterCheck;
            tree.AfterSelect += (s, e) => UpdateBackupButton();

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 5,
                Padding = new Padding(8, 4, 8, 8)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            label_Status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(440, 0) };
            button_Clear = new Button { Text = T("Hist_Clear", "Clear finished"), AutoSize = true };
            button_Clear.Click += Clear_Click;
            button_Undo = new Button { Text = T("Hist_UndoSelected", "Undo selected"), AutoSize = true, Enabled = false };
            button_Undo.Click += Undo_Click;
            button_Close = new Button { Text = T("Button_Close", "Close"), AutoSize = true };
            button_Close.Click += (s, e) => Close();   // this is an independent window, not a dialog
            button_DeleteBackups = new Button { Text = T("Hist_DeleteBackups", "Delete backups"), AutoSize = true, Enabled = false };
            button_DeleteBackups.Click += DeleteBackups_Click;

            bottom.Controls.Add(label_Status, 0, 0);
            bottom.Controls.Add(button_DeleteBackups, 1, 0);
            bottom.Controls.Add(button_Clear, 2, 0);
            bottom.Controls.Add(button_Undo, 3, 0);
            bottom.Controls.Add(button_Close, 4, 0);

            // --- Top: what's moved now, and tools for the whole history ---
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                Padding = new Padding(8, 8, 8, 4)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            label_Saved = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(420, 0) };
            button_Health = new Button { Text = T("Hist_HealthCheck", "Health check"), AutoSize = true };
            button_Health.Click += async (s, e) => await HealthCheckAsync();
            button_FindMissing = new Button { Text = T("Hist_FindMissing", "Find missing moves"), AutoSize = true };
            button_FindMissing.Click += async (s, e) => await FindMissingAsync();
            button_UndoAll = new Button { Text = T("Hist_UndoAll", "Move everything back"), AutoSize = true };
            button_UndoAll.Click += UndoAll_Click;

            var tips = new ToolTip();
            tips.SetToolTip(button_Health, T("Hist_HealthTip", "Check that every moved folder still has its link and that the moved files are reachable."));
            tips.SetToolTip(button_FindMissing, T("Hist_FindMissingTip", "Look for links to other drives that aren't in this list (for example after History was lost) and add them."));
            tips.SetToolTip(button_UndoAll, T("Hist_UndoAllTip", "Move every moved folder back to where it was. Do this before uninstalling FreeMove or wiping the other drive."));

            top.Controls.Add(label_Saved, 0, 0);
            top.Controls.Add(button_Health, 1, 0);
            top.Controls.Add(button_FindMissing, 2, 0);
            top.Controls.Add(button_UndoAll, 3, 0);

            Controls.Add(tree);
            Controls.Add(bottom);
            Controls.Add(top);
            CancelButton = button_Close;

            LoadTree();
        }

        void LoadTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            states.Clear();
            backupSizes.Clear();

            MoveHistoryData data = MoveHistory.Load();
            foreach (MoveEntry entry in data.Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries))
            {
                states[entry.Id] = MoveHistory.GetLiveState(entry);
                if (entry.MergeJournalPath != null && File.Exists(entry.MergeJournalPath))
                {
                    try { backupSizes[entry.Id] = IO.MoveJournal.Load(entry.MergeJournalPath).BackupSizeBytes(); }
                    catch { }
                }
            }

            foreach (MoveBatch batch in data.Batches.OrderByDescending(b => b.Time))
            {
                if (batch.Entries.Count == 0) continue;

                // Every action is an expandable row: its items are listed underneath
                string title = batch.IsUndoBatch ? T("Hist_BatchUndo", "Undo")
                             : batch.IsRecommendedBatch ? T("Hist_BatchRecommended", "Recommended moves")
                             : T("Hist_BatchManual", "Move");
                long total = batch.Entries.Sum(e => e.SizeBytes);

                // A single item names its folder; a batch gives the count
                string summary = batch.Entries.Count == 1
                    ? batch.Entries[0].LinkPath + (total > 0 ? ", " + RecommendationScanner.FormatSize(total) : "")
                    : string.Format(T("Hist_ItemsFormat", "{0} items, {1}"), batch.Entries.Count, RecommendationScanner.FormatSize(total));

                var batchNode = new TreeNode($"{FormatTime(batch.Time)}   {title}: {summary}") { Tag = batch };
                foreach (MoveEntry entry in batch.Entries.OrderByDescending(e => e.SizeBytes))
                    batchNode.Nodes.Add(MakeEntryNode(entry, batch.IsUndoBatch));

                if (batch.IsUndoBatch || !batch.Entries.Any(IsUndoable)) batchNode.ForeColor = SystemColors.GrayText;
                tree.Nodes.Add(batchNode);
            }

            tree.EndUpdate();

            if (tree.Nodes.Count == 0)
                label_Status.Text = T("Hist_Empty", "No moves yet. Moves you make with FreeMove will appear here.");

            // How much is moved off each drive right now
            List<MoveEntry> moved = data.Batches.Where(b => !b.IsUndoBatch).SelectMany(b => b.Entries)
                .Where(en => StateOf(en) == MoveEntryLiveState.Moved).ToList();
            string perDrive = string.Join(", ", moved
                .GroupBy(en => (Path.GetPathRoot(en.LinkPath) ?? "").ToUpperInvariant())
                .Select(g => string.Format(T("Hist_SavedDrive", "{0} freed from {1}"), RecommendationScanner.FormatSize(g.Sum(en => en.SizeBytes)), g.Key.TrimEnd('\\'))));
            label_Saved.Text = moved.Count == 0
                ? T("Hist_SavedNone", "Nothing is moved right now.")
                : string.Format(T("Hist_Saved", "{0} item(s) moved: {1}."), moved.Count, perDrive);
            button_UndoAll.Enabled = moved.Count > 0;
            UpdateStatus();
            UpdateBackupButton();
        }

        MoveEntryLiveState StateOf(MoveEntry entry) =>
            states.TryGetValue(entry.Id, out MoveEntryLiveState state) ? state : MoveHistory.GetLiveState(entry);

        TreeNode MakeEntryNode(MoveEntry entry, bool isUndoRecord)
        {
            string size = entry.SizeBytes > 0 ? RecommendationScanner.FormatSize(entry.SizeBytes) : "";

            if (isUndoRecord)
            {
                // A record of moving something back: shown in the direction the files went
                return new TreeNode($"{entry.TargetPath}  →  {entry.LinkPath}   {size}   [{T("Hist_StateMovedBack", "Moved back")}]")
                {
                    Tag = entry,
                    ForeColor = SystemColors.GrayText
                };
            }

            MoveEntryLiveState state = StateOf(entry);
            string stateText;
            switch (state)
            {
                case MoveEntryLiveState.Moved: stateText = T("Hist_StateMoved", "Moved"); break;
                case MoveEntryLiveState.Undone: stateText = T("Hist_StateUndone", "Undone"); break;
                default: stateText = T("Hist_StateChanged", "Changed outside FreeMove"); break;
            }

            // A merge keeps backups of the files it replaced until it is undone
            string backupNote = backupSizes.TryGetValue(entry.Id, out long backup) && backup > 0
                ? "   " + string.Format(T("Hist_BackupNote", "(merged; {0} of replaced files backed up)"), RecommendationScanner.FormatSize(backup))
                : "";

            var node = new TreeNode($"{entry.LinkPath}  →  {entry.TargetPath}   {size}   [{stateText}]{backupNote}")
            {
                Tag = entry,
                ToolTipText = state == MoveEntryLiveState.Changed
                    ? T("Hist_ChangedTip", "The link or the moved files are missing or no longer match, so this can't be undone automatically.")
                    : null
            };
            if (state != MoveEntryLiveState.Moved) node.ForeColor = SystemColors.GrayText;
            return node;
        }

        bool IsUndoable(MoveEntry entry) => StateOf(entry) == MoveEntryLiveState.Moved;

        /// <summary>The highlighted entry, if it is a merge that still has backups.</summary>
        MoveEntry SelectedMergeWithBackups()
        {
            if (tree.SelectedNode?.Tag is MoveEntry entry && backupSizes.TryGetValue(entry.Id, out long size) && size > 0)
                return entry;
            return null;
        }

        void UpdateBackupButton() => button_DeleteBackups.Enabled = SelectedMergeWithBackups() != null;

        void DeleteBackups_Click(object sender, EventArgs e)
        {
            MoveEntry entry = SelectedMergeWithBackups();
            if (entry == null) return;

            string message = string.Format(T("Hist_DeleteBackupsConfirm",
                "Delete the backups of the files this move replaced in {0} ({1})?\n\nThis frees the space, but those original files can no longer be restored. Undoing the move afterwards will leave the files that replaced them in place."),
                entry.TargetPath, RecommendationScanner.FormatSize(backupSizes[entry.Id]));
            if (Dialogs.ScrollableMessageBox.Show(this, message, T("Hist_DeleteBackups", "Delete backups"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                IO.MoveJournal.Load(entry.MergeJournalPath).RemoveBackups();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, T("ErrorTitle", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            LoadTree();
        }

        static string FormatTime(DateTime time) => time.ToString("g", CultureInfo.CurrentCulture);

        static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (TreeNode child in AllNodes(node.Nodes)) yield return child;
            }
        }

        void Tree_BeforeCheck(object sender, TreeViewCancelEventArgs e)
        {
            if (updatingChecks) return;
            // Only items that are still moved can be ticked
            if (e.Node.Tag is MoveEntry entry && !IsUndoable(entry))
                e.Cancel = true;
            else if (e.Node.Tag is MoveBatch batch && (batch.IsUndoBatch || !batch.Entries.Any(IsUndoable)))
                e.Cancel = true;
        }

        void Tree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (updatingChecks) return;
            updatingChecks = true;
            try
            {
                if (e.Node.Tag is MoveBatch)
                {
                    // Ticking a batch ticks every folder in it that can be undone
                    foreach (TreeNode child in e.Node.Nodes)
                        child.Checked = e.Node.Checked && child.Tag is MoveEntry entry && IsUndoable(entry);
                }
                else if (e.Node.Parent != null)
                {
                    // The batch is ticked only while all of its undoable folders are ticked
                    TreeNode parent = e.Node.Parent;
                    parent.Checked = parent.Nodes.Cast<TreeNode>()
                        .Where(n => n.Tag is MoveEntry entry && IsUndoable(entry))
                        .All(n => n.Checked);
                }
            }
            finally
            {
                updatingChecks = false;
            }
            UpdateStatus();
        }

        void UpdateStatus()
        {
            List<MoveEntry> selected = SelectedEntries;
            button_Undo.Enabled = selected.Count > 0;
            if (tree.Nodes.Count > 0)
            {
                label_Status.Text = selected.Count == 0
                    ? T("Hist_Hint", "Tick moves to undo. Expand a batch to pick individual folders.")
                    : string.Format(T("Hist_Status", "{0} selected, {1} to move back."),
                        selected.Count, RecommendationScanner.FormatSize(selected.Sum(s => s.SizeBytes)));
            }
        }

        void Clear_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                    T("Hist_ClearConfirm", "Remove moves that are already undone or no longer valid from the history? Moves that can still be undone are kept."),
                    T("Hist_Title", "Move history"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                MoveHistory.ClearFinished();
                LoadTree();
            }
        }

        void Undo_Click(object sender, EventArgs e) => ConfirmAndUndo(SelectedEntries);

        /// <summary>Every move that can still be undone, e.g. before uninstalling FreeMove.</summary>
        void UndoAll_Click(object sender, EventArgs e)
        {
            List<MoveEntry> all = AllNodes(tree.Nodes).Select(n => n.Tag).OfType<MoveEntry>()
                .Where(en => !IsUndoRecord(en) && IsUndoable(en))
                .GroupBy(en => en.Id).Select(g => g.First()).ToList();
            if (all.Count == 0) return;
            ConfirmAndUndo(all);
        }

        bool IsUndoRecord(MoveEntry entry) =>
            AllNodes(tree.Nodes).Any(n => n.Tag is MoveBatch b && b.IsUndoBatch && b.Entries.Contains(entry));

        /// <summary>Checks every move and offers to repair what can be repaired.</summary>
        async System.Threading.Tasks.Task HealthCheckAsync()
        {
            button_Health.Enabled = false;
            UseWaitCursor = true;
            List<HealthIssue> issues;
            try { issues = await System.Threading.Tasks.Task.Run(() => HealthCheck.Run()); }
            finally
            {
                UseWaitCursor = false;
                if (!IsDisposed) button_Health.Enabled = true;
            }
            if (IsDisposed) return;
            ShowHealthReport(this, issues);
            LoadTree();
        }

        /// <summary>
        /// Shows the result of a health check and repairs the missing links if the person agrees.
        /// Shared with the check the main window runs at startup.
        /// </summary>
        public static void ShowHealthReport(IWin32Window owner, List<HealthIssue> issues)
        {
            string title = T("Hist_HealthCheck", "Health check");
            if (issues.Count == 0)
            {
                MessageBox.Show(owner, T("Health_AllGood", "Every moved item has its link in place and its files are reachable."),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (HealthIssue issue in issues) Log.Warn("Health check: " + issue.Health + " " + issue.Entry.LinkPath + " -> " + issue.Entry.TargetPath);

            string report = HealthCheck.Report(issues);
            List<HealthIssue> repairable = issues.Where(i => i.CanRepair).ToList();
            if (repairable.Count == 0)
            {
                Dialogs.ScrollableMessageBox.Show(owner, report, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            report += "\n\n" + string.Format(T("Health_RepairQuestion", "Recreate the {0} missing link(s) now?"), repairable.Count);
            if (Dialogs.ScrollableMessageBox.Show(owner, report, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var errors = new List<string>();
            foreach (HealthIssue issue in repairable)
            {
                string error = HealthCheck.Repair(issue);
                if (error != null) errors.Add(issue.Entry.LinkPath + "\n    " + error);
            }
            if (errors.Count == 0)
                MessageBox.Show(owner, T("Health_Repaired", "The missing links were recreated."), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                Dialogs.ScrollableMessageBox.Show(owner, T("Health_RepairFailed", "Some links couldn't be recreated:") + "\n\n" + string.Join("\n\n", errors),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Finds links to other drives that History doesn't list, and adds them.</summary>
        async System.Threading.Tasks.Task FindMissingAsync()
        {
            button_FindMissing.Enabled = false;
            UseWaitCursor = true;
            string oldStatus = label_Status.Text;
            label_Status.Text = T("Hist_Searching", "Searching for links...");
            List<MoveEntry> found;
            try
            {
                found = await System.Threading.Tasks.Task.Run(() => MoveHistory.FindUnrecordedLinks(System.Threading.CancellationToken.None));
            }
            catch (Exception ex)
            {
                found = new List<MoveEntry>();
                Log.Error("Find missing moves failed", ex);
            }
            finally
            {
                UseWaitCursor = false;
                if (!IsDisposed)
                {
                    button_FindMissing.Enabled = true;
                    label_Status.Text = oldStatus;
                }
            }
            if (IsDisposed) return;

            string title = T("Hist_FindMissing", "Find missing moves");
            if (found.Count == 0)
            {
                MessageBox.Show(this, T("Hist_FindNone", "No links to other drives were found that aren't already in History."),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string list = string.Join("\n", found.Select(f => $"{f.LinkPath}  →  {f.TargetPath}"));
            string question = string.Format(T("Hist_FindQuestion", "Found {0} link(s) to other drives that aren't in History:\n\n{1}\n\nAdd them, so they can be checked and moved back like any other move?"), found.Count, list);
            if (Dialogs.ScrollableMessageBox.Show(this, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            MoveHistory.AddRecoveredEntries(found);
            Log.Info($"Added {found.Count} link(s) found on disk to History");
            LoadTree();
        }

        void ConfirmAndUndo(List<MoveEntry> selected)
        {
            if (selected.Count == 0) return;
            if (IsBusy?.Invoke() == true)
            {
                MessageBox.Show(this, T("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string list = string.Join("\n", selected.Select(s => $"{s.TargetPath}  →  {s.LinkPath}"));

            string message = string.Format(
                T("Hist_UndoConfirm", "Move {0} item(s) back to their original location?\n\n{1}\n\nThis needs {2} of free space on the original drive. Close the programs that use these folders first."),
                selected.Count, list, RecommendationScanner.FormatSize(selected.Sum(s => s.SizeBytes)));

            if (Dialogs.ScrollableMessageBox.Show(this, message, T("Hist_UndoTitle", "Undo moves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                Close();
                UndoAccepted?.Invoke(this, selected);
            }
        }
    }
}
