// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FreeMove
{
    /// <summary>
    /// A plain-text log in %APPDATA%\FreeMove\Logs, one file per day, kept for 14 days. It records
    /// what was moved, undone or repaired and every error, so a problem can be traced after the
    /// message box is gone (and attached to an issue report). Logging never throws.
    /// </summary>
    public static class Log
    {
        static readonly object Sync = new object();
        static bool cleaned;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeMove", "Logs");

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

        static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, "freemove-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                    string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + message + Environment.NewLine;
                    File.AppendAllText(file, line);
                    if (!cleaned)
                    {
                        cleaned = true;
                        DeleteOldLogs();
                    }
                }
            }
            catch { }
        }

        static void DeleteOldLogs()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-14);
                foreach (string file in Directory.GetFiles(Folder, "freemove-*.log").Where(f => File.GetLastWriteTime(f) < cutoff))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }
    }
}
