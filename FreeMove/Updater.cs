// FreeMove -- Move directories without breaking shortcuts or installations 
//    Copyright(C) 2020  Luca De Martini

//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//    GNU General Public License for more details.

//    You should have received a copy of the GNU General Public License
//    along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FreeMove
{
    public partial class Updater : Form
    {
        readonly string CurrentVersion;
        string NewVersion = "";
        readonly bool Silent;

        const string LatestReleaseApi = "https://api.github.com/repos/connorlabianco/FreeMove/releases/latest";
        const string LatestReleasePage = "https://github.com/connorlabianco/FreeMove/releases/latest";

        // One client for the app's lifetime, with a timeout so a slow network can't hang the check
        static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
#if NETFRAMEWORK
            // Allow TLS 1.2 (and 1.3 where Windows supports it) without forcing a version the OS
            // may not have: forcing TLS 1.3 made every check fail on older Windows
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(Properties.Resources.UserAgent, GetCurrentVersion()));
            return client;
        }

        static string GetCurrentVersion() =>
            System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;

        /// <summary>Shows the update window. Create it on the UI thread.</summary>
        public Updater(bool Silent, string newVersion = null)
        {
            this.Silent = Silent;
            this.CurrentVersion = GetCurrentVersion();
            if (newVersion != null) NewVersion = newVersion;
            InitializeComponent();
            Text = Properties.Resources.ResourceManager.GetString("Updater_FormTitle");
            label1.Text = Properties.Resources.ResourceManager.GetString("Updater_Checking");
            button_Ok.Text = Properties.Resources.ResourceManager.GetString("Button_OK");
            button_Cancel.Text = Properties.Resources.ResourceManager.GetString("Button_Cancel");
        }

        private void Updater_Shown(object sender, EventArgs e)
        {
            if (Silent)
                ShowUpdate();
            else
                Check();
        }

        private async void Check()
        {
            try
            {
                string latest = await GetLatestVersionAsync();
                if (IsNewer(latest, CurrentVersion))
                {
                    NewVersion = latest;
                    ShowUpdate();
                }
                else
                {
                    ShowMessage(Properties.Resources.ResourceManager.GetString("Updater_NoUpdates"));
                }
            }
            catch (Exception)
            {
                // Offline, timed out, GitHub unreachable or an unexpected reply: say so, never crash
                ShowMessage(Properties.Resources.GitHubErrorMessage);
            }
        }

        void ShowMessage(string text)
        {
            if (IsDisposed) return;
            label1.Text = text;
            progressBar1.Dispose();
            button_Ok.Enabled = true;
            button_Ok.Click += delegate { Dispose(); };
        }

        private void ShowUpdate()
        {
            progressBar1.Dispose();
            label1.Font = new Font("Lucida Console", label1.Font.Size);
            label1.Text = string.Format(CultureInfo.CurrentUICulture, Properties.Resources.ResourceManager.GetString("Updater_VersionAvailable"), CurrentVersion, NewVersion);
            button_Cancel.Enabled = true;
            button_Cancel.Click += delegate { Dispose(); };

            button_Ok.Enabled = true;
            button_Ok.Click += delegate { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LatestReleasePage) { UseShellExecute = true }); Dispose(); };
        }

        /// <summary>The latest released version on GitHub. Throws if it can't be determined.</summary>
        public static async Task<string> GetLatestVersionAsync()
        {
            using (Stream response = await Client.GetStreamAsync(LatestReleaseApi))
            using (var reader = new StreamReader(response))
            {
                const string pattern = "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"";
                string version = Regex.Match(await reader.ReadToEndAsync(), pattern).Groups[1].Value;
                if (version == "") throw new Exception(Properties.Resources.GitHubErrorMessage);
                return version;
            }
        }

        /// <summary>True when <paramref name="latest"/> is a higher version than <paramref name="current"/>.</summary>
        public static bool IsNewer(string latest, string current)
        {
            if (Version.TryParse(Pad(latest), out Version l) && Version.TryParse(Pad(current), out Version c))
                return l > c;
            return false;   // can't tell: don't offer an update that might be older
        }

        static string Pad(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            int parts = v.Split('.').Length;
            return parts == 1 ? v + ".0" : v;
        }

        /// <summary>
        /// Background check for a newer version. Returns it, or null when there is none or the check
        /// failed for any reason. Doesn't create any window, so it can run on any thread.
        /// </summary>
        public static async Task<string> SilentCheckAsync()
        {
            try
            {
                string latest = await GetLatestVersionAsync().ConfigureAwait(false);
                return IsNewer(latest, GetCurrentVersion()) ? latest : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
