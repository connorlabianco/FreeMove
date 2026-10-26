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
using System.Linq;
using System.Windows.Forms;

namespace FreeMove.MoveQueue
{
    /// <summary>
    /// The list of manual moves waiting to run. Items are added from the main window ("Add to
    /// queue", or by dropping several folders on it). "Move all" runs them one after another with
    /// the usual "Item i of j" progress, Skip and Skip remaining.
    /// </summary>
    public class QueueForm : Form
    {
        readonly List<MoveJob> items;
        readonly ListView listView;
        readonly Label label_Status;
        readonly Button button_Remove;
        readonly Button button_Clear;
        readonly Button button_Preview;
        readonly Button button_MoveAll;
        readonly Button button_Close;

        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        /// <summary>Asked before moving: true while the main window is already moving files.</summary>
        public Func<bool> IsBusy { get; set; }

        /// <summary>The person wants a preview of every queued item.</summary>
        public event EventHandler PreviewRequested;

        /// <summary>The person wants to move everything in the queue now.</summary>
        public event EventHandler MoveAllRequested;

        /// <param name="items">The queue itself, shared with the main window (not a copy).</param>
        public QueueForm(List<MoveJob> items)
        {
            this.items = items;
            Text = T("Queue_Title", "Move queue");
            MinimumSize = new Size(620, 320);
            Size = new Size(820, 420);
            Font = SystemFonts.MessageBoxFont;

            listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
            listView.Columns.Add(T("Queue_ColFrom", "Move"), 360);
            listView.Columns.Add(T("Queue_ColTo", "To"), 360);
            listView.SelectedIndexChanged += (s, e) => UpdateButtons();
            listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 6,
                Padding = new Padding(8, 4, 8, 8)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            label_Status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(300, 0) };
            button_Remove = new Button { Text = T("Queue_Remove", "Remove"), AutoSize = true };
            button_Remove.Click += (s, e) => RemoveSelected();
            button_Clear = new Button { Text = T("Queue_Clear", "Clear"), AutoSize = true };
            button_Clear.Click += (s, e) => { items.Clear(); RefreshList(); };
            button_Preview = new Button { Text = T("Preview_Button", "Preview..."), AutoSize = true };
            button_Preview.Click += (s, e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
            button_MoveAll = new Button { Text = T("Queue_MoveAll", "Move all"), AutoSize = true };
            button_MoveAll.Click += MoveAll_Click;
            button_Close = new Button { Text = T("Button_Close", "Close"), AutoSize = true };
            button_Close.Click += (s, e) => Close();

            bottom.Controls.Add(label_Status, 0, 0);
            bottom.Controls.Add(button_Remove, 1, 0);
            bottom.Controls.Add(button_Clear, 2, 0);
            bottom.Controls.Add(button_Preview, 3, 0);
            bottom.Controls.Add(button_MoveAll, 4, 0);
            bottom.Controls.Add(button_Close, 5, 0);

            Controls.Add(listView);
            Controls.Add(bottom);
            CancelButton = button_Close;

            RefreshList();
        }

        /// <summary>Shows the queue as it is now (call after the main window changes it).</summary>
        public void RefreshList()
        {
            if (IsDisposed) return;
            listView.BeginUpdate();
            listView.Items.Clear();
            foreach (MoveJob job in items)
            {
                var item = new ListViewItem(job.Source) { Tag = job };
                item.SubItems.Add(job.Destination);
                listView.Items.Add(item);
            }
            listView.EndUpdate();
            label_Status.Text = items.Count == 0
                ? T("Queue_Empty", "The queue is empty. Pick a folder in the main window and click \"Add to queue\", or drop several folders on it.")
                : string.Format(T("Queue_Count", "{0} item(s) waiting."), items.Count);
            UpdateButtons();
        }

        void UpdateButtons()
        {
            button_Remove.Enabled = listView.SelectedItems.Count > 0;
            button_Clear.Enabled = items.Count > 0;
            button_Preview.Enabled = items.Count > 0;
            button_MoveAll.Enabled = items.Count > 0;
        }

        void RemoveSelected()
        {
            foreach (ListViewItem item in listView.SelectedItems)
                items.Remove((MoveJob)item.Tag);
            RefreshList();
        }

        void MoveAll_Click(object sender, EventArgs e)
        {
            if (items.Count == 0) return;
            if (IsBusy?.Invoke() == true)
            {
                MessageBox.Show(this, T("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string list = string.Join("\n", items.Select(j => $"{j.Source}  →  {j.Destination}"));
            string message = string.Format(T("Queue_Confirm", "Move these {0} item(s)?\n\n{1}\n\nClose the programs that use them first. Each one is moved and a link is left in its place."), items.Count, list);
            if (Dialogs.ScrollableMessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                MoveAllRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
