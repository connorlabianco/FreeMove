// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace FreeMove
{
    /// <summary>
    /// Shared state for a queue of moves (a Recommended moves batch or a History undo).
    /// One instance lives for the whole queue, while each item gets its own progress dialog.
    /// </summary>
    public class BatchQueue
    {
        /// <summary>1-based position of the item being processed.</summary>
        public int Position { get; set; }
        public int Count { get; set; }
        public string CurrentItem { get; set; }
        /// <summary>Set when the person skipped the current item; reset before each item.</summary>
        public bool SkipCurrent { get; set; }
        /// <summary>Set when the person chose to skip the items not started yet.</summary>
        public bool SkipRemaining { get; set; }
    }

    // Queue mode: shows "Item i of j" plus Skip and Skip remaining buttons.
    public partial class ProgressDialog
    {
        private Label label_Queue;
        private Button button_Skip;
        private Button button_SkipRemaining;

        public event EventHandler SkipRequested;
        public event EventHandler SkipRemainingRequested;

        static string Q(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        /// <summary>
        /// Adds the queue line and the Skip / Skip remaining buttons. Call before ShowDialog.
        /// </summary>
        public void EnableQueueMode(BatchQueue queue)
        {
            const int width = 520;
            const int queueLineHeight = 22;
            int extraWidth = width - ClientSize.Width;

            // Make room for the queue line at the top and widen everything
            foreach (Control c in Controls) c.Top += queueLineHeight;
            progressBar1.Width += extraWidth;
            label_Progress.Left += extraWidth;
            button_Cancel.Left += extraWidth;

            label_Queue = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(12, 9),
                Size = new Size(width - 24, 15),
                Text = string.Format(Q("Queue_ItemFormat", "Item {0} of {1}: {2}"), queue.Position, queue.Count, queue.CurrentItem)
            };

            button_SkipRemaining = new Button
            {
                Text = Q("Queue_SkipRemaining", "Skip remaining"),
                AutoSize = true,
                UseVisualStyleBackColor = true,
                Enabled = !queue.SkipRemaining && queue.Position < queue.Count
            };
            button_Skip = new Button
            {
                Text = Q("Queue_Skip", "Skip"),
                AutoSize = true,
                UseVisualStyleBackColor = true,
                Enabled = Cancellable
            };

            // Right-align: [Skip] [Skip remaining] [Cancel]
            button_SkipRemaining.Size = button_SkipRemaining.PreferredSize;
            button_Skip.Size = button_Skip.PreferredSize;
            int top = button_Cancel.Top;
            button_SkipRemaining.Location = new Point(button_Cancel.Left - 6 - button_SkipRemaining.Width, top);
            button_Skip.Location = new Point(button_SkipRemaining.Left - 6 - button_Skip.Width, top);

            button_Skip.Click += (s, e) => SkipRequested?.Invoke(this, EventArgs.Empty);
            button_SkipRemaining.Click += (s, e) =>
            {
                button_SkipRemaining.Enabled = false;
                label_Queue.Text += "  " + Q("Queue_RemainingWillSkip", "(remaining items will be skipped)");
                SkipRemainingRequested?.Invoke(this, EventArgs.Empty);
            };

            if (queue.SkipRemaining)
                label_Queue.Text += "  " + Q("Queue_RemainingWillSkip", "(remaining items will be skipped)");

            Controls.Add(label_Queue);
            Controls.Add(button_Skip);
            Controls.Add(button_SkipRemaining);
            ClientSize = new Size(width, ClientSize.Height + queueLineHeight);
        }
    }
}
