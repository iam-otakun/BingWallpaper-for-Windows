// BingWallpaper for Windows
// A system-tray app that downloads the Bing daily wallpapers, archives them
// under Pictures\bing-wallpapers and sets the newest one as the desktop
// wallpaper. It is the Windows sibling of BingWallpaper-for-Mac and mirrors
// its architecture: tray-resident, fetch 8 archive entries from Bing's
// HPImageArchive API, download only the images not yet on disk (UHD),
// resolve file names from market metadata (English / bilingual / local),
// keep a small index of what was downloaded, prune old images, and give
// the user explicit feedback on a manual refresh.
//
// Single-file C# (C# 5, .NET Framework 4.8), built with the framework's
// csc.exe — the same recipe as our NetworkSwitch tool. No SDK, no NuGet.
//
// Idea by Fred (Otakun), code by Dash.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("BingWallpaper")]
[assembly: AssemblyProduct("BingWallpaper for Windows")]
[assembly: AssemblyVersion("1.0.6.0")]
[assembly: AssemblyFileVersion("1.0.6.0")]

namespace BingWallpaperWin
{
    // ------------------------------------------------------------------
    // Data model (mirrors the macOS ImageDescriptor + stored file names)
    // ------------------------------------------------------------------

    public class BingImage
    {
        public string url { get; set; }
        public string startdate { get; set; }
        public string enddate { get; set; }
        public string copyright { get; set; }
        public string copyrightlink { get; set; }
        public string title { get; set; }
    }

    public class BingArchive
    {
        public List<BingImage> images { get; set; }
    }

    public class IndexEntry
    {
        public string StartDate { get; set; }      // yyyyMMdd
        public string FileName { get; set; }       // resolved on-disk name
        public string Copyright { get; set; }
        public string CopyrightLink { get; set; }
        public string Title { get; set; }
        public string Key { get; set; }            // OHR image key of the downloaded photo (null in pre-1.0.6 indexes)
    }

    public enum FileNameFormat
    {
        English = 0,
        Bilingual = 1,
        Local = 2
    }

    public enum UpdateOutcome
    {
        Updated,
        UpToDate,
        Failed
    }

    // ------------------------------------------------------------------
    // Settings (HKCU\Software\BingWallpaper) — mirrors macOS Settings
    // ------------------------------------------------------------------

    static class Settings
    {
        private const string KeyPath = @"Software\BingWallpaper";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "BingWallpaper";

