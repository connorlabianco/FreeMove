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
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FreeMove
{
    // Busy state while a move or undo runs, and the separate History / Recommended moves windows.
    public partial class Form1
    {
        int busyDepth;
        bool closeWhenIdle;
        readonly List<Control> disabledWhileBusy = new List<Control>();

        /// <summary>The batch being processed, so closing the window can skip what hasn't started.</summary>
        BatchQueue currentQueue;

        /// <summary>True while a move, undo or restore is running.</summary>
        internal bool IsBusy => busyDepth > 0;

        /// <summary>
        /// Marks the window busy (or not). The window itself stays enabled so it keeps responding and
        /// the title bar buttons work; only its contents are disabled. Calls can nest.
        /// </summary>
        void SetBusy(bool busy)
        {
            if (busy)
            {
                if (busyDepth++ > 0) return;
                foreach (Control c in Controls)
                {
                    if (c.Enabled)
                    {
                        c.Enabled = false;
                        disabledWhileBusy.Add(c);
                    }
                }
                UseWaitCursor = true;
            }
            else
            {
                if (busyDepth == 0 || --busyDepth > 0) return;
                foreach (Control c in disabledWhileBusy) c.Enabled = true;
                disabledWhileBusy.Clear();
                UseWaitCursor = false;
                if (closeWhenIdle && !IsDisposed) BeginInvoke(new Action(Close));
            }
        }

        /// <summary>
        /// Closing while files are being moved: stopping in the middle of a step isn't safe, so the
        /// items not started yet are skipped and the window closes by itself once the current step
        /// is finished (or rolled back).
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (IsBusy && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (!closeWhenIdle)
                {
                    closeWhenIdle = true;
                    if (currentQueue != null) currentQueue.SkipRemaining = true;
                    Text = Text + " - " + R("Busy_ClosingWhenDone", "will close when the current item is finished...");
                }
            }
            base.OnFormClosing(e);
        }

        /// <summary>Shows an independent window (its own taskbar button) centered over this one.</summary>
        void ShowSideWindow(Form form)
        {
            form.Icon = Icon;
            form.ShowIcon = true;
            form.ShowInTaskbar = true;
            form.StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Left + (Width - form.Width) / 2;
            int y = Top + (Height - form.Height) / 2;
            x = Math.Max(area.Left, Math.Min(x, area.Right - form.Width));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - form.Height));
            form.Location = new Point(x, y);
            form.Show();
        }

        /// <summary>Brings an already open window to the front.</summary>
        static void ActivateWindow(Form form)
        {
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        /// <summary>Gives a dialog FreeMove's icon and its own taskbar button, so it can't get lost behind other windows.</summary>
        void PrepareDialog(Form form)
        {
            form.Icon = Icon;
            form.ShowIcon = true;
            form.ShowInTaskbar = true;
        }

        /// <summary>
        /// Runs the pre-move checks in the background with the progress window up ("Checking
        /// files..."), so the window keeps responding and, in a batch, the queue line with Skip /
        /// Skip remaining is shown. Throws <see cref="OperationCanceledException"/> if the person
        /// cancels or skips; the checks only read files, so stopping waiting for them is safe.
        /// </summary>
        Task RunCheck(Action check, BatchQueue queue = null) =>
            RunOperation(new CheckOperation(check), queue,
                R("Check_Checking", "Checking the files before moving..."));

        /// <summary>A background check that can be abandoned from the progress window.</summary>
        class CheckOperation : IO.IOOperation
        {
            readonly Action check;
            readonly TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();

            public CheckOperation(Action check) { this.check = check; }

            public override void Cancel() => cancelled.TrySetResult(true);

            public override async Task Run()
            {
                OnStart(EventArgs.Empty);
                try
                {
                    Task work = Task.Run(check);
                    if (await Task.WhenAny(work, cancelled.Task) != work)
                    {
                        // Let the abandoned check finish quietly in the background
                        _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                        throw new OperationCanceledException();
                    }
                    await work;   // rethrows the check's own error
                }
                finally
                {
                    OnEnd(EventArgs.Empty);
                }
            }
        }

        void ShowBusyMessage(IWin32Window owner)
        {
            MessageBox.Show(owner,
                R("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
