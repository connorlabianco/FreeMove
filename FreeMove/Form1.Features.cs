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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FreeMove.Dialogs;
using FreeMove.History;
using FreeMove.IO;
using FreeMove.MoveQueue;
using FreeMove.Recommendations;

namespace FreeMove
{
    // Main-window features: move queue, preview, safety warnings, drag and drop, startup health
    // check, the log menu item.
    public partial class Form1
    {
        Button button_AddToQueue;
        Button button_Preview;
        ToolStripMenuItem queueToolStripMenuItem;
        ToolStripMenuItem openLogToolStripMenuItem;
        ToolStripMenuItem healthOnStartToolStripMenuItem;

        /// <summary>Manual moves waiting to run, shared with the queue window.</summary>
        readonly List<MoveJob> manualQueue = new List<MoveJob>();
        QueueForm queueWindow;
        ToolTip featureTips;

        /// <summary>Adds the extra buttons and menu items. Called from the constructor.</summary>
        void SetUpFeatures()
        {
            // [Preview...] [Add to queue] [Move] on the button row, right-aligned like Move
            button_AddToQueue = new Button { AutoSize = true, UseVisualStyleBackColor = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            button_Preview = new Button { AutoSize = true, UseVisualStyleBackColor = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            button_AddToQueue.Click += (s, e) => AddCurrentToQueue();
            button_Preview.Click += async (s, e) => await PreviewCurrent();
            splitContainer_Form1.Panel1.Controls.Add(button_AddToQueue);
            splitContainer_Form1.Panel1.Controls.Add(button_Preview);

            queueToolStripMenuItem = new ToolStripMenuItem();
            queueToolStripMenuItem.Click += (s, e) => ShowQueue();
            menuStrip1.Items.Insert(2, queueToolStripMenuItem);

            openLogToolStripMenuItem = new ToolStripMenuItem();
            openLogToolStripMenuItem.Click += (s, e) => OpenLogFolder();
            infoToolStripMenuItem.DropDownItems.Add(openLogToolStripMenuItem);

            healthOnStartToolStripMenuItem = new ToolStripMenuItem { CheckOnClick = true, Checked = Settings.CheckHealthOnStart };
            healthOnStartToolStripMenuItem.CheckedChanged += (s, e) =>
            {
                try { Settings.CheckHealthOnStart = healthOnStartToolStripMenuItem.Checked; } catch { }
            };
            settingsToolStripMenuItem.DropDownItems.Add(healthOnStartToolStripMenuItem);

            UpdateFeatureTexts();
        }

        /// <summary>Texts of the added controls in the current language (called from ApplyLanguage too).</summary>
        void UpdateFeatureTexts()
        {
            if (button_AddToQueue == null) return;
            button_AddToQueue.Text = R("Queue_Add", "Add to queue");
            button_Preview.Text = R("Preview_Button", "Preview...");
            queueToolStripMenuItem.Text = manualQueue.Count == 0
                ? R("Menu_Queue", "Queue")
                : string.Format(R("Menu_QueueCount", "Queue ({0})"), manualQueue.Count);
            openLogToolStripMenuItem.Text = R("Menu_OpenLog", "Open log folder");
            healthOnStartToolStripMenuItem.Text = R("Menu_HealthOnStart", "Check moved folders at startup");

            // Lay out right to left, left of Move, keeping each button at least as wide as Move
            foreach (Button b in new[] { button_AddToQueue, button_Preview })
                b.Size = new Size(Math.Max(button_Move.Width, b.PreferredSize.Width), button_Move.Height);
            button_AddToQueue.Location = new Point(button_Move.Left - 6 - button_AddToQueue.Width, button_Move.Top);
            button_Preview.Location = new Point(button_AddToQueue.Left - 6 - button_Preview.Width, button_Move.Top);

            if (featureTips == null) featureTips = new ToolTip();
            ToolTip tips = featureTips;
            tips.SetToolTip(button_AddToQueue, R("Queue_AddTip", "Add this move to the queue, to move several folders one after another."));
            tips.SetToolTip(button_Preview, R("Preview_Tip", "See the size, free space afterwards, programs using it and any warnings, without moving anything."));
        }

        /// <summary>Brings the main window to the front before showing a result, so it can't hide behind another window.</summary>
        void BringMainWindowForward()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            }
            catch { }
        }

