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
            button_Close = new Button { Text = T("Button_Close", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
            button_DeleteBackups = new Button { Text = T("Hist_DeleteBackups", "Delete backups"), AutoSize = true, Enabled = false };
            button_DeleteBackups.Click += DeleteBackups_Click;

            bottom.Controls.Add(label_Status, 0, 0);
            bottom.Controls.Add(button_DeleteBackups, 1, 0);
            bottom.Controls.Add(button_Clear, 2, 0);
            bottom.Controls.Add(button_Undo, 3, 0);
            bottom.Controls.Add(button_Close, 4, 0);

            Controls.Add(tree);
            Controls.Add(bottom);
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

        void Undo_Click(object sender, EventArgs e)
        {
            List<MoveEntry> selected = SelectedEntries;
            if (selected.Count == 0) return;

            string list = string.Join("\n", selected.Select(s => $"{s.TargetPath}  →  {s.LinkPath}"));

            string message = string.Format(
                T("Hist_UndoConfirm", "Move {0} item(s) back to their original location?\n\n{1}\n\nThis needs {2} of free space on the original drive. Close the programs that use these folders first."),
                selected.Count, list, RecommendationScanner.FormatSize(selected.Sum(s => s.SizeBytes)));

            if (Dialogs.ScrollableMessageBox.Show(this, message, T("Hist_UndoTitle", "Undo moves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
