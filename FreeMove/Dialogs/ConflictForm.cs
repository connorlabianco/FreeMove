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
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FreeMove.Dialogs
{
    public class ConflictItem
    {
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
    }

    /// <summary>
    /// Shown before a batch when some destinations already exist. Nothing is overwritten unless
    /// the person ticks it: unticked items are skipped. Ticked items are merged into the existing
    /// folder, replacing files with the same name and keeping the other files already there.
    /// </summary>
    public class ConflictForm : Form
    {
        readonly ListView listView;

        /// <summary>Indices (into the list passed in) of the items to overwrite.</summary>
        public List<int> OverwriteIndices =>
            listView.CheckedItems.Cast<ListViewItem>().Select(i => (int)i.Tag).ToList();

        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        public ConflictForm(List<ConflictItem> items)
        {
            Text = T("Conflict_Title", "Already exists at destination");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 320);
            Size = new Size(800, 440);
            ShowIcon = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 4),
                MaximumSize = new Size(780, 0),
                Text = T("Conflict_Intro", "These already exist at the destination. Unticked items are skipped and nothing is overwritten. Tick an item to overwrite it: files with the same name are replaced by the ones being moved, and other files already there are kept.")
            };

            listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true
            };
            listView.Columns.Add(T("Conflict_ColSource", "Moving"), 340);
            listView.Columns.Add(T("Conflict_ColDest", "Already at destination"), 340);
            listView.Columns.Add(T("Conflict_ColExisting", "Existing items"), 100, HorizontalAlignment.Right);

            for (int i = 0; i < items.Count; i++)
            {
                var row = new ListViewItem(items[i].SourcePath) { Tag = i, Checked = false };
                row.SubItems.Add(items[i].DestinationPath);
                row.SubItems.Add(CountEntries(items[i].DestinationPath));
                listView.Items.Add(row);
            }

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 4, 8, 8)
            };
            var button_Cancel = new Button { Text = T("Conflict_CancelBatch", "Cancel batch"), AutoSize = true, DialogResult = DialogResult.Cancel };
            var button_Continue = new Button { Text = T("Conflict_Continue", "Continue"), AutoSize = true, DialogResult = DialogResult.OK };
            var button_OverwriteAll = new Button { Text = T("Conflict_OverwriteAll", "Overwrite all"), AutoSize = true };
            var button_SkipAll = new Button { Text = T("Conflict_SkipAll", "Skip all"), AutoSize = true };
            button_OverwriteAll.Click += (s, e) => SetAll(true);
            button_SkipAll.Click += (s, e) => SetAll(false);

            bottom.Controls.Add(button_Cancel);
            bottom.Controls.Add(button_Continue);
            bottom.Controls.Add(button_OverwriteAll);
            bottom.Controls.Add(button_SkipAll);

            Controls.Add(listView);
            Controls.Add(bottom);
            Controls.Add(intro);
            AcceptButton = button_Continue;
            CancelButton = button_Cancel;
        }

        void SetAll(bool value)
        {
            listView.BeginUpdate();
            foreach (ListViewItem item in listView.Items) item.Checked = value;
            listView.EndUpdate();
        }

        static string CountEntries(string path)
        {
            try
            {
                if (File.Exists(path)) return "1";
                return Directory.EnumerateFileSystemEntries(path).Take(10000).Count().ToString();
            }
            catch
            {
                return "?";
            }
        }
    }
}