        /// <summary>The move described by the From and To boxes, or null (after saying why).</summary>
        MoveJob JobFromTextBoxes()
        {
            string source = textBox_From.Text.Trim().TrimEnd('\\');
            string to = textBox_To.Text.Trim();
            if (source.Length == 0 || to.Length == 0)
            {
                MessageBox.Show(this, R("Queue_NeedPaths", "Choose what to move (From) and where to (To) first."), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            string destination = Path.Combine(to.Length > 3 ? to.TrimEnd('\\') : to, Path.GetFileName(source));
            return new MoveJob { Source = source, Destination = destination };
        }

        #region Queue

        void AddCurrentToQueue()
        {
            MoveJob job = JobFromTextBoxes();
            if (job == null) return;
            AddToQueue(new[] { job });
            textBox_From.Text = "";
        }

        void AddToQueue(IEnumerable<MoveJob> jobs)
        {
            foreach (MoveJob job in jobs)
            {
                if (!PathExists(job.Source))
                {
                    MessageBox.Show(this, string.Format(R("Queue_SourceMissing", "{0} doesn't exist."), job.Source), Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                bool duplicate = manualQueue.Any(q => string.Equals(q.Source, job.Source, StringComparison.OrdinalIgnoreCase));
                if (!duplicate) manualQueue.Add(job);
            }
            UpdateFeatureTexts();
            ShowQueue();
            queueWindow?.RefreshList();
        }

        void ShowQueue()
        {
            if (queueWindow != null && !queueWindow.IsDisposed)
            {
                queueWindow.RefreshList();
                ActivateWindow(queueWindow);
                return;
            }
            var form = new QueueForm(manualQueue) { IsBusy = () => IsBusy };
            form.PreviewRequested += async (s, e) => await Preview(manualQueue.ToList(), (Form)s);
            form.MoveAllRequested += async (s, e) => await RunManualQueue();
            form.FormClosed += (s, e) => { if (ReferenceEquals(queueWindow, s)) queueWindow = null; };
            queueWindow = form;
            ShowSideWindow(form);
        }

        /// <summary>Moves everything in the queue, one item after another.</summary>
        async Task RunManualQueue()
        {
            if (IsBusy) { ShowBusyMessage(this); return; }
            if (manualQueue.Count == 0) return;

            // Take the items out of the queue first; anything not moved is listed in the summary
            List<MoveJob> jobs = manualQueue.Select(j => new MoveJob { Source = j.Source.TrimEnd('\\'), Destination = j.Destination.TrimEnd('\\') }).ToList();
            manualQueue.Clear();
            UpdateFeatureTexts();
            queueWindow?.RefreshList();
            queueWindow?.Close();

            SetBusy(true);
            try
            {
                await MoveJobsWithChecks(jobs, recommendedBatch: false, R("Queue_SummaryTitle", "Move queue"));
            }
            finally
            {
                SetBusy(false);
            }
        }

        #endregion

        #region Safety warnings

        /// <summary>
        /// Looks for risky moves (cloud folders, Store apps, anti-cheat, services, removable,
        /// network or non-NTFS destinations) and asks about them. Returns the jobs to go ahead
        /// with, or null if the person cancelled. Items the person chose to leave are added to
        /// <paramref name="skipped"/>.
        /// </summary>
        async Task<List<MoveJob>> ConfirmSafety(List<MoveJob> jobs, List<string> skipped)
        {
            var flagged = new List<KeyValuePair<MoveJob, List<SafetyNote>>>();
            try
            {
                await RunCheck(status =>
                {
                    foreach (MoveJob job in jobs)
                    {
                        status(string.Format(R("Safety_Checking", "Looking for possible problems: {0}"), job.Source));
                        List<SafetyNote> notes = SafetyCheck.Assess(job.Source, job.Destination);
                        if (notes.Any(n => n.Serious))
                            lock (flagged) flagged.Add(new KeyValuePair<MoveJob, List<SafetyNote>>(job, notes));
                    }
                }, null, R("Safety_CheckingTitle", "Looking for possible problems..."));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // The warnings are advice: if they can't be worked out, the hard checks still run
                Log.Error("Safety check failed", ex);
                return jobs;
            }

            if (flagged.Count == 0) return jobs;

            var sb = new StringBuilder();
            foreach (var pair in flagged)
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(pair.Key.Source);
                foreach (SafetyNote note in pair.Value)
                    sb.AppendLine().Append("    ").Append(note);
            }
            foreach (var pair in flagged)
                Log.Warn("Safety warning for " + pair.Key.Source + ": " + string.Join(" | ", pair.Value.Select(n => n.Message)));

            string title = R("Safety_Title", "Check before moving");
            BringMainWindowForward();
            if (jobs.Count == 1)
            {
                string question = R("Safety_IntroOne", "This move may cause problems:") + "\n\n" + sb +
                                  "\n\n" + R("Safety_AskOne", "Move it anyway?");
                if (ScrollableMessageBox.Show(this, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    Log.Info("Move not started after safety warning: " + jobs[0].Source);
                    return null;
                }
                Log.Info("Moving despite safety warning: " + jobs[0].Source);
                return jobs;
            }

            string batchQuestion = string.Format(R("Safety_IntroMany", "{0} of these {1} items may cause problems:"), flagged.Count, jobs.Count) + "\n\n" + sb +
                                   "\n\n" + R("Safety_AskMany", "Yes: move everything anyway\nNo: skip these and move the rest\nCancel: don't move anything");
            DialogResult answer = ScrollableMessageBox.Show(this, batchQuestion, title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return null;
            if (answer == DialogResult.Yes) return jobs;

            var flaggedJobs = new HashSet<MoveJob>(flagged.Select(p => p.Key));
            skipped.AddRange(flaggedJobs.Select(j => j.Source + "  " + R("Safety_SkippedNote", "(skipped because of a warning)")));
            return jobs.Where(j => !flaggedJobs.Contains(j)).ToList();
        }

        #endregion

        #region Preview

        async Task PreviewCurrent()
        {
            MoveJob job = JobFromTextBoxes();
            if (job == null) return;
            await Preview(new List<MoveJob> { job }, this);
        }

        /// <summary>
        /// Shows what would happen, without moving anything: sizes, where each item goes, programs
        /// using it, warnings, and the free space on each drive before and after.
        /// </summary>
        async Task Preview(List<MoveJob> jobs, IWin32Window owner)
        {
            if (jobs.Count == 0) return;
            if (IsBusy) { ShowBusyMessage(owner); return; }

            string report = null;
            SetBusy(true);
            try
            {
                await RunCheck(status => report = BuildPreview(jobs, status), null, R("Preview_Working", "Preparing the preview..."));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                report = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }

            ScrollableMessageBox.Show(owner, report, R("Preview_Title", "Preview (nothing has been moved)"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static string BuildPreview(List<MoveJob> jobs, Action<string> status)
        {
            var sb = new StringBuilder();
            var change = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // drive -> bytes freed (+) or used (-)
            Dictionary<int, string> running = ProcessFinder.GetProcessImagePaths();

            foreach (MoveJob job in jobs)
            {
                status(string.Format(R("Preview_Measuring", "Measuring {0}"), job.Source));
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(job.Source).Append("  →  ").Append(job.Destination);

                if (!PathExists(job.Source))
                {
                    sb.AppendLine().Append("    ").Append(R("Preview_Missing", "Doesn't exist."));
                    continue;
                }

                long size = 0;
                try
                {
                    size = File.Exists(job.Source) ? new FileInfo(job.Source).Length
                                                   : RecommendationScanner.GetDirectorySize(job.Source, CancellationToken.None);
                }
                catch { }
                sb.AppendLine().Append("    ").AppendFormat(R("Preview_Size", "Size: {0}"), RecommendationScanner.FormatSize(size));


                if (NativeFs.IsLink(job.Source))
                    sb.AppendLine().Append("    ").Append(R("Preview_AlreadyLink", "This is already a link, so there is nothing to move."));
                if (PathExists(job.Destination))
                    sb.AppendLine().Append("    ").Append(R("Preview_Exists", "Already exists at the destination: you'll be asked whether to merge into it."));

                List<string> users = ProcessFinder.RunningFrom(job.Source, running);
                if (users.Count > 0)
                    sb.AppendLine().Append("    ").AppendFormat(R("Preview_Running", "Running now: {0}. Close it before moving."), string.Join(", ", users.Distinct()));

                foreach (SafetyNote note in SafetyCheck.Assess(job.Source, job.Destination))
                    sb.AppendLine().Append("    ").Append(note);

                string from = Path.GetPathRoot(job.Source) ?? "";
                string to = Path.GetPathRoot(job.Destination) ?? "";
                if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                {
                    change[from] = (change.TryGetValue(from, out long f) ? f : 0) + size;
                    change[to] = (change.TryGetValue(to, out long t) ? t : 0) - size;
                }
            }

            if (change.Count > 0)
            {
                sb.AppendLine().AppendLine().Append(R("Preview_Space", "Free space:"));
                foreach (var drive in change.OrderBy(d => d.Key))
                {
                    string line;
                    try
                    {
                        var info = new DriveInfo(drive.Key);
                        long free = info.AvailableFreeSpace;
                        long after = free + drive.Value;
                        line = string.Format(R("Preview_SpaceLine", "{0}  {1} now, about {2} after"), drive.Key.TrimEnd('\\'),
                            RecommendationScanner.FormatSize(free), after < 0 ? R("Preview_NotEnough", "not enough space!") : RecommendationScanner.FormatSize(after));
                    }
                    catch
                    {
                        line = string.Format(R("Preview_DriveNotReady", "{0}  not available"), drive.Key.TrimEnd('\\'));
                    }
                    sb.AppendLine().Append("    ").Append(line);
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Startup health check

        /// <summary>
        /// After start-up (and recovery), quietly checks the moved folders and speaks up only if
        /// some aren't working the way they were left: a missing drive, or a link replaced by a
        /// real folder after a reinstall.
        /// </summary>
        async Task StartupHealthCheck()
        {
            if (!Settings.CheckHealthOnStart) return;
            List<HealthIssue> issues;
            try
            {
                issues = await Task.Run(() => HealthCheck.Run());
            }
            catch (Exception ex)
            {
                Log.Error("Startup health check failed", ex);
                return;
            }

            // Entries whose files are simply gone are cleanup, not an emergency: don't nag about them
            issues = issues.Where(i => i.Health == MoveHealth.TargetMissing || i.Health == MoveHealth.Replaced || i.Health == MoveHealth.LinkMissing).ToList();
            if (issues.Count == 0 || IsDisposed) return;

            string question = string.Format(R("Health_StartupQuestion",
                "{0} moved folder(s) aren't working the way FreeMove left them (for example, the drive they were moved to isn't connected, or a reinstall replaced a link with a new folder).\n\nShow the details?"), issues.Count);
            if (MessageBox.Show(this, question, R("Hist_HealthCheck", "Health check"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                HistoryForm.ShowHealthReport(this, issues);
        }

        #endregion

        #region Log

        void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(Log.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, R("ErrorTitle", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Drag and drop

        // FreeMove runs as administrator, and Windows blocks ordinary (OLE) drag and drop from
        // Explorer into elevated windows. The older WM_DROPFILES message still works once it is
        // allowed through the message filter, so that is what is used here.
        const int WM_DROPFILES = 0x0233;
        const int WM_COPYDATA = 0x004A;
        const int WM_COPYGLOBALDATA = 0x0049;
        const uint MSGFLT_ALLOW = 1;

        [DllImport("shell32.dll")]
        static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        [DllImport("shell32.dll")]
        static extern bool DragQueryPoint(IntPtr hDrop, out NativePoint lppt);

        [DllImport("shell32.dll")]
        static extern void DragFinish(IntPtr hDrop);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X; public int Y; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                ChangeWindowMessageFilterEx(Handle, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(Handle, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
                DragAcceptFiles(Handle, true);
            }
            catch (Exception ex)
            {
                Log.Warn("Drag and drop unavailable: " + ex.Message);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DROPFILES)
            {
                IntPtr hDrop = m.WParam;
                try
                {
                    var paths = new List<string>();
                    uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < count; i++)
                    {
                        uint length = DragQueryFile(hDrop, i, null, 0);
                        var buffer = new StringBuilder((int)length + 1);
                        DragQueryFile(hDrop, i, buffer, length + 1);
                        paths.Add(buffer.ToString());
                    }
                    DragQueryPoint(hDrop, out NativePoint point);
                    Point screen = PointToScreen(new Point(point.X, point.Y));
                    BeginInvoke(new Action(() => HandleDrop(paths, screen)));
                }
                catch (Exception ex)
                {
                    Log.Warn("Drop failed: " + ex.Message);
                }
                finally
                {
                    DragFinish(hDrop);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// One item dropped on the To box sets the destination; one item anywhere else sets what to
        /// move. Several items are added to the queue (with the current To folder).
        /// </summary>
        void HandleDrop(List<string> paths, Point screenPoint)
        {
            if (IsBusy || paths.Count == 0) return;
            bool onTo = textBox_To.RectangleToScreen(textBox_To.ClientRectangle).Contains(screenPoint) ||
                        button_BrowseTo.RectangleToScreen(button_BrowseTo.ClientRectangle).Contains(screenPoint);

            if (paths.Count == 1)
            {
                if (onTo)
                {
                    string folder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
                    textBox_To.Text = folder;
                }
                else
                {
                    textBox_From.Text = paths[0];
                }
                Activate();
                return;
            }

            string to = textBox_To.Text.Trim();
            if (to.Length == 0)
            {
                MessageBox.Show(this, R("Drop_NeedTo", "To move several items at once, first choose where to move them (To), then drop them again. They'll be added to the queue."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string root = to.Length > 3 ? to.TrimEnd('\\') : to;
            AddToQueue(paths.Select(p => new MoveJob { Source = p.TrimEnd('\\'), Destination = Path.Combine(root, Path.GetFileName(p.TrimEnd('\\'))) }));
        }

        #endregion
    }
}
