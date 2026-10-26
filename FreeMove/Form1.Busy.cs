diff --git a/FreeMove/Form1.Busy.cs b/FreeMove/Form1.Busy.cs
new file mode 100644
index 0000000..707e4a9
--- /dev/null
+++ b/FreeMove/Form1.Busy.cs
@@ -0,0 +1,158 @@
+// FreeMove -- Move directories without breaking shortcuts or installations
+//    Copyright(C) 2020  Luca De Martini
+//
+//    This program is free software: you can redistribute it and/or modify
+//    it under the terms of the GNU General Public License as published by
+//    the Free Software Foundation, either version 3 of the License, or
+//    (at your option) any later version.
+
+using System;
+using System.Collections.Generic;
+using System.Drawing;
+using System.Threading.Tasks;
+using System.Windows.Forms;
+
+namespace FreeMove
+{
+    // Busy state while a move or undo runs, and the separate History / Recommended moves windows.
+    public partial class Form1
+    {
+        int busyDepth;
+        bool closeWhenIdle;
+        readonly List<Control> disabledWhileBusy = new List<Control>();
+
+        /// <summary>The batch being processed, so closing the window can skip what hasn't started.</summary>
+        BatchQueue currentQueue;
+
+        /// <summary>True while a move, undo or restore is running.</summary>
+        internal bool IsBusy => busyDepth > 0;
+
+        /// <summary>
+        /// Marks the window busy (or not). The window itself stays enabled so it keeps responding and
+        /// the title bar buttons work; only its contents are disabled. Calls can nest.
+        /// </summary>
+        void SetBusy(bool busy)
+        {
+            if (busy)
+            {
+                if (busyDepth++ > 0) return;
+                foreach (Control c in Controls)
+                {
+                    if (c.Enabled)
+                    {
+                        c.Enabled = false;
+                        disabledWhileBusy.Add(c);
+                    }
+                }
+                UseWaitCursor = true;
+            }
+            else
+            {
+                if (busyDepth == 0 || --busyDepth > 0) return;
+                foreach (Control c in disabledWhileBusy) c.Enabled = true;
+                disabledWhileBusy.Clear();
+                UseWaitCursor = false;
+                if (closeWhenIdle && !IsDisposed) BeginInvoke(new Action(Close));
+            }
+        }
+
+        /// <summary>
+        /// Closing while files are being moved: stopping in the middle of a step isn't safe, so the
+        /// items not started yet are skipped and the window closes by itself once the current step
+        /// is finished (or rolled back).
+        /// </summary>
+        protected override void OnFormClosing(FormClosingEventArgs e)
+        {
+            if (IsBusy && e.CloseReason == CloseReason.UserClosing)
+            {
+                e.Cancel = true;
+                if (!closeWhenIdle)
+                {
+                    closeWhenIdle = true;
+                    if (currentQueue != null) currentQueue.SkipRemaining = true;
+                    Text = Text + " - " + R("Busy_ClosingWhenDone", "will close when the current item is finished...");
+                }
+            }
+            base.OnFormClosing(e);
+        }
+
+        /// <summary>Shows an independent window (its own taskbar button) centered over this one.</summary>
+        void ShowSideWindow(Form form)
+        {
+            form.Icon = Icon;
+            form.ShowIcon = true;
+            form.ShowInTaskbar = true;
+            form.StartPosition = FormStartPosition.Manual;
+            Rectangle area = Screen.FromControl(this).WorkingArea;
+            int x = Left + (Width - form.Width) / 2;
+            int y = Top + (Height - form.Height) / 2;
+            x = Math.Max(area.Left, Math.Min(x, area.Right - form.Width));
+            y = Math.Max(area.Top, Math.Min(y, area.Bottom - form.Height));
+            form.Location = new Point(x, y);
+            form.Show();
+        }
+
+        /// <summary>Brings an already open window to the front.</summary>
+        static void ActivateWindow(Form form)
+        {
+            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
+            form.Activate();
+        }
+
+        /// <summary>Gives a dialog FreeMove's icon and its own taskbar button, so it can't get lost behind other windows.</summary>
+        void PrepareDialog(Form form)
+        {
+            form.Icon = Icon;
+            form.ShowIcon = true;
+            form.ShowInTaskbar = true;
+        }
+
+        /// <summary>
+        /// Runs the pre-move checks in the background with the progress window up ("Checking
+        /// files..."), so the window keeps responding and, in a batch, the queue line with Skip /
+        /// Skip remaining is shown. Throws <see cref="OperationCanceledException"/> if the person
+        /// cancels or skips; the checks only read files, so stopping waiting for them is safe.
+        /// </summary>
+        Task RunCheck(Action check, BatchQueue queue = null) =>
+            RunOperation(new CheckOperation(check), queue,
+                R("Check_Checking", "Checking the files before moving..."));
+
+        /// <summary>A background check that can be abandoned from the progress window.</summary>
+        class CheckOperation : IO.IOOperation
+        {
+            readonly Action check;
+            readonly TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();
+
+            public CheckOperation(Action check) { this.check = check; }
+
+            public override void Cancel() => cancelled.TrySetResult(true);
+
+            public override async Task Run()
+            {
+                OnStart(EventArgs.Empty);
+                try
+                {
+                    Task work = Task.Run(check);
+                    if (await Task.WhenAny(work, cancelled.Task) != work)
+                    {
+                        // Let the abandoned check finish quietly in the background
+                        _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
+                        throw new OperationCanceledException();
+                    }
+                    await work;   // rethrows the check's own error
+                }
+                finally
+                {
+                    OnEnd(EventArgs.Empty);
+                }
+            }
+        }
+
+        void ShowBusyMessage(IWin32Window owner)
+        {
+            MessageBox.Show(owner,
+                R("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
+                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
+        }
+    }
+}
diff --git a/FreeMove/Form1.History.cs b/FreeMove/Form1.History.cs
index d542a46..fc9807f 100644
--- a/FreeMove/Form1.History.cs
+++ b/FreeMove/Form1.History.cs
@@ -30,15 +30,29 @@ namespace FreeMove
             ShowHistory();
         }
 
-        private async void ShowHistory()
+        HistoryForm historyWindow;
+
+        /// <summary>
+        /// Opens History as its own window (with its own taskbar button), so you can switch between
+        /// it and the main window. Only one is open at a time.
+        /// </summary>
+        private void ShowHistory()
         {
-            List<MoveEntry> selected;
-            using (var form = new HistoryForm())
+            if (historyWindow != null && !historyWindow.IsDisposed)
             {
-                if (form.ShowDialog(this) != DialogResult.OK) return;
-                selected = form.SelectedEntries;
+                ActivateWindow(historyWindow);
+                return;
             }
-            if (selected.Count > 0) await UndoEntries(selected);
+
+            var form = new HistoryForm { IsBusy = () => IsBusy };
+            form.UndoAccepted += async (s, entries) =>
+            {
+                if (IsBusy) { ShowBusyMessage(this); return; }
+                if (entries.Count > 0) await UndoEntries(entries);
+            };
+            form.FormClosed += (s, e) => { if (ReferenceEquals(historyWindow, s)) historyWindow = null; };
+            historyWindow = form;
+            ShowSideWindow(form);
         }
 
         /// <summary>
@@ -49,6 +63,20 @@ namespace FreeMove
         /// "Skip remaining" stops before the next item; cancelling stops. A summary is shown at the end.
         /// </summary>
         private async Task UndoEntries(List<MoveEntry> entries, bool keepMovedCopy = false)
+        {
+            if (IsBusy) { ShowBusyMessage(this); return; }
+            SetBusy(true);   // held until after the summary, so closing the window waits for all of it
+            try
+            {
+                await UndoEntriesCore(entries, keepMovedCopy);
+            }
+            finally
+            {
+                SetBusy(false);
+            }
+        }
+
+        private async Task UndoEntriesCore(List<MoveEntry> entries, bool keepMovedCopy)
         {
             var undone = new List<MoveEntry>();
             var failed = new List<string>();
@@ -57,7 +85,6 @@ namespace FreeMove
             bool cancelled = false;
             Guid batchId = Guid.NewGuid();
 
-            Enabled = false;
             try
             {
                 var inUse = new List<InUseItem>();
@@ -65,9 +92,16 @@ namespace FreeMove
 
                 while (!cancelled && inUse.Count > 0)
                 {
+                    if (closeWhenIdle)
+                    {
+                        skipped.AddRange(inUse.Select(i => i.DisplayPath + "  " + R("InUse_SkippedNote", "(in use)")));
+                        break;
+                    }
+
                     InUseChoice choice;
                     using (var form = new InUseForm(inUse))
                     {
+                        PrepareDialog(form);
                         form.ShowDialog(this);
                         choice = form.Choice;
                     }
@@ -87,10 +121,11 @@ namespace FreeMove
             }
             finally
             {
-                Enabled = true;
                 directoryBrowser1.RefreshBrowser();
             }
 
+            if (closeWhenIdle) return;   // closing: the results are in History
+
             var summary = new StringBuilder();
             summary.AppendFormat(R("Hist_SummaryUndone", "Moved {0} item(s), {1} in total, back to their original location."),
                 undone.Count, RecommendationScanner.FormatSize(undone.Sum(u => u.SizeBytes)));
@@ -111,7 +146,8 @@ namespace FreeMove
         private async Task<bool> RunUndoQueue(List<MoveEntry> entries, bool keepMovedCopy, Guid batchId, List<MoveEntry> undone,
             List<string> failed, List<string> warnings, List<string> skipped, List<InUseItem> inUse)
         {
-            var queue = new BatchQueue { Count = entries.Count };
+            var queue = new BatchQueue { Count = entries.Count, SkipRemaining = closeWhenIdle };
+            currentQueue = queue;
             for (int i = 0; i < entries.Count; i++)
             {
                 MoveEntry entry = entries[i];
diff --git a/FreeMove/Form1.Recommendations.cs b/FreeMove/Form1.Recommendations.cs
index b663993..27bdd84 100644
--- a/FreeMove/Form1.Recommendations.cs
+++ b/FreeMove/Form1.Recommendations.cs
@@ -52,20 +52,29 @@ namespace FreeMove
         static string R(string key, string fallback) =>
             Properties.Resources.ResourceManager.GetString(key) ?? fallback;
 
-        private async void RecommendedMovesToolStripMenuItem_Click(object sender, EventArgs e)
-        {
-            List<MoveRecommendation> accepted;
-            string destinationRoot;
+        RecommendationsForm recommendationsWindow;
 
-            using (var form = new RecommendationsForm())
+        /// <summary>
+        /// Opens Recommended moves as its own window (with its own taskbar button), so you can switch
+        /// between it and the main window. Only one is open at a time.
+        /// </summary>
+        private void RecommendedMovesToolStripMenuItem_Click(object sender, EventArgs e)
+        {
+            if (recommendationsWindow != null && !recommendationsWindow.IsDisposed)
             {
-                if (form.ShowDialog(this) != DialogResult.OK) return;
-                accepted = form.SelectedRecommendations;
-                destinationRoot = form.DestinationRoot;
+                ActivateWindow(recommendationsWindow);
+                return;
             }
 
-            if (accepted.Count == 0) return;
-            await MoveRecommendations(accepted, destinationRoot);
+            var form = new RecommendationsForm { IsBusy = () => IsBusy };
+            form.MoveAccepted += async (s, args) =>
+            {
+                if (IsBusy) { ShowBusyMessage(this); return; }
+                if (args.Items.Count > 0) await MoveRecommendations(args.Items, args.DestinationRoot);
+            };
+            form.FormClosed += (s, args) => { if (ReferenceEquals(recommendationsWindow, s)) recommendationsWindow = null; };
+            recommendationsWindow = form;
+            ShowSideWindow(form);
         }
 
         /// <summary>
@@ -74,6 +83,19 @@ namespace FreeMove
         /// the person chooses to overwrite it.
         /// </summary>
         private async Task MoveRecommendations(List<MoveRecommendation> items, string destinationRoot)
+        {
+            SetBusy(true);
+            try
+            {
+                await MoveRecommendationsCore(items, destinationRoot);
+            }
+            finally
+            {
+                SetBusy(false);
+            }
+        }
+
+        private async Task MoveRecommendationsCore(List<MoveRecommendation> items, string destinationRoot)
         {
             var jobs = items.Select(i => new MoveJob
             {
@@ -93,6 +115,7 @@ namespace FreeMove
                     DestinationPath = c.Destination
                 }).ToList()))
                 {
+                    PrepareDialog(form);
                     if (form.ShowDialog(this) != DialogResult.OK) return;
                     chosen = form.OverwriteIndices;
                 }
@@ -106,6 +129,7 @@ namespace FreeMove
             }
 
             MoveBatchResult result = await RunMoveJobs(jobs, recommendedBatch: true);
+            if (closeWhenIdle) return;   // closing: the results are in History
             result.Skipped.InsertRange(0, skippedConflicts);
             ShowBatchSummary(result, R("Rec_SummaryTitle", "Recommended moves"));
         }
@@ -120,7 +144,7 @@ namespace FreeMove
             var result = new MoveBatchResult();
             Guid batchId = Guid.NewGuid();
 
-            Enabled = false;
+            SetBusy(true);
             try
             {
                 var inUse = new List<InUseItem>();
@@ -128,9 +152,16 @@ namespace FreeMove
 
                 while (!result.Cancelled && inUse.Count > 0)
                 {
+                    if (closeWhenIdle)
+                    {
+                        result.Skipped.AddRange(inUse.Select(i => i.DisplayPath + "  " + R("InUse_SkippedNote", "(in use)")));
+                        break;
+                    }
+
                     InUseChoice choice;
                     using (var form = new InUseForm(inUse))
                     {
+                        PrepareDialog(form);
                         form.ShowDialog(this);
                         choice = form.Choice;
                     }
@@ -150,8 +181,8 @@ namespace FreeMove
             }
             finally
             {
-                Enabled = true;
                 directoryBrowser1.RefreshBrowser();
+                SetBusy(false);
             }
             return result;
         }
@@ -211,7 +242,8 @@ namespace FreeMove
         private async Task<bool> RunMoveQueue(List<MoveJob> jobs, Guid batchId, bool recommendedBatch,
             MoveBatchResult result, List<InUseItem> inUse)
         {
-            var queue = new BatchQueue { Count = jobs.Count };
+            var queue = new BatchQueue { Count = jobs.Count, SkipRemaining = closeWhenIdle };
+            currentQueue = queue;
             for (int i = 0; i < jobs.Count; i++)
             {
                 MoveJob job = jobs[i];
@@ -297,11 +329,19 @@ namespace FreeMove
                 createdParents = IOHelper.CreateDirectoryTracked(Path.GetDirectoryName(destination));
                 if (!job.ChecksDone)
                 {
-                    // Same checks as a manual move (protected folders, safe mode, permissions, free space)
-                    IOHelper.CheckDirectories(source, destination, safeMode, createDestination: true,
-                        skipAllChecks: skipSecurityChecks, allowExistingDestination: job.Overwrite);
+                    // Same checks as a manual move (protected folders, safe mode, permissions, free space).
+                    // They read every file, so they run in the background to keep the window responsive.
+                    bool safe = safeMode, skipChecks = skipSecurityChecks, overwrite = job.Overwrite;
+                    await RunCheck(() => IOHelper.CheckDirectories(source, destination, safe, createDestination: true,
+                        skipAllChecks: skipChecks, allowExistingDestination: overwrite), queue);
                 }
             }
+            catch (OperationCanceledException)
+            {
+                // Skipped or cancelled from the progress window while checking: nothing was moved
+                IOHelper.RemoveEmptyDirectories(createdParents);
+                return new MoveOutcome { Cancelled = true };
+            }
             catch (AggregateException ae) when (!ae.InnerExceptions.All(x => x is IOHelper.ReadOnlyPrecheckException))
             {
                 IOHelper.RemoveEmptyDirectories(createdParents);
@@ -353,7 +393,8 @@ namespace FreeMove
                 // Copy failed, was cancelled or skipped, or a same-drive move failed: the original is
                 // still complete, so only the destination needs undoing
                 bool cancelled = ex is OperationCanceledException;
-                string rollbackProblem = RollBackCopy(journal, source, destination);
+                // Deleting a partial copy can take a while: do it in the background
+                string rollbackProblem = await Task.Run(() => RollBackCopy(journal, source, destination));
                 if (rollbackProblem == null) CleanUpAfterRollback(journal, op, createdParents);
                 else journal.Close();   // kept: the next start retries the rollback
 
diff --git a/FreeMove/Form1.cs b/FreeMove/Form1.cs
index a411bbf..187ca4a 100644
--- a/FreeMove/Form1.cs
+++ b/FreeMove/Form1.cs
@@ -246,13 +246,20 @@ namespace FreeMove
         /// <summary>
         /// 执行移动前预检查，并在失败原因仅为只读属性时提供自动修复入口。
         /// </summary>
-        private bool PreliminaryCheck(string source, string destination, bool allowExistingDestination = false)
+        private async Task<bool> PreliminaryCheck(string source, string destination, bool allowExistingDestination = false)
         {
+            // The checks read every file of the folder, which can take minutes on a big folder:
+            // run them in the background so the window keeps responding
+            bool safe = safeMode, createDest = chkBox_createDest.Checked, skipChecks = skipSecurityChecks;
             try
             {
-                IOHelper.CheckDirectories(source, destination, safeMode, chkBox_createDest.Checked, skipAllChecks: skipSecurityChecks, allowExistingDestination: allowExistingDestination);
+                await RunCheck(() => IOHelper.CheckDirectories(source, destination, safe, createDest, skipAllChecks: skipChecks, allowExistingDestination: allowExistingDestination));
                 return true;
             }
+            catch (OperationCanceledException)
+            {
+                return false;   // cancelled from the progress window: nothing was changed
+            }
             catch (AggregateException ae) when (IOHelper.TryGetReadOnlyPrecheckFailures(ae, out List<IOHelper.ReadOnlyPrecheckException> readOnlyFailures))
             {
                 // If every failure comes from read-only attributes, offer an in-place repair instead of failing immediately.
@@ -271,7 +278,7 @@ namespace FreeMove
 
                 try
                 {
-                    IOHelper.ClearReadOnlyAttributes(source);
+                    await Task.Run(() => IOHelper.ClearReadOnlyAttributes(source));
                 }
                 catch (Exception ex)
                 {
@@ -287,9 +294,13 @@ namespace FreeMove
                 try
                 {
                     // Re-run with Full so the move only continues after all files pass the stricter access check.
-                    IOHelper.CheckDirectories(source, destination, safeMode, chkBox_createDest.Checked, Settings.PermissionCheckLevel.Full, skipAllChecks: skipSecurityChecks, allowExistingDestination: allowExistingDestination);
+                    await RunCheck(() => IOHelper.CheckDirectories(source, destination, safe, createDest, Settings.PermissionCheckLevel.Full, skipAllChecks: skipChecks, allowExistingDestination: allowExistingDestination));
                     return true;
                 }
+                catch (OperationCanceledException)
+                {
+                    return false;
+                }
                 catch (Exception ex)
                 {
                     ShowPreliminaryCheckError(ex);
@@ -321,7 +332,8 @@ namespace FreeMove
 
         private async void Begin()
         {
-            Enabled = false;
+            if (IsBusy) return;
+            SetBusy(true);
             try
             {
                 string source = textBox_From.Text.TrimEnd('\\');
@@ -343,12 +355,14 @@ namespace FreeMove
 
                 // Checks with the interactive fix for read-only files; the move then runs through the
                 // same path as Recommended moves: journal, verified copy, link check, rollback
-                if (!PreliminaryCheck(source, destination, allowExistingDestination: overwrite))
+                if (!await PreliminaryCheck(source, destination, allowExistingDestination: overwrite))
                     return;
+                if (closeWhenIdle) return;   // the window was closed during the checks: don't start
 
                 var job = new MoveJob { Source = source, Destination = destination, Overwrite = overwrite, ChecksDone = true };
                 MoveBatchResult result = await RunMoveJobs(new List<MoveJob> { job }, recommendedBatch: false);
 
+                if (closeWhenIdle) return;   // closing: the result is in History
                 if (result.Moved.Count == 1 && result.Warnings.Count == 0)
                     MessageBox.Show(this, Properties.Resources.ResourceManager.GetString("DoneMessage"));
                 else
@@ -356,7 +370,7 @@ namespace FreeMove
             }
             finally
             {
-                Enabled = true;
+                SetBusy(false);
             }
         }
 
@@ -375,11 +389,13 @@ namespace FreeMove
         /// continuation of the operation's task, which runs inside the window's message loop, so it
         /// closes reliably even when the operation finishes or fails before the window is shown.
         /// </summary>
-        private async Task RunOperation(IO.IOOperation operation, BatchQueue queue = null)
+        private async Task RunOperation(IO.IOOperation operation, BatchQueue queue = null, string indeterminateMessage = null)
         {
             using (ProgressDialog progressDialog = new ProgressDialog(Properties.Resources.ResourceManager.GetString("MovingFilesTitle")))
             {
                 Task task = null;
+                PrepareDialog(progressDialog);
+                if (indeterminateMessage != null) progressDialog.ShowIndeterminate(indeterminateMessage);
                 operation.ProgressChanged += (sender, e) => progressDialog.UpdateProgress(e);
 
                 progressDialog.CancelRequested += (sender, e) =>
diff --git a/FreeMove/History/HistoryForm.cs b/FreeMove/History/HistoryForm.cs
index 6989542..ba0b72a 100644
--- a/FreeMove/History/HistoryForm.cs
+++ b/FreeMove/History/HistoryForm.cs
@@ -55,6 +55,12 @@ namespace FreeMove.History
             }
         }
 
+        /// <summary>Asked before undoing: true while the main window is already moving files.</summary>
+        public Func<bool> IsBusy { get; set; }
+
+        /// <summary>Raised (after this window closes) with the entries the person chose to undo.</summary>
+        public event EventHandler<List<MoveEntry>> UndoAccepted;
+
         public HistoryForm()
         {
             Text = T("Hist_Title", "Move history");
@@ -91,7 +97,8 @@ namespace FreeMove.History
             button_Clear.Click += Clear_Click;
             button_Undo = new Button { Text = T("Hist_UndoSelected", "Undo selected"), AutoSize = true, Enabled = false };
             button_Undo.Click += Undo_Click;
-            button_Close = new Button { Text = T("Button_Close", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
+            button_Close = new Button { Text = T("Button_Close", "Close"), AutoSize = true };
+            button_Close.Click += (s, e) => Close();   // this is an independent window, not a dialog
             button_DeleteBackups = new Button { Text = T("Hist_DeleteBackups", "Delete backups"), AutoSize = true, Enabled = false };
             button_DeleteBackups.Click += DeleteBackups_Click;
 
@@ -310,6 +317,12 @@ namespace FreeMove.History
         {
             List<MoveEntry> selected = SelectedEntries;
             if (selected.Count == 0) return;
+            if (IsBusy?.Invoke() == true)
+            {
+                MessageBox.Show(this, T("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
+                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
+                return;
+            }
 
             string list = string.Join("\n", selected.Select(s => $"{s.TargetPath}  →  {s.LinkPath}"));
 
@@ -319,8 +332,8 @@ namespace FreeMove.History
 
             if (Dialogs.ScrollableMessageBox.Show(this, message, T("Hist_UndoTitle", "Undo moves"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
             {
-                DialogResult = DialogResult.OK;
                 Close();
+                UndoAccepted?.Invoke(this, selected);
             }
         }
     }
diff --git a/FreeMove/ProgressDialog.cs b/FreeMove/ProgressDialog.cs
index ad6a50b..05875dd 100644
--- a/FreeMove/ProgressDialog.cs
+++ b/FreeMove/ProgressDialog.cs
@@ -56,6 +56,18 @@ namespace FreeMove
             label_Progress.Text = Message;
         }
 
+        /// <summary>
+        /// For a step with no measurable progress (such as checking the files before a move):
+        /// shows the message and a moving bar instead of a count.
+        /// </summary>
+        public void ShowIndeterminate(string message)
+        {
+            Message = message;
+            label_Progress.Text = "";
+            progressBar1.Style = ProgressBarStyle.Marquee;
+            progressBar1.MarqueeAnimationSpeed = 30;
+        }
+
         public event EventHandler CancelRequested
         {
             add => button_Cancel.Click += value;
diff --git a/FreeMove/Recommendations/RecommendationsForm.cs b/FreeMove/Recommendations/RecommendationsForm.cs
index 9e5eaed..d698466 100644
--- a/FreeMove/Recommendations/RecommendationsForm.cs
+++ b/FreeMove/Recommendations/RecommendationsForm.cs
@@ -17,12 +17,25 @@ using System.Windows.Forms;
 
 namespace FreeMove.Recommendations
 {
+    /// <summary>The folders the person chose to move, and where to.</summary>
+    public class MoveAcceptedEventArgs : EventArgs
+    {
+        public List<MoveRecommendation> Items { get; set; }
+        public string DestinationRoot { get; set; }
+    }
+
     /// <summary>
     /// Lists the largest movable folders on the system drive. The person ticks the ones to move,
-    /// and on OK the caller moves each one to <see cref="DestinationRoot"/> and links it back.
+    /// and on Move the caller (via <see cref="MoveAccepted"/>) moves each one to <see cref="DestinationRoot"/> and links it back.
     /// </summary>
     public class RecommendationsForm : Form
     {
+        /// <summary>Asked before moving: true while the main window is already moving files.</summary>
+        public Func<bool> IsBusy { get; set; }
+
+        /// <summary>Raised (after this window closes) with the folders the person chose to move.</summary>
+        public event EventHandler<MoveAcceptedEventArgs> MoveAccepted;
+
         readonly TextBox textBox_Dest;
         readonly Button button_BrowseDest;
         readonly NumericUpDown numeric_MinSize;
@@ -122,7 +135,8 @@ namespace FreeMove.Recommendations
             button_SelectNone.Click += (s, e) => SetAllChecked(false);
             button_Move = new Button { Text = T("Rec_MoveSelected", "Move selected"), AutoSize = true, Enabled = false };
             button_Move.Click += Move_Click;
-            button_Cancel = new Button { Text = T("Button_Close", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
+            button_Cancel = new Button { Text = T("Button_Close", "Close"), AutoSize = true };
+            button_Cancel.Click += (s, e) => Close();   // this is an independent window, not a dialog
 
             bottom.Controls.Add(label_Status, 0, 0);
             bottom.Controls.Add(button_SelectAll, 1, 0);
@@ -315,6 +329,12 @@ namespace FreeMove.Recommendations
         {
             List<MoveRecommendation> selected = SelectedRecommendations;
             if (selected.Count == 0) return;
+            if (IsBusy?.Invoke() == true)
+            {
+                MessageBox.Show(this, T("Busy_AlreadyMoving", "FreeMove is already moving files. Wait for it to finish, then try again."),
+                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
+                return;
+            }
 
             string list = string.Join("\n", selected.Select(r =>
                 $"{r.SourcePath}  ({RecommendationScanner.FormatSize(r.SizeBytes)})"));
@@ -325,8 +345,9 @@ namespace FreeMove.Recommendations
 
             if (Dialogs.ScrollableMessageBox.Show(this, message, T("Rec_ConfirmTitle", "Confirm move"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
             {
-                DialogResult = DialogResult.OK;
+                var args = new MoveAcceptedEventArgs { Items = selected, DestinationRoot = DestinationRoot };
                 Close();
+                MoveAccepted?.Invoke(this, args);
             }
         }
     }
