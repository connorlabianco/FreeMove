// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace FreeMove.IO
{
    /// <summary>One thing the person should know before a move goes ahead.</summary>
    public class SafetyNote
    {
        /// <summary>True when the move is likely to break something; false for advice.</summary>
        public bool Serious { get; set; }
        public string Message { get; set; }
        public override string ToString() => (Serious ? "⚠ " : "ℹ ") + Message;
    }

    /// <summary>
    /// Looks for situations where a move technically works but is likely to cause trouble:
    /// cloud-synced folders, Microsoft Store apps, services, removable or network
    /// destinations and destinations that aren't NTFS. These are warnings the person can accept,
    /// unlike the hard checks in <see cref="IOHelper.CheckDirectories"/>.
    /// </summary>
    public static class SafetyCheck
    {
        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        static List<string> serviceFolders;

        /// <summary>
        /// Notes for moving <paramref name="source"/> to <paramref name="destination"/> (the full
        /// path the item will have). Reads the registry and the drives, so call it off the UI thread.
        /// </summary>
        public static List<SafetyNote> Assess(string source, string destination)
        {
            var notes = new List<SafetyNote>();
            source = source.TrimEnd('\\');
            destination = destination.TrimEnd('\\');

            AssessDestinationDrive(destination, notes);

            // Cloud-synced folders: the sync client gets confused when part of its folder becomes a link
            foreach (string cloud in CloudFolders())
            {
                if (ProcessFinder.IsUnder(source, cloud) || ProcessFinder.IsUnder(cloud, source))
                    notes.Add(Serious(string.Format(T("Safety_CloudSource", "{0} is synced by a cloud service ({1}). Moving it out through a link usually breaks syncing or makes the service upload or delete files. Use the service's own settings to change its folder instead."), source, cloud)));
                if (ProcessFinder.IsUnder(destination, cloud))
                    notes.Add(Serious(string.Format(T("Safety_CloudDest", "The destination is inside a cloud-synced folder ({0}). The moved files would be uploaded, and the service may lock them while syncing. Pick a folder outside it."), cloud)));
            }

            // Microsoft Store apps
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Under(source, Path.Combine(programFiles, "WindowsApps")) || Under(source, Path.Combine(localAppData, "Packages")))
                notes.Add(Serious(T("App_NoteStore", "Microsoft Store apps break when moved through a link. Use Settings > Apps > Installed apps > Move instead.")));

            // A service that starts with Windows: always in use, and Windows may not start it from a link
            try
            {
                if (serviceFolders == null) serviceFolders = Recommendations.RecommendationScanner.GetAutoStartServiceFolders();
                string service = serviceFolders.FirstOrDefault(f => ProcessFinder.IsUnder(f, source));
                if (service != null)
                    notes.Add(Serious(string.Format(T("Safety_Service", "A Windows service that starts with the PC runs from {0}. It is always in use, so the move will probably fail, and some services refuse to start from a link."), service)));
            }
            catch { }

            return notes;
        }

        static void AssessDestinationDrive(string destination, List<SafetyNote> notes)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(destination));
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;   // CheckDirectories reports a missing drive

                if (drive.DriveType == DriveType.Removable)
                    notes.Add(Serious(string.Format(T("Safety_Removable", "{0} is a removable drive. Whenever it is unplugged, the programs using the moved files stop working and may act as if they were reinstalled. Use an internal drive for anything that has to be there all the time."), root)));
                else if (drive.DriveType == DriveType.Network)
                    notes.Add(Serious(string.Format(T("Safety_Network", "{0} is a network drive. Programs will break whenever the network or the other computer is unavailable, and many programs don't work from network drives at all."), root)));

                string format = drive.DriveFormat ?? "";
                if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !format.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
                    notes.Add(Serious(string.Format(T("Safety_NotNtfs", "{0} is formatted as {1}, not NTFS. File permissions, hard links and hidden data streams can't be kept there, and some programs won't work correctly from it. An NTFS drive is strongly recommended."), root, format)));
            }
            catch { }
        }

        /// <summary>
        /// Folders kept in sync by cloud storage apps on this PC. Every modern sync app (OneDrive,
        /// Dropbox, Google Drive, iCloud, Box, ...) registers its folders with Windows under
        /// SyncRootManager, so no app names are needed. OneDrive's own environment variables are
        /// added for older OneDrive versions. Read locally from the registry; nothing goes online.
        /// </summary>
        public static List<string> CloudFolders()
        {
            var list = new List<string>();
            try
            {
                using (RegistryKey roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"))
                {
                    if (roots != null)
                    {
                        foreach (string name in roots.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey users = roots.OpenSubKey(name + @"\UserSyncRoots"))
                                {
                                    if (users == null) continue;
                                    foreach (string sid in users.GetValueNames())
                                        if (users.GetValue(sid) is string path && path.Length > 0) list.Add(path);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                string value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(value)) list.Add(value);
            }

            return list
                .Where(p => { try { return Directory.Exists(p); } catch { return false; } })
                .Select(p => p.TrimEnd('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static bool Under(string path, string folder) =>
            !string.IsNullOrEmpty(folder) && ProcessFinder.IsUnder(path, folder);

        static SafetyNote Serious(string message) => new SafetyNote { Serious = true, Message = message };
    }
}
