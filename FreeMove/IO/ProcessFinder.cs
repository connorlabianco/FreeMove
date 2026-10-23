// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeMove.IO
{
    /// <summary>A running process that is using a folder or file.</summary>
    public class LockingProcess
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ImagePath { get; set; }
        /// <summary>
        /// False for services, critical system processes and Explorer, which FreeMove never ends.
        /// </summary>
        public bool CanEnd { get; set; } = true;
        /// <summary>
        /// When the process started. Process ids are reused, so before ending a process FreeMove
        /// checks that the id still belongs to the same process (same start time and name).
        /// </summary>
        public DateTime? StartTime { get; set; }

        public override string ToString() => $"{Name} (PID {Id})";
    }

    /// <summary>
    /// Finds the processes using a folder: programs running from inside it, plus processes that
    /// hold its files open (via the Windows Restart Manager, the same API installers use).
    /// </summary>
    public static class ProcessFinder
    {
        #region Native
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        const int RmRebootReasonNone = 0;
        const int CCH_RM_MAX_APP_NAME = 255;
        const int CCH_RM_MAX_SVC_NAME = 63;
        const int ERROR_MORE_DATA = 234;

        enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
        #endregion

        /// <summary>
        /// Full path of every running process's executable, by process id. Processes FreeMove
        /// cannot inspect are left out.
        /// </summary>
        public static Dictionary<int, string> GetProcessImagePaths()
        {
            var result = new Dictionary<int, string>();
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string path = GetImagePath(p.Id);
                    if (path != null) result[p.Id] = path;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return result;
        }

        static string GetImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(32768);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        static DateTime? GetStartTime(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return p.StartTime; }
            catch { return null; }
        }

        /// <summary>Describes a running process for the in-use list.</summary>
        public static LockingProcess Describe(int pid, string imagePath) =>
            new LockingProcess { Id = pid, Name = Path.GetFileName(imagePath), ImagePath = imagePath, StartTime = GetStartTime(pid) };

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        public static bool IsUnder(string path, string folder)
        {
            string p = path.TrimEnd('\\');
            string f = folder.TrimEnd('\\');
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Names of programs running from inside <paramref name="folder"/> (fast).</summary>
        public static List<string> RunningFrom(string folder, Dictionary<int, string> imagePaths)
        {
            return imagePaths.Values
                .Where(path => IsUnder(path, folder))
                .Select(Path.GetFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Processes using <paramref name="path"/> (a folder or a file). Checks programs running
        /// from inside it, then asks the Restart Manager about open files: first the files named in
        /// <paramref name="hintFiles"/> (e.g. the file an error mentioned), then the folder's
        /// executables and libraries, then up to <paramref name="maxFiles"/> files in total.
        /// </summary>
        public static List<LockingProcess> FindUsers(string path, IEnumerable<string> hintFiles = null, int maxFiles = 50000)
        {
            var found = new Dictionary<int, LockingProcess>();
            int self = Process.GetCurrentProcess().Id;

            // 1. Programs whose executable lives inside the folder
            foreach (KeyValuePair<int, string> kv in GetProcessImagePaths())
            {
                if (IsUnder(kv.Value, path))
                    found[kv.Key] = Describe(kv.Key, kv.Value);
            }

            // 2. Processes holding files open
            List<string> files = (hintFiles ?? Enumerable.Empty<string>()).Where(File.Exists)
                .Concat(SampleFiles(path, maxFiles))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxFiles)
                .ToList();
            if (files.Count > 0)
            {
                foreach (LockingProcess lp in RestartManagerUsers(files))
                {
                    if (lp.Id == self) continue;
                    if (found.TryGetValue(lp.Id, out LockingProcess existing))
                        existing.CanEnd &= lp.CanEnd;
                    else
                        found[lp.Id] = lp;
                }
            }

            foreach (LockingProcess lp in found.Values)
            {
                if (string.Equals(lp.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase))
                    lp.CanEnd = false;
            }

            return found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static List<string> SampleFiles(string path, int maxFiles)
        {
            var files = new List<string>();
            if (File.Exists(path))
            {
                files.Add(path);
                return files;
            }
            if (!Directory.Exists(path)) return files;

            // Executables and libraries first: they are what running programs keep open
            var pending = new Stack<string>();
            pending.Push(path);
            var others = new List<string>();
            while (pending.Count > 0 && files.Count < maxFiles)
            {
                string dir = pending.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        string ext = Path.GetExtension(f);
                        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                            files.Add(f);
                        else if (others.Count < maxFiles)
                            others.Add(f);
                        if (files.Count >= maxFiles) break;
                    }
                    foreach (string d in Directory.EnumerateDirectories(dir))
                    {
                        if (!NativeFs.IsLink(d)) pending.Push(d);
                    }
                }
                catch { }
            }
            files.AddRange(others.Take(Math.Max(0, maxFiles - files.Count)));
            return files;
        }

        static List<LockingProcess> RestartManagerUsers(List<string> files)
        {
            var result = new List<LockingProcess>();
            if (RmStartSession(out uint session, 0, new StringBuilder(64)) != 0) return result;
            try
            {
                // Register in chunks; the API copes with large lists but chunks keep memory modest
                const int chunk = 500;
                for (int i = 0; i < files.Count; i += chunk)
                {
                    string[] part = files.Skip(i).Take(chunk).ToArray();
                    if (RmRegisterResources(session, (uint)part.Length, part, 0, null, 0, null) != 0)
                        return result;
                }

                uint needed = 0, count = 0, reasons = RmRebootReasonNone;
                int rc = RmGetList(session, out needed, ref count, null, ref reasons);
                if (rc == ERROR_MORE_DATA && needed > 0)
                {
                    var infos = new RM_PROCESS_INFO[needed];
                    count = needed;
                    rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                    if (rc != 0) return result;

                    for (int i = 0; i < count; i++)
                    {
                        RM_PROCESS_INFO info = infos[i];
                        int pid = info.Process.dwProcessId;
                        string image = null;
                        try { image = GetImagePath(pid); } catch { }
                        result.Add(new LockingProcess
                        {
                            Id = pid,
                            Name = image != null ? Path.GetFileName(image) : info.strAppName,
                            ImagePath = image,
                            StartTime = DateTime.FromFileTime(((long)(uint)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime),
                            CanEnd = info.ApplicationType != RM_APP_TYPE.RmService &&
                                     info.ApplicationType != RM_APP_TYPE.RmCritical &&
                                     info.ApplicationType != RM_APP_TYPE.RmExplorer
                        });
                    }
                }
            }
            catch { }
            finally
            {
                RmEndSession(session);
            }
            return result;
        }

        /// <summary>
        /// Ends the given processes (and the processes they started) and waits briefly for them to
        /// exit. A process is ended only if its id still belongs to the same program: same name and
        /// start time as when it was found. Returns messages for any that could not be ended.
        /// </summary>
        public static List<string> EndProcesses(IEnumerable<LockingProcess> processes)
        {
            var errors = new List<string>();
            foreach (LockingProcess lp in processes.Where(p => p.CanEnd).GroupBy(p => p.Id).Select(g => g.First()))
            {
                try
                {
                    using (Process p = Process.GetProcessById(lp.Id))
                    {
                        if (!IsSameProcess(p, lp))
                            continue;   // it already exited and the id now belongs to another program

                        // Ask nicely first so the program can save, then force it
                        if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(3000))
                            continue;

                        foreach (int child in GetDescendants(p.Id, SafeStartTime(p)))
                        {
                            try { using (Process c = Process.GetProcessById(child)) c.Kill(); } catch { }
                        }
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (ArgumentException)
                {
                    // Already exited
                }
                catch (Exception ex)
                {
                    errors.Add($"{lp}: {ex.Message}");
                }
            }
            return errors;
        }

        static DateTime? SafeStartTime(Process p)
        {
            try { return p.StartTime; } catch { return null; }
        }

        static bool IsSameProcess(Process p, LockingProcess expected)
        {
            DateTime? start = SafeStartTime(p);
            if (expected.StartTime.HasValue && start.HasValue &&
                Math.Abs((expected.StartTime.Value - start.Value).TotalSeconds) > 1)
                return false;
            if (!expected.StartTime.HasValue || !start.HasValue)
            {
                // Without start times, fall back to the executable path
                string image = GetImagePath(p.Id);
                if (expected.ImagePath != null && !string.Equals(image, expected.ImagePath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        #region Process tree
        const uint TH32CS_SNAPPROCESS = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        /// <summary>
        /// Processes started (directly or indirectly) by <paramref name="pid"/>. A child must have
        /// started after its parent, which rules out unrelated processes that reused a parent id.
        /// </summary>
        static List<int> GetDescendants(int pid, DateTime? parentStart)
        {
            var parents = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return new List<int>();
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do { parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var result = new List<int>();
            var queue = new Queue<KeyValuePair<int, DateTime?>>();
            queue.Enqueue(new KeyValuePair<int, DateTime?>(pid, parentStart));
            while (queue.Count > 0)
            {
                KeyValuePair<int, DateTime?> parent = queue.Dequeue();
                foreach (KeyValuePair<int, int> kv in parents)
                {
                    if (kv.Value != parent.Key || kv.Key == parent.Key || result.Contains(kv.Key)) continue;
                    DateTime? childStart = GetStartTime(kv.Key);
                    if (parent.Value.HasValue && childStart.HasValue && childStart.Value < parent.Value.Value) continue;
                    result.Add(kv.Key);
                    queue.Enqueue(new KeyValuePair<int, DateTime?>(kv.Key, childStart));
                }
            }
            result.Reverse();   // end the deepest children first
            return result;
        }
        #endregion
    }
}
