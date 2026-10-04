// SPDX-License-Identifier: GPL-3.0-or-later
// Updates from GitHub Releases. The latest release of github.com/<update.repo> is checked at start and
// every few hours. Updating downloads that release's SwarlexBattery.exe, checks it against
// SwarlexBattery.exe.sha256 from the same release, swaps it in place of the running exe (a running exe
// can be renamed, not overwritten) and restarts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SwarlexBattery
{
    class ReleaseInfo { public string Tag, Url, Sha, Page; public Version Version; }

    static class Updater
    {
        const string Agent = "SwarlexBattery-updater";

        public static Version Norm(string v)
        {
            Version x;
            if (v == null || !Version.TryParse(v.Trim().TrimStart('v', 'V'), out x)) return null;
            return new Version(x.Major, x.Minor, Math.Max(0, x.Build));
        }

        static void Tls() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        static int Head(string url, out string location)
        {
            var q = (HttpWebRequest)WebRequest.Create(url);
            q.Method = "HEAD"; q.AllowAutoRedirect = false; q.Timeout = 20000; q.UserAgent = Agent;
            q.Headers["Cache-Control"] = "no-cache";
            HttpWebResponse s;
            try { s = (HttpWebResponse)q.GetResponse(); }
            catch (WebException e) { s = e.Response as HttpWebResponse; if (s == null) throw; }
            using (s) { location = s.Headers["Location"]; return (int)s.StatusCode; }
        }

        // No API rate limit: github.com/<repo>/releases/latest answers with a redirect to
        // /releases/tag/<tag>, and release files live at /releases/download/<tag>/<name>.
        // (The REST API allows only 60 unauthenticated requests per hour per IP address.)
        public static ReleaseInfo Check(string repo)
        {
            Tls();
            ReleaseInfo r;
            try
            {
                // GitHub's cache keeps answering with the previous release for a few minutes after a new one;
                // a query string that changes each time skips that cache
                string u = "https://github.com/" + repo + "/releases/latest?t=" + DateTime.UtcNow.Ticks, tag = null;
                for (int i = 0; i < 4 && tag == null; i++)   // a renamed repo adds one redirect first
                {
                    string loc; int code = Head(u, out loc);
                    if (code < 300 || code >= 400 || string.IsNullOrEmpty(loc)) throw new Exception("unexpected answer " + code + " from " + u);
                    u = new Uri(new Uri(u), loc).AbsoluteUri;
                    var m = Regex.Match(u, "/releases/tag/([^/?#]+)$");
                    if (m.Success) tag = Uri.UnescapeDataString(m.Groups[1].Value);
                }
                if (tag == null) throw new Exception("no release tag in the redirect");
                string b = "https://github.com/" + repo + "/releases/download/" + Uri.EscapeDataString(tag), dummy;
                int sc = Head(b + "/SwarlexBattery.exe.sha256", out dummy);
                bool hasSha = sc == 200 || sc == 301 || sc == 302 || sc == 307;
                r = new ReleaseInfo { Tag = tag, Url = hasSha ? b + "/SwarlexBattery.exe" : "", Sha = hasSha ? b + "/SwarlexBattery.exe.sha256" : "", Page = "https://github.com/" + repo + "/releases/tag/" + tag };
            }
            catch (Exception first)
            {
                // fallback: the REST API (rate limited, but independent of the web site's redirects)
                Log.Write("update check (web): " + first.Message);
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = Agent; wc.Headers["Accept"] = "application/vnd.github+json";
                    var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.DownloadString("https://api.github.com/repos/" + repo + "/releases/latest"));
                    Func<string, string> asset = n =>
                    {
                        var list = o.ContainsKey("assets") ? o["assets"] as System.Collections.ArrayList : null;
                        if (list == null) return "";
                        foreach (Dictionary<string, object> a in list) if ((a["name"] as string) == n) return a["browser_download_url"] as string;
                        return "";
                    };
                    r = new ReleaseInfo { Tag = o["tag_name"] as string, Url = asset("SwarlexBattery.exe"), Sha = asset("SwarlexBattery.exe.sha256"), Page = o["html_url"] as string };
                }
            }
            r.Version = Norm(r.Tag);
            return r;
        }

        public static void Download(ReleaseInfo info, string dest, string repo)
        {
            Tls();
            // only files of this repository's releases: https://github.com/<owner>/<repo>/releases/download/...
            foreach (var u in new[] { info.Url, info.Sha })
            {
                var x = new Uri(u);
                if (x.Scheme != "https" || x.Host != "github.com" || !x.AbsolutePath.StartsWith("/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("unexpected download address: " + u);
            }
            string expected;
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = Agent;
                expected = wc.DownloadString(info.Sha).Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                expected = (expected ?? "").ToLowerInvariant();
                if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$")) throw new Exception("invalid SHA-256 file");
                wc.Headers["User-Agent"] = Agent;
                wc.DownloadFile(info.Url, dest);
            }
            var bytes = File.ReadAllBytes(dest);
            if (bytes.Length < 50 * 1024 || bytes[0] != 0x4D || bytes[1] != 0x5A) { File.Delete(dest); throw new Exception("the download is not an exe"); }
            string actual;
            using (var sha = SHA256.Create()) actual = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            if (actual != expected) { File.Delete(dest); throw new Exception("SHA-256 mismatch: the download is damaged or was altered"); }
        }
    }
}
