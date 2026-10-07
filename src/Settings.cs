using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace StrataHome
{
    internal static class Paths
    {
        public static string Roaming
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrataHome"); }
        }

        public static string Logs
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "logs"); }
        }

        /// <summary>A line in logs\app.log (or, if that folder cannot be written, in %TEMP%). Never throws.</summary>
        public static void Diag(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n";
            try
            {
                Directory.CreateDirectory(Logs);
                File.AppendAllText(Path.Combine(Logs, "app.log"), line);
            }
            catch
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "stratahome-app.log"), line); } catch { }
            }
        }
    }

    /// <summary>The few things the app remembers, in %APPDATA%\StrataHome\settings.json.</summary>
    internal sealed class Settings
    {
        public string StrataDir = "";
        public string Config = "";               // file name of the strata-*.json that was last used
        public string Mode = "always";           // always | idle | ondemand
        public int IdleMinutes = 10;
        public bool AutoStartServer = true;      // start Strata when this app opens
        public bool KeepRunning = false;         // leave Strata running when this app exits
        public string Effort = "medium";         // the quick chat's reasoning effort

        static string FilePath { get { return Path.Combine(Paths.Roaming, "settings.json"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (d == null) return s;
                s.StrataDir = Str(d, "strataDir", s.StrataDir);
                s.Config = Str(d, "config", s.Config);
                s.Mode = Str(d, "mode", s.Mode);
                s.IdleMinutes = Math.Max(1, Math.Min(1440, Int(d, "idleMinutes", s.IdleMinutes)));
                s.AutoStartServer = Bool(d, "autoStartServer", s.AutoStartServer);
                s.KeepRunning = Bool(d, "keepRunning", s.KeepRunning);
                s.Effort = Str(d, "effort", s.Effort);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Roaming);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["strataDir"] = StrataDir;
                d["config"] = Config;
                d["mode"] = Mode;
                d["idleMinutes"] = IdleMinutes;
                d["autoStartServer"] = AutoStartServer;
                d["keepRunning"] = KeepRunning;
                d["effort"] = Effort;
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(d));
                Paths.Diag("settings saved: " + FilePath);
            }
            catch (Exception ex) { Paths.Diag("settings NOT saved (" + FilePath + "): " + ex.Message); }
        }

        static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : fallback;
        }

        static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return fallback;
        }

        static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }
    }
}
