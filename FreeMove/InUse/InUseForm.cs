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
using FreeMove.IO;

namespace FreeMove.InUse
{
    /// <summary>An item set aside during a batch because programs were using it.</summary>
    public class InUseItem
    {
        /// <summary>The folder shown to the person.</summary>
        public string DisplayPath { get; set; }
        /// <summary>The folder checked for programs using it.</summary>
        public string CheckPath { get; set; }
        public List<LockingProcess> Processes { get; set; } = new List<LockingProcess>();
        /// <summary>The caller's own object for this item (what to retry).</summary>
        public object Payload { get; set; }
        /// <summary>The error from the last attempt.</summary>
        public string LastError { get; set; }
    }

    public enum InUseChoice
    {
        EndTasksAndRetry,
        Retry,
        Skip
    }

    /// <summary>
    /// Shown at the end of a batch for the items that were set aside because they were in use.
    /// Lists each item with the programs using it, and offers End tasks and retry, Retry (after
    /// closing them yourself), or Skip.
    /// </summary>
    public class InUseForm : Form
    {
        public InUseChoice Choice { get; private set; } = InUseChoice.Skip;

        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        public InUseForm(List<InUseItem> items)
        {
            Text = T("InUse_Title", "Folders in use");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 340);
            Size = new Size(700, 440);
            ShowIcon = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 4),
                MaximumSize = new Size(680, 0),
                Text = T("InUse_Intro", "These were set aside because programs are using them. End those programs and retry, close them yourself and retry, or skip these folders. Nothing has been changed for them.")
            };

            var tree = new TreeView { Dock = DockStyle.Fill, ShowNodeToolTips = true };
            tree.BeginUpdate();
            foreach (InUseItem item in items)
            {
                var node = new TreeNode(item.DisplayPath) { ToolTipText = item.LastError };
                if (item.Processes.Count == 0)
                {
                    node.Nodes.Add(new TreeNode(T("InUse_Unknown", "In use, but the program couldn't be identified")) { ForeColor = SystemColors.GrayText });
                }
                foreach (LockingProcess p in item.Processes)
                {
                    string text = p.ToString();
                    if (!p.CanEnd) text += "  " + T("InUse_CannotEnd", "(system process or service; close it yourself)");
                    node.Nodes.Add(new TreeNode(text)
                    {
                        ToolTipText = p.ImagePath,
                        ForeColor = p.CanEnd ? tree.ForeColor : SystemColors.GrayText
                    });
                }
                tree.Nodes.Add(node);
            }
            tree.ExpandAll();
            tree.EndUpdate();

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 4, 8, 8)
            };

            bool anyEndable = items.Any(i => i.Processes.Any(p => p.CanEnd));
            var button_Skip = new Button { Text = T("InUse_Skip", "Skip these"), AutoSize = true, DialogResult = DialogResult.Cancel };
            var button_Retry = new Button { Text = T("InUse_Retry", "Retry"), AutoSize = true };
            var button_End = new Button { Text = T("InUse_EndAndRetry", "End tasks and retry"), AutoSize = true, Enabled = anyEndable };

            button_Retry.Click += (s, e) => { Choice = InUseChoice.Retry; DialogResult = DialogResult.OK; };
            button_End.Click += (s, e) =>
            {
                string names = string.Join("\n", items.SelectMany(i => i.Processes).Where(p => p.CanEnd)
                    .GroupBy(p => p.Id).Select(g => g.First().ToString()));
                string message = string.Format(T("InUse_EndConfirm", "End these programs?\n\n{0}\n\nUnsaved work in them will be lost."), names);
                if (MessageBox.Show(this, message, T("InUse_EndAndRetry", "End tasks and retry"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    Choice = InUseChoice.EndTasksAndRetry;
                    DialogResult = DialogResult.OK;
                }
            };

            bottom.Controls.Add(button_Skip);
            bottom.Controls.Add(button_Retry);
            bottom.Controls.Add(button_End);

            Controls.Add(tree);
            Controls.Add(bottom);
            Controls.Add(intro);
            CancelButton = button_Skip;
        }
    }
}
