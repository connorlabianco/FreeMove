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
using System.Text;

namespace FreeMove.History
{
    /// <summary>A moved item that isn't working the way it was left.</summary>
    public class HealthIssue
    {
        public MoveEntry Entry { get; set; }
        public MoveHealth Health { get; set; }
        /// <summary>FreeMove can fix this by itself (recreate a missing link).</summary>
        public bool CanRepair => Health == MoveHealth.LinkMissing;
    }

    /// <summary>
    /// Checks every move that hasn't been undone: is the link still there, and are the moved files
    /// still reachable through it? Finds the problems that make programs act as if they were
    /// reinstalled: the destination drive is missing, or a reinstall replaced the link with a new folder.
    /// </summary>
    public static class HealthCheck
    {
        static string T(string key, string fallback) =>
            Properties.Resources.ResourceManager.GetString(key) ?? fallback;

        /// <summary>Problems found, if any. Touches the disk, so call it off the UI thread.</summary>
        public static List<HealthIssue> Run()
        {
            var issues = new List<HealthIssue>();
            foreach (MoveEntry entry in MoveHistory.ActiveMoves())
            {
                MoveHealth health = MoveHistory.Diagnose(entry);
                if (health != MoveHealth.Healthy) issues.Add(new HealthIssue { Entry = entry, Health = health });
            }
            return issues;
        }

        /// <summary>A plain explanation of the problem and what to do about it.</summary>
        public static string Describe(HealthIssue issue)
        {
            MoveEntry e = issue.Entry;
            switch (issue.Health)
            {
                case MoveHealth.TargetMissing:
                    string drive = Path.GetPathRoot(e.TargetPath);
                    bool driveMissing = !string.IsNullOrEmpty(drive) && !Directory.Exists(drive);
                    return driveMissing
                        ? string.Format(T("Health_DriveMissing", "{0}\n    The drive it was moved to ({1}) isn't connected. Reconnect it; programs using this folder won't work until then."), e.LinkPath, drive)
                        : string.Format(T("Health_TargetMissing", "{0}\n    The moved files at {1} are gone (deleted or renamed). The link leads nowhere, so programs using it will fail or start fresh."), e.LinkPath, e.TargetPath);
                case MoveHealth.Replaced:
                    return string.Format(T("Health_Replaced", "{0}\n    A new, real folder is there instead of the link (often after a reinstall or update), so new data is saved on this drive again. The moved files are still at {1}. Close the program, compare the two, then move the folder again or delete the one you don't need."), e.LinkPath, e.TargetPath);
                case MoveHealth.LinkMissing:
                    return string.Format(T("Health_LinkMissing", "{0}\n    The link was deleted, but the moved files are still at {1}. FreeMove can recreate the link."), e.LinkPath, e.TargetPath);
                case MoveHealth.PointsElsewhere:
                    return string.Format(T("Health_PointsElsewhere", "{0}\n    The link now points somewhere other than {1}. It was changed outside FreeMove."), e.LinkPath, e.TargetPath);
                default:
                    return string.Format(T("Health_Gone", "{0}\n    Neither the link nor the moved files at {1} exist anymore. You can remove this entry with \"Clear finished\"."), e.LinkPath, e.TargetPath);
            }
        }

        /// <summary>The full report for a list of problems.</summary>
        public static string Report(List<HealthIssue> issues)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(T("Health_Found", "{0} moved item(s) need attention:"), issues.Count);
            foreach (HealthIssue issue in issues)
            {
                sb.AppendLine().AppendLine();
                sb.Append(Describe(issue));
            }
            return sb.ToString();
        }

        /// <summary>Recreates a missing link. Returns null on success, otherwise the error.</summary>
        public static string Repair(HealthIssue issue)
        {
            if (!issue.CanRepair) return T("Health_CantRepair", "This can't be repaired automatically.");
            try
            {
                MoveEntry e = issue.Entry;
                if (File.Exists(e.LinkPath) || Directory.Exists(e.LinkPath) || IO.NativeFs.IsLink(e.LinkPath))
                    return T("Health_SomethingThere", "Something is at the original location now, so the link wasn't recreated.");
                string parent = Path.GetDirectoryName(e.LinkPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (!IOHelper.MakeLink(e.TargetPath, e.LinkPath))
                    return new System.ComponentModel.Win32Exception().Message;
                Log.Info($"Health check: recreated link {e.LinkPath} -> {e.TargetPath}");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