        private static int GetInt(string name, int fallback)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null) return fallback;
                object v = key.GetValue(name);
                if (v is int) return (int)v;
                return fallback;
            }
        }

        private static void SetInt(string name, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        private static string GetString(string name, string fallback)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null) return fallback;
                string v = key.GetValue(name) as string;
                return v ?? fallback;
            }
        }

        private static void SetString(string name, string value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue(name, value ?? "", RegistryValueKind.String);
            }
        }

        public static FileNameFormat Format
        {
            get { return (FileNameFormat)GetInt("FileNameFormat", (int)FileNameFormat.Bilingual); }
            set { SetInt("FileNameFormat", (int)value); }
        }

        // 5 / 10 / 50 / 100 days, or 0 = keep forever (macOS KeepImageDuration)
        public static int KeepDays
        {
            get { return GetInt("KeepDays", 50); }
            set { SetInt("KeepDays", value); }
        }

        public static string CurrentStartDate
        {
            get { return GetString("CurrentStartDate", ""); }
            set { SetString("CurrentStartDate", value); }
        }

        public static bool StickEnabled
        {
            get { return GetInt("StickEnabled", 0) != 0; }
            set { SetInt("StickEnabled", value ? 1 : 0); }
        }

        public static DateTime LastUpdate
        {
            get
            {
                string s = GetString("LastUpdate", "");
                DateTime dt;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) return dt;
                return DateTime.MinValue;
            }
            set { SetString("LastUpdate", value.ToString("o", CultureInfo.InvariantCulture)); }
        }

        public static DateTime LastAppCheck
        {
            get
            {
                string s = GetString("LastAppCheck", "");
                DateTime dt;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) return dt;
                return DateTime.MinValue;
            }
            set { SetString("LastAppCheck", value.ToString("o", CultureInfo.InvariantCulture)); }
        }

        public static bool LaunchAtLogin
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValueName) != null;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (value)
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(RunValueName, false);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Core: fetch, name, download, index, wallpaper — mirrors the macOS
    // DownloadManager / FileNameResolver / UpdateManager / FileHandler.
    // ------------------------------------------------------------------

    static class Core
    {
        public static string SaveDir
        {
            get
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                return Path.Combine(pictures, "bing-wallpapers");
            }
        }

        public static string StateDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BingWallpaper");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string IndexPath { get { return Path.Combine(StateDir, "index.json"); } }
        private static string LogPath { get { return Path.Combine(StateDir, "BingWallpaper.log"); } }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine);
            }
            catch { }
        }

        // ---------------- index ----------------

        public static List<IndexEntry> LoadIndex()
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    string json = File.ReadAllText(IndexPath);
                    var ser = new JavaScriptSerializer();
                    List<IndexEntry> list = ser.Deserialize<List<IndexEntry>>(json);
                    if (list != null) return list;
                }
            }
            catch (Exception ex) { Log("LoadIndex failed: " + ex.Message); }
            return new List<IndexEntry>();
        }

        public static void SaveIndex(List<IndexEntry> index)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                File.WriteAllText(IndexPath, ser.Serialize(index));
            }
            catch (Exception ex) { Log("SaveIndex failed: " + ex.Message); }
        }

        public static IndexEntry Find(List<IndexEntry> index, string startDate)
        {
            foreach (IndexEntry e in index)
                if (e.StartDate == startDate) return e;
            return null;
        }

        public static bool IsOnDisk(IndexEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.FileName)) return false;
            return File.Exists(Path.Combine(SaveDir, e.FileName));
        }

        public static List<IndexEntry> OnDiskEntries(List<IndexEntry> index)
        {
            var list = new List<IndexEntry>();
            foreach (IndexEntry e in index)
                if (IsOnDisk(e)) list.Add(e);
            list.Sort(delegate(IndexEntry a, IndexEntry b) { return string.Compare(a.StartDate, b.StartDate, StringComparison.Ordinal); });
            return list;
        }

        // ---------------- HTTP ----------------

        private static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BingWallpaper/1.0");
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        public static async Task<List<BingImage>> FetchEntries(int count, string market)
        {
            string url = "https://www.bing.com/HPImageArchive.aspx?format=js&n="
                + count.ToString(CultureInfo.InvariantCulture) + "&idx=0";
            if (!string.IsNullOrEmpty(market)) url += "&mkt=" + market;
            using (HttpClient client = MakeClient())
            {
                string json = await client.GetStringAsync(url).ConfigureAwait(false);
                var ser = new JavaScriptSerializer();
                BingArchive archive = ser.Deserialize<BingArchive>(json);
                if (archive == null || archive.images == null) return new List<BingImage>();
                return archive.images;
            }
        }

        public static async Task<byte[]> DownloadBinary(string url)
        {
            using (HttpClient client = MakeClient())
            {
                return await client.GetByteArrayAsync(url).ConfigureAwait(false);
            }
        }

        public static string ImageUrl(BingImage entry)
        {
            return "https://www.bing.com" + entry.url.Replace("1920x1080", "UHD");
        }

        // ---------------- file-name resolution (port of FileNameResolver) ----------------

        public static string ImageKey(string url)
        {
            if (url == null) return null;
            int i = url.IndexOf("OHR.", StringComparison.Ordinal);
            if (i < 0) return null;
            string rest = url.Substring(i + 4);
            int u = rest.IndexOf('_');
            string key = u >= 0 ? rest.Substring(0, u) : rest;
            return key.Length == 0 ? null : key;
        }

        public static string Keywords(string copyright)
        {
            if (string.IsNullOrEmpty(copyright)) return "";
            char[] disallowed = new char[] { '(', ')', '\\', '/', ':', '*', '?', '"', '<', '>' };
            string[] parts = copyright.Split(disallowed);
            string head = "";
            foreach (string p in parts)
            {
                if (p.Length > 0) { head = p; break; }
            }
            string result = head.Replace('，', '_').Replace(',', '_').Replace(' ', '_');
            while (result.Contains("__")) result = result.Replace("__", "_");
            return result.Trim('_');
        }

        private static string DashedDate(string startdate)
        {
            if (startdate == null || startdate.Length != 8) return startdate;
            return startdate.Substring(0, 4) + "-" + startdate.Substring(4, 2) + "-" + startdate.Substring(6, 2);
        }

        private static string BuildName(string startdate, string keywords)
        {
            string name = "Bing_" + DashedDate(startdate);
            if (!string.IsNullOrEmpty(keywords)) name += "_" + keywords;
            return name + ".jpg";
        }

        public static string LocalMarket()
        {
            string name = CultureInfo.CurrentCulture.Name; // e.g. zh-CN, en-US
            string[] known = new string[] {
                "zh-CN","zh-TW","zh-HK","en-US","en-GB","en-AU","en-CA","en-IN","en-NZ",
                "ja-JP","de-DE","fr-FR","fr-CA","es-ES","es-MX","it-IT","pt-BR","pt-PT",
                "ko-KR","ru-RU","nl-NL","pl-PL","sv-SE","da-DK","nb-NO","fi-FI","tr-TR" };
            foreach (string k in known)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return k;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            return "en-US";
        }

        private static BingImage Match(List<BingImage> entries, string key)
        {
            foreach (BingImage e in entries)
            {
                if (ImageKey(e.url) == key) return e;
                if (ImageKey("https://www.bing.com" + e.url) == key) return e;
            }
            return null;
        }

        // Last-resort name from the primary feed's own metadata for this
        // image: a real title in the exit market's language still beats
        // a bare date, and Update() heals it to the format's proper
        // name on a later run once the market feeds cooperate.
        private static string OwnName(BingImage entry)
        {
            return BuildName(entry.startdate, Keywords(entry.copyright));
        }

        // Resolve the display file name for an entry, following the user's
        // naming format. Falls back to <startdate>.jpg whenever the market
        // metadata cannot be fetched or matched, so naming never fails a
        // download (same contract as the macOS resolver).
        public static async Task<string> ResolveFileName(BingImage entry, FileNameFormat format)
        {
            string fallback = entry.startdate + ".jpg";
            string key = ImageKey(entry.url);
            if (key == null) return OwnName(entry);
            try
            {
                // The name's date is always the entry's own date (the one
                // the index knows it by); only the keywords come from the
                // matched market entry — markets label the same photo
                // with different dates around their local midnights.
                if (format == FileNameFormat.English)
                {
                    List<BingImage> en = await FetchEntries(8, "en-US").ConfigureAwait(false);
                    BingImage m = Match(en, key);
                    if (m == null) return OwnName(entry);
                    return BuildName(entry.startdate, Keywords(m.copyright));
                }
                else if (format == FileNameFormat.Bilingual)
                {
                    Task<List<BingImage>> tEn = FetchEntries(8, "en-US");
                    Task<List<BingImage>> tZh = FetchEntries(9, "zh-CN");
                    List<BingImage> en = await tEn.ConfigureAwait(false);
                    List<BingImage> zh = await tZh.ConfigureAwait(false);
                    // The two markets are matched independently: each one
                    // runs its own editorial selection, so a photo the
                    // exit market shows may exist in only one of them
                    // (e.g. the 2026-11-23 Cairngorms squirrel ran in the
                    // zh-CN and ja-JP selections but never in en-US).
                    // Requiring the en-US match first sent exactly those
                    // photos to the bare-date fallback.
                    BingImage enEntry = Match(en, key);
                    BingImage zhEntry = Match(zh, key);
                    if (enEntry == null && zhEntry == null) return OwnName(entry);
                    string kwEn = enEntry != null ? Keywords(enEntry.copyright) : "";
                    string kwZh = zhEntry != null ? Keywords(zhEntry.copyright) : "";
                    // Bing steers by exit IP: from a CN exit the en-US
                    // request is answered with zh-CN metadata anyway
                    // (verified on a CN-exit host: identical OHR id and
                    // copyright). Identical keywords mean no English
                    // metadata was really delivered — drop the segment
                    // instead of duplicating the Chinese one.
                    if (kwEn == kwZh) kwEn = "";
                    string name = "Bing_" + DashedDate(entry.startdate);
                    if (!string.IsNullOrEmpty(kwZh)) name += "_" + kwZh;
                    if (!string.IsNullOrEmpty(kwEn)) name += string.IsNullOrEmpty(kwZh) ? "_" + kwEn : "-" + kwEn;
                    return name + ".jpg";
                }
                else
                {
                    List<BingImage> local = await FetchEntries(8, LocalMarket()).ConfigureAwait(false);
                    BingImage m = Match(local, key);
                    if (m == null) return OwnName(entry);
                    return BuildName(entry.startdate, Keywords(m.copyright));
                }
            }
            catch (Exception ex)
            {
                Log("ResolveFileName failed for " + entry.startdate + ": " + ex.Message);
                return OwnName(entry);
            }
        }

        // ---------------- update flow (port of UpdateManager.update) ----------------

        public static async Task<UpdateOutcome> Update()
        {
            List<BingImage> entries;
            try
            {
                entries = await FetchEntries(8, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Fetch entries failed: " + ex.Message);
                return UpdateOutcome.Failed;
            }
            if (entries.Count == 0) return UpdateOutcome.Failed;

            if (!Directory.Exists(SaveDir)) Directory.CreateDirectory(SaveDir);
            List<IndexEntry> index = LoadIndex();

            // Merge fresh metadata into the index (the descriptor store).
            foreach (BingImage e in entries)
            {
                IndexEntry existing = Find(index, e.startdate);
                if (existing == null)
                {
                    var created = new IndexEntry();
                    created.StartDate = e.startdate;
                    created.FileName = null;
                    created.Key = ImageKey(e.url);
                    created.Copyright = e.copyright;
                    created.CopyrightLink = e.copyrightlink;
                    created.Title = e.title;
                    index.Add(created);
                }
                else
                {
                    existing.Copyright = e.copyright;
                    existing.CopyrightLink = e.copyrightlink;
                    existing.Title = e.title;
                    // Backfill the image key for entries written before
                    // 1.0.6. The primary feed is the same market these
                    // photos were downloaded from, so its same-date entry
                    // is the same photo (the trust this loop already
                    // places in the copyright refresh above).
                    if (string.IsNullOrEmpty(existing.Key)) existing.Key = ImageKey(e.url);
                }
            }

            var newEntries = new List<BingImage>();
            foreach (BingImage e in entries)
            {
                IndexEntry ie = Find(index, e.startdate);
                if (!IsOnDisk(ie)) newEntries.Add(e);
            }

            int successes = 0;
            foreach (BingImage e in newEntries)
            {
                try
                {
                    IndexEntry ie = Find(index, e.startdate);
                    if (string.IsNullOrEmpty(ie.FileName))
                    {
                        ie.FileName = await ResolveFileName(e, Settings.Format).ConfigureAwait(false);
                        SaveIndex(index); // remember the name first, like macOS
                    }
                    byte[] data = await DownloadBinary(ImageUrl(e)).ConfigureAwait(false);
                    string path = Path.Combine(SaveDir, ie.FileName);
                    if (!File.Exists(path))
                    {
                        // Write via a temp file so a partial download never
                        // poses as a finished wallpaper.
                        string tmp = path + ".part";
                        File.WriteAllBytes(tmp, data);
                        File.Move(tmp, path);
                    }
                    successes++;
                }
                catch (Exception ex)
                {
                    Log("Download failed for " + e.startdate + ": " + ex.Message);
                }
            }

            // Heal names that fell back to a bare <startdate>.jpg at
            // download time (the market feeds were unreachable or had
            // not caught up then): re-resolve them now and rename the
            // files, so a first-run fallback does not stay bare forever.
            foreach (BingImage e in entries)
            {
                IndexEntry ie = Find(index, e.startdate);
                if (ie == null) continue;
                if (ie.FileName != ie.StartDate + ".jpg") continue;
                if (!IsOnDisk(ie)) continue;
                string healed = await ResolveFileName(e, Settings.Format).ConfigureAwait(false);
                if (string.IsNullOrEmpty(healed) || healed == ie.FileName) continue;
                string oldPath = Path.Combine(SaveDir, ie.FileName);
                string newPath = Path.Combine(SaveDir, healed);
                try
                {
                    if (File.Exists(newPath)) continue;
                    File.Move(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    Log("Name heal rename failed for " + ie.StartDate + ": " + ex.Message);
                    continue;
                }
                ie.FileName = healed;
                Log("Name healed for " + ie.StartDate + ": " + healed);
                if (ie.StartDate == Settings.CurrentStartDate) SetWallpaper(newPath);
            }

            SaveIndex(index);
            Settings.LastUpdate = DateTime.Now;
            Cleanup(index);
            SaveIndex(index);

            if (newEntries.Count == 0) return UpdateOutcome.UpToDate;
            if (successes == 0) return UpdateOutcome.Failed;

            List<IndexEntry> onDisk = OnDiskEntries(index);
            if (onDisk.Count > 0 && !Settings.StickEnabled)
            {
                // Stick mode: new photos are still downloaded and archived,
                // but the wallpaper stays on the pinned picture.
                IndexEntry newest = onDisk[onDisk.Count - 1];
                SetWallpaper(Path.Combine(SaveDir, newest.FileName));
                Settings.CurrentStartDate = newest.StartDate;
            }
            return UpdateOutcome.Updated;
        }

        // After a file-name-format change: re-resolve the names of the
        // on-disk archive entries the market feeds can still describe,
        // rename the files to the new format, and point each entry's
        // copyright at the new format's display language so the menu
        // title follows the format. Entries older than the feed window
        // keep their download-time names, exactly as before. The image
        // bytes never change — a rename is the whole operation.
        public static async Task<int> ReformatArchive(FileNameFormat format)
        {
            List<BingImage> primary;
            try
            {
                if (format == FileNameFormat.English)
                    primary = await FetchEntries(8, "en-US").ConfigureAwait(false);
                else if (format == FileNameFormat.Bilingual)
                    primary = await FetchEntries(9, "zh-CN").ConfigureAwait(false);
                else
                    primary = await FetchEntries(8, LocalMarket()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("ReformatArchive fetch failed: " + ex.Message);
                return 0;
            }
            if (primary == null || primary.Count == 0) return 0;

            List<IndexEntry> index = LoadIndex();
            int renamed = 0;
            foreach (IndexEntry ie in index)
            {
                if (!IsOnDisk(ie)) continue;
                // Match the format market's entry to this index entry.
                // When the entry knows its photo's image key, match by
                // key and skip when the market does not carry the photo:
                // each market runs its own selection, so a same-date
                // entry there can be a DIFFERENT photo, and borrowing
                // its name would mislabel this file. Keyless entries
                // (written before 1.0.6) keep the historical date match.
                BingImage fresh = null;
                if (!string.IsNullOrEmpty(ie.Key))
                {
                    foreach (BingImage e in primary)
                        if (ImageKey(e.url) == ie.Key) { fresh = e; break; }
                    if (fresh == null) continue;
                }
                else
                {
                    foreach (BingImage e in primary)
                        if (e.startdate == ie.StartDate) { fresh = e; break; }
                    if (fresh == null) continue;
                }

                // Resolve under the index entry's own date: the fresh
                // entry supplies the photo's key and metadata, but the
                // name must carry the date this archive knows it by.
                var named = new BingImage();
                named.startdate = ie.StartDate;
                named.url = fresh.url;
                named.copyright = fresh.copyright;
                string newName = await ResolveFileName(named, format).ConfigureAwait(false);
                if (string.IsNullOrEmpty(newName) || newName == ie.FileName)
                {
                    // Name already fits the format; still refresh the
                    // display copyright so the title speaks the format's
                    // language.
                    ie.Copyright = fresh.copyright;
                    ie.CopyrightLink = fresh.copyrightlink;
                    ie.Title = fresh.title;
                    continue;
                }
                if (newName == ie.StartDate + ".jpg") continue; // resolver fallback: keep the good name
                string oldPath = Path.Combine(SaveDir, ie.FileName);
                string newPath = Path.Combine(SaveDir, newName);
                try
                {
                    if (File.Exists(newPath)) continue;
                    File.Move(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    Log("ReformatArchive rename failed for " + ie.StartDate + ": " + ex.Message);
                    continue;
                }
                ie.FileName = newName;
                ie.Copyright = fresh.copyright;
                ie.CopyrightLink = fresh.copyrightlink;
                ie.Title = fresh.title;
                renamed++;
            }
            SaveIndex(index);
            return renamed;
        }

        private static void Cleanup(List<IndexEntry> index)
        {
            int keep = Settings.KeepDays;
            if (keep <= 0) return; // keep forever
            string threshold = DateTime.Today.AddDays(-keep).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            for (int i = index.Count - 1; i >= 0; i--)
            {
                IndexEntry e = index[i];
                bool pinned = Settings.StickEnabled && e.StartDate == Settings.CurrentStartDate;
                if (string.Compare(e.StartDate, threshold, StringComparison.Ordinal) <= 0 && !pinned)
                {
                    try
                    {
                        if (IsOnDisk(e)) File.Delete(Path.Combine(SaveDir, e.FileName));
                    }
                    catch (Exception ex) { Log("Cleanup delete failed: " + ex.Message); }
                    index.RemoveAt(i);
                }
            }
        }

        // ---------------- wallpaper ----------------

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        public static bool SetWallpaper(string path)
        {
            // Preferred: IDesktopWallpaper, per monitor (Windows 8+).
            try
            {
                var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
                uint count = 0;
                dw.GetMonitorDevicePathCount(out count);
                if (count == 0)
                {
                    dw.SetWallpaper(null, path);
                }
                else
                {
                    for (uint i = 0; i < count; i++)
                    {
                        string monitorId = null;
                        dw.GetMonitorDevicePathAt(i, out monitorId);
                        dw.SetWallpaper(monitorId, path);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("IDesktopWallpaper failed, falling back: " + ex.Message);
            }
            try
            {
                const int SPI_SETDESKWALLPAPER = 20;
                const int SPIF_UPDATEINIFILE = 0x01;
                const int SPIF_SENDWININICHANGE = 0x02;
                return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
            }
            catch (Exception ex)
            {
                Log("SystemParametersInfo failed: " + ex.Message);
                return false;
            }
        }
    }

    // IDesktopWallpaper (partial declaration: only the leading methods, in
    // vtable order, which are the ones this app calls).
    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    class DesktopWallpaperClass { }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                                       [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                                       [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
        [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex,
                                                 [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
    }

    public class ReleaseInfo
    {
        public string Tag;
        public Version Version;
        public string ZipUrl;
    }

    // ------------------------------------------------------------------
    // Self-update from GitHub Releases: check the latest release of
    // iam-otakun/BingWallpaper-for-Windows, download its zip, then swap
    // the running exe (rename it to .old, copy the new one in, relaunch
    // with --updated so the new instance waits for the old one's mutex).
    // Any failure before the swap leaves the install untouched.
    // ------------------------------------------------------------------

    public static class AppUpdate
    {
        private const string LatestUrl = "https://api.github.com/repos/iam-otakun/BingWallpaper-for-Windows/releases/latest";

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static async Task<ReleaseInfo> FetchLatest()
        {
            try
            {
                string json;
                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", "BingWallpaper-Windows/" + CurrentVersion.ToString());
                    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    HttpResponseMessage resp = await client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Core.Log("Update check failed: HTTP " + (int)resp.StatusCode);
                        return null;
                    }
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(json);
                object tagObj;
                if (!root.TryGetValue("tag_name", out tagObj)) return null;
                string tag = tagObj as string;
                if (string.IsNullOrEmpty(tag)) return null;
                Version ver;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out ver)) return null;
                string zip = null;
                object assetsObj;
                if (root.TryGetValue("assets", out assetsObj))
                {
                    var assets = assetsObj as System.Collections.ArrayList;
                    if (assets != null)
                    {
                        foreach (object a in assets)
                        {
                            var d = a as Dictionary<string, object>;
                            if (d == null) continue;
                            object nameObj, urlObj;
                            string name = d.TryGetValue("name", out nameObj) ? nameObj as string : null;
                            if (name != null
                                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                && d.TryGetValue("browser_download_url", out urlObj))
                            {
                                zip = urlObj as string;
                                break;
                            }
                        }
                    }
                }
                if (zip == null) return null;
                var info = new ReleaseInfo();
                info.Tag = tag;
                info.Version = ver;
                info.ZipUrl = zip;
                return info;
            }
            catch (Exception ex)
            {
                Core.Log("Update check failed: " + ex.Message);
                return null;
            }
        }

        // Download the release zip and extract the new exe into the
        // state dir; returns the staged exe path, or null on failure.
        public static async Task<string> Stage(ReleaseInfo rel)
        {
            try
            {
                string dir = Path.Combine(Core.StateDir, "update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                byte[] zipBytes = await Core.DownloadBinary(rel.ZipUrl).ConfigureAwait(false);
                string zipPath = Path.Combine(dir, "pkg.zip");
                File.WriteAllBytes(zipPath, zipBytes);
                string extract = Path.Combine(dir, "extract");
                ZipFile.ExtractToDirectory(zipPath, extract);
                string exe = FindExe(extract);
                if (exe == null)
                {
                    Core.Log("Update package has no BingWallpaper.exe");
                    return null;
                }
                var fi = new FileInfo(exe);
                if (fi.Length < 50000)
                {
                    Core.Log("Staged exe looks too small: " + fi.Length);
                    return null;
                }
                return exe;
            }
            catch (Exception ex)
            {
                Core.Log("Update staging failed: " + ex.Message);
                return null;
            }
        }

        private static string FindExe(string dir)
        {
            string direct = Path.Combine(dir, "BingWallpaper.exe");
            if (File.Exists(direct)) return direct;
            foreach (string f in Directory.GetFiles(dir, "BingWallpaper.exe", SearchOption.AllDirectories))
                return f;
            return null;
        }

        // A running exe can be renamed but not overwritten: move the
        // current one to .old, copy the staged one into its place and
        // relaunch. If the copy fails, the .old is moved back first.
        public static bool SwapAndRelaunch(string stagedExe)
        {
            string exePath = Application.ExecutablePath;
            string oldPath = exePath + ".old";
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(exePath, oldPath);
                try
                {
                    File.Copy(stagedExe, exePath);
                }
                catch
                {
                    File.Move(oldPath, exePath);
                    throw;
                }
                System.Diagnostics.Process.Start(exePath, "--updated");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("Update swap failed: " + ex.Message);
                return false;
            }
        }

        // Remove the previous exe and any staged leftovers at startup.
        public static void CleanupOld()
        {
            try
            {
                string oldPath = Application.ExecutablePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                string dir = Path.Combine(Core.StateDir, "update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Tray application — mirrors MenuController: preview title, Open
    // Photo Folder, Refresh Images (with feedback), previous/next day,
    // Settings, Quit. A timer checks once in a while whether today's
    // image is still missing (the UpdateScheduleManager role).
    // ------------------------------------------------------------------

    class TrayApp : ApplicationContext
    {
        private NotifyIcon tray;
        private ToolStripMenuItem titleItem;
        private ToolStripMenuItem refreshItem;
        private ToolStripMenuItem stickItem;
        private ToolStripMenuItem updateItem;
        private ReleaseInfo pendingUpdate;
        private bool checkingUpdate;
        private System.Windows.Forms.Timer timer;
        private bool updating;

        public TrayApp()
        {
            tray = new NotifyIcon();
            tray.Text = "BingWallpaper";
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenPhotoFolder(); };
            tray.BalloonTipClicked += delegate
            {
                if (pendingUpdate != null)
                {
                    if (MessageBox.Show("Download and install BingWallpaper " + pendingUpdate.Tag + " now? The app will restart.", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        var ignored = InstallUpdate();
                    }
                }
            };

            var menu = new ContextMenuStrip();
            titleItem = new ToolStripMenuItem("(no image yet)");
            titleItem.Click += delegate { OpenCopyrightLink(); };
            menu.Items.Add(titleItem);

            stickItem = new ToolStripMenuItem("Stick With This Picture");
            stickItem.Checked = Settings.StickEnabled;
            stickItem.Click += delegate { ToggleStick(); };
            menu.Items.Add(stickItem);

            menu.Items.Add(new ToolStripSeparator());

            var openItem = new ToolStripMenuItem("Open Photo Folder");
            openItem.Click += delegate { OpenPhotoFolder(); };
            menu.Items.Add(openItem);

            refreshItem = new ToolStripMenuItem("Refresh Images");
            refreshItem.Click += async delegate { await RefreshWithFeedback(); };
            menu.Items.Add(refreshItem);

            menu.Items.Add(new ToolStripSeparator());

            var prevItem = new ToolStripMenuItem("Previous Day");
            prevItem.Click += delegate { StepDay(-1); };
            menu.Items.Add(prevItem);
            var nextItem = new ToolStripMenuItem("Next Day");
            nextItem.Click += delegate { StepDay(1); };
            menu.Items.Add(nextItem);

            var todayItem = new ToolStripMenuItem("Today");
            todayItem.Click += delegate { JumpToToday(); };
            menu.Items.Add(todayItem);

            menu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem("Settings");
            settingsItem.Click += delegate { ShowSettings(); };
            menu.Items.Add(settingsItem);

            updateItem = new ToolStripMenuItem("Check for Updates…");
            updateItem.Click += async delegate { await CheckForUpdates(true); };
            menu.Items.Add(updateItem);

            var quitItem = new ToolStripMenuItem("Quit");
            quitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(quitItem);

            tray.ContextMenuStrip = menu;

            RefreshTitle();
            EnsureWallpaperCurrent();

            // Startup: fetch if today's image is not here yet.
            if (Settings.LastUpdate.Date < DateTime.Today)
            {
                var ignored = SilentUpdate();
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 30 * 60 * 1000;
            timer.Tick += delegate
            {
                if (Settings.LastUpdate.Date < DateTime.Today)
                {
                    var ignored = SilentUpdate();
                }
            };
            timer.Start();

            // Self-update: clear the previous exe left by a swap, then
            // quietly check GitHub at most once a day; a balloon offers
            // the update when one exists.
            AppUpdate.CleanupOld();
            if (DateTime.Now - Settings.LastAppCheck > TimeSpan.FromHours(20))
            {
                var ignoredCheck = CheckForUpdates(false);
            }
        }

        protected override void ExitThreadCore()
        {
            if (timer != null) timer.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.ExitThreadCore();
        }

        private IndexEntry CurrentEntry()
        {
            List<IndexEntry> index = Core.LoadIndex();
            List<IndexEntry> onDisk = Core.OnDiskEntries(index);
            if (onDisk.Count == 0) return null;
            string cur = Settings.CurrentStartDate;
            IndexEntry found = Core.Find(onDisk, cur);
            return found ?? onDisk[onDisk.Count - 1];
        }

        private void RefreshTitle()
        {
            IndexEntry cur = CurrentEntry();
            if (cur == null)
            {
                titleItem.Text = "(no image yet)";
                return;
            }
            string head = Core.Keywords(cur.Copyright ?? "");
            string text = string.IsNullOrEmpty(head) ? (cur.Title ?? cur.StartDate) : head.Replace('_', ' ');
            titleItem.Text = text;
            string trayText = "BingWallpaper - " + text;
            if (trayText.Length >= 64) trayText = trayText.Substring(0, 63);
            tray.Text = trayText;
        }

        private void EnsureWallpaperCurrent()
        {
            IndexEntry cur = CurrentEntry();
            if (cur != null)
            {
                Core.SetWallpaper(Path.Combine(Core.SaveDir, cur.FileName));
                Settings.CurrentStartDate = cur.StartDate;
            }
        }

        private void OpenPhotoFolder()
        {
            try
            {
                if (!Directory.Exists(Core.SaveDir)) Directory.CreateDirectory(Core.SaveDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Core.SaveDir + "\"");
            }
            catch (Exception ex) { Core.Log("OpenPhotoFolder failed: " + ex.Message); }
        }

        private void OpenCopyrightLink()
        {
            IndexEntry cur = CurrentEntry();
            if (cur != null && !string.IsNullOrEmpty(cur.CopyrightLink))
            {
                try { System.Diagnostics.Process.Start(cur.CopyrightLink); }
                catch (Exception ex) { Core.Log("OpenCopyrightLink failed: " + ex.Message); }
            }
        }

        // Browsing away from the pinned picture releases the pin: a
        // Previous / Next / Today click means "show me another day",
        // so Stick turns itself off and the wallpaper and the title
        // follow the click. (The incoherent state — state moved, pin
        // still on, wallpaper unmoved — is what must never happen.)
        private void ReleaseStickForBrowsing()
        {
            if (Settings.StickEnabled)
            {
                Settings.StickEnabled = false;
                stickItem.Checked = false;
            }
        }

        private void StepDay(int delta)
        {
            ReleaseStickForBrowsing();
            List<IndexEntry> index = Core.LoadIndex();
            List<IndexEntry> onDisk = Core.OnDiskEntries(index);
            if (onDisk.Count == 0) return;
            int pos = onDisk.Count - 1;
            string cur = Settings.CurrentStartDate;
            for (int i = 0; i < onDisk.Count; i++)
                if (onDisk[i].StartDate == cur) { pos = i; break; }
            int next = pos + delta;
            if (next < 0 || next >= onDisk.Count) return;
            IndexEntry target = onDisk[next];
            Core.SetWallpaper(Path.Combine(Core.SaveDir, target.FileName));
            Settings.CurrentStartDate = target.StartDate;
            RefreshTitle();
        }

        private void JumpToToday()
        {
            ReleaseStickForBrowsing();
            List<IndexEntry> index = Core.LoadIndex();
            List<IndexEntry> onDisk = Core.OnDiskEntries(index);
            if (onDisk.Count == 0) return;
            IndexEntry newest = onDisk[onDisk.Count - 1];
            Core.SetWallpaper(Path.Combine(Core.SaveDir, newest.FileName));
            Settings.CurrentStartDate = newest.StartDate;
            RefreshTitle();
        }

        private void ToggleStick()
        {
            bool on = !Settings.StickEnabled;
            Settings.StickEnabled = on;
            stickItem.Checked = on;
            if (!on)
            {
                // Stick released: today's picture goes up right away.
                JumpToToday();
            }
        }

        private async Task SilentUpdate()
        {
            if (updating) return;
            updating = true;
            try
            {
                UpdateOutcome outcome = await Core.Update();
                if (outcome == UpdateOutcome.Updated)
                {
                    RefreshTitle();
                    tray.ShowBalloonTip(5000, "BingWallpaper",
                        Settings.StickEnabled ? "New photo downloaded." : "New wallpaper set.",
                        ToolTipIcon.Info);
                }
            }
            catch (Exception ex) { Core.Log("SilentUpdate failed: " + ex.Message); }
            finally { updating = false; }
        }

        private async Task RefreshWithFeedback()
        {
            if (updating) return;
            updating = true;
            refreshItem.Enabled = false;
            try
            {
                UpdateOutcome outcome = await Core.Update();
                RefreshTitle();
                string message;
                if (outcome == UpdateOutcome.Updated) message = "Update successfully!";
                else if (outcome == UpdateOutcome.UpToDate) message = "Up to date!";
                else message = "Update failed!";
                MessageBox.Show(message, "BingWallpaper",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Core.Log("Refresh failed: " + ex.Message);
                MessageBox.Show("Update failed!", "BingWallpaper",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                updating = false;
                refreshItem.Enabled = true;
            }
        }

        private async Task CheckForUpdates(bool interactive)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            try
            {
                ReleaseInfo rel = await AppUpdate.FetchLatest();
                if (rel == null)
                {
                    if (interactive)
                        MessageBox.Show("Could not check for updates. Please try again later.", "BingWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Settings.LastAppCheck = DateTime.Now;
                Version cur = AppUpdate.CurrentVersion;
                if (rel.Version > cur)
                {
                    pendingUpdate = rel;
                    updateItem.Text = "Update to " + rel.Tag + "…";
                    if (interactive)
                    {
                        if (MessageBox.Show("BingWallpaper " + rel.Tag + " is available (you have v" + cur.ToString(3) + ").\n\nDownload and install it now? The app will restart.", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            await InstallUpdate();
                        }
                    }
                    else
                    {
                        tray.BalloonTipTitle = "BingWallpaper update";
                        tray.BalloonTipText = "Version " + rel.Tag + " is available. Click to update.";
                        tray.BalloonTipIcon = ToolTipIcon.Info;
                        tray.ShowBalloonTip(8000);
                    }
                }
                else if (interactive)
                {
                    MessageBox.Show("BingWallpaper is up to date (v" + cur.ToString(3) + ").", "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { Core.Log("CheckForUpdates failed: " + ex.Message); }
            finally { checkingUpdate = false; }
        }

        private async Task InstallUpdate()
        {
            if (pendingUpdate == null) return;
            updateItem.Enabled = false;
            try
            {
                tray.BalloonTipTitle = "BingWallpaper";
                tray.BalloonTipText = "Downloading update " + pendingUpdate.Tag + "…";
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(4000);
                string staged = await AppUpdate.Stage(pendingUpdate);
                if (staged == null)
                {
                    MessageBox.Show("The update could not be downloaded. Please try again later, or download it from GitHub.", "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (AppUpdate.SwapAndRelaunch(staged))
                {
                    ExitThread();
                }
                else
                {
                    MessageBox.Show("The update could not be installed. Your current version is untouched.", "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { Core.Log("InstallUpdate failed: " + ex.Message); }
            finally { updateItem.Enabled = true; }
        }

        private void ShowSettings()
        {
            DialogResult result;
            using (var form = new SettingsForm())
            {
                result = form.ShowDialog();
            }
            // Apply the chosen format on every OK, not only when the
            // value changed: re-applying is what heals archive names
            // that never matched the format (a first-run fallback name,
            // a photo renamed under another market's selection), and
            // the user pressing OK with a format selected means
            // "make the names follow this format".
            if (result == DialogResult.OK)
            {
                var ignored = ReformatAfterSettingsChange();
            }
        }

        private async Task ReformatAfterSettingsChange()
        {
            if (updating) return;
            updating = true;
            try
            {
                await Core.ReformatArchive(Settings.Format);
                // Re-apply the current picture from its (possibly renamed)
                // file so the wallpaper and the menu title follow the new
                // format immediately. Stick and browsing state are kept:
                // the same picture stays up, only its name changes.
                EnsureWallpaperCurrent();
                RefreshTitle();
            }
            catch (Exception ex) { Core.Log("Reformat after settings change failed: " + ex.Message); }
            finally { updating = false; }
        }
    }

    // ------------------------------------------------------------------
    // Settings form — naming format, retention, launch at login
    // ------------------------------------------------------------------

    class SettingsForm : Form
    {
        private RadioButton rbEnglish;
        private RadioButton rbBilingual;
        private RadioButton rbLocal;
        private ComboBox cbKeep;
        private CheckBox ckLogin;
        private Label lblFolder;

        public SettingsForm()
        {
            Text = "BingWallpaper Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 430;
            Height = 320;

            var lblFormat = new Label();
            lblFormat.Text = "File name format:";
            lblFormat.Left = 16; lblFormat.Top = 16; lblFormat.Width = 380;
            Controls.Add(lblFormat);

            rbEnglish = new RadioButton();
            rbEnglish.Text = "English name — Bing_2026-11-24_Fiddlehead_fern.jpg";
            rbEnglish.Left = 32; rbEnglish.Top = 42; rbEnglish.Width = 380;
            Controls.Add(rbEnglish);

            rbBilingual = new RadioButton();
            rbBilingual.Text = "Bilingual — Bing_2026-11-24_蕨菜_Fiddlehead_fern.jpg";
            rbBilingual.Left = 32; rbBilingual.Top = 66; rbBilingual.Width = 380;
            Controls.Add(rbBilingual);

            rbLocal = new RadioButton();
            rbLocal.Text = "Local language (system)";
            rbLocal.Left = 32; rbLocal.Top = 90; rbLocal.Width = 380;
            Controls.Add(rbLocal);

            var lblKeep = new Label();
            lblKeep.Text = "Keep images for:";
            lblKeep.Left = 16; lblKeep.Top = 126; lblKeep.Width = 140;
            Controls.Add(lblKeep);

            cbKeep = new ComboBox();
            cbKeep.DropDownStyle = ComboBoxStyle.DropDownList;
            cbKeep.Left = 160; cbKeep.Top = 122; cbKeep.Width = 120;
            cbKeep.Items.Add("5 days");
            cbKeep.Items.Add("10 days");
            cbKeep.Items.Add("50 days");
            cbKeep.Items.Add("100 days");
            cbKeep.Items.Add("Forever");
            Controls.Add(cbKeep);

            ckLogin = new CheckBox();
            ckLogin.Text = "Launch at login";
            ckLogin.Left = 16; ckLogin.Top = 160; ckLogin.Width = 380;
            Controls.Add(ckLogin);

            lblFolder = new Label();
            lblFolder.Text = "Photos: " + Core.SaveDir;
            lblFolder.Left = 16; lblFolder.Top = 192; lblFolder.Width = 380;
            Controls.Add(lblFolder);

            var btnFolder = new Button();
            btnFolder.Text = "Open Photo Folder";
            btnFolder.Left = 16; btnFolder.Top = 218; btnFolder.Width = 140;
            btnFolder.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(Core.SaveDir)) Directory.CreateDirectory(Core.SaveDir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Core.SaveDir + "\"");
                }
                catch { }
            };
            Controls.Add(btnFolder);

            var btnOk = new Button();
            btnOk.Text = "OK";
            btnOk.Left = 232; btnOk.Top = 218; btnOk.Width = 80;
            btnOk.Click += delegate { SaveAndClose(); };
            Controls.Add(btnOk);

            var btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Left = 324; btnCancel.Top = 218; btnCancel.Width = 80;
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            LoadValues();
        }

        private void LoadValues()
        {
            FileNameFormat f = Settings.Format;
            rbEnglish.Checked = f == FileNameFormat.English;
            rbBilingual.Checked = f == FileNameFormat.Bilingual;
            rbLocal.Checked = f == FileNameFormat.Local;
            int keep = Settings.KeepDays;
            cbKeep.SelectedIndex = keep == 5 ? 0 : keep == 10 ? 1 : keep == 100 ? 3 : keep == 0 ? 4 : 2;
            ckLogin.Checked = Settings.LaunchAtLogin;
        }

        private void SaveAndClose()
        {
            Settings.Format = rbEnglish.Checked ? FileNameFormat.English
                : rbBilingual.Checked ? FileNameFormat.Bilingual
                : FileNameFormat.Local;
            int[] days = new int[] { 5, 10, 50, 100, 0 };
            Settings.KeepDays = days[Math.Max(0, cbKeep.SelectedIndex)];
            Settings.LaunchAtLogin = ckLogin.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool selftest = false;
            bool updated = false;
            foreach (string a in args)
            {
                if (a == "--selftest") selftest = true;
                if (a == "--updated") updated = true;
            }

            if (selftest) return RunSelfTest();

            // After a self-update swap the previous instance may still
            // be exiting: wait for its mutex instead of giving up.
            bool createdNew = false;
            Mutex mutex = null;
            int attempts = updated ? 50 : 1;
            for (int i = 0; i < attempts && !createdNew; i++)
            {
                if (i > 0) Thread.Sleep(200);
                mutex = new Mutex(true, "BingWallpaperForWindows", out createdNew);
                if (!createdNew) { mutex.Dispose(); mutex = null; }
            }
            if (!createdNew) return 0; // already running

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
            return 0;
        }

        // Headless verification: one update cycle, results written to
        // selftest.txt in the state dir; exit code 0 unless it failed.
        private static int RunSelfTest()
        {
            var lines = new List<string>();
            int code = 0;
            try
            {
                lines.Add("SaveDir=" + Core.SaveDir);
                UpdateOutcome outcome = Core.Update().GetAwaiter().GetResult();
                lines.Add("Outcome=" + outcome.ToString());
                List<IndexEntry> index = Core.LoadIndex();
                List<IndexEntry> onDisk = Core.OnDiskEntries(index);
                lines.Add("OnDisk=" + onDisk.Count.ToString(CultureInfo.InvariantCulture));
                foreach (IndexEntry e in onDisk)
                    lines.Add("  " + e.StartDate + " -> " + e.FileName);
                lines.Add("CurrentStartDate=" + Settings.CurrentStartDate);
                if (outcome == UpdateOutcome.Failed) code = 2;
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
                code = 3;
            }
            try { File.WriteAllLines(Path.Combine(Core.StateDir, "selftest.txt"), lines.ToArray()); }
            catch { }
            return code;
        }
    }
}
