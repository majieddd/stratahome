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
        public string Effort = "high";           // thinking: none | low | medium | high (the web app's default is high)
        public string Theme = "system";          // system | light | dark
        public double Temperature = 0.6;
        public double TopP = 0.95;
        public int TopK = 20;
        public string MaxTokens = "";            // empty = until done
        public string Seed = "";                 // empty = random
        public bool ShowThinking = true;
        public string ApiKey = "";               // only for a server that was started with one

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
                s.Theme = Str(d, "theme", s.Theme);
                s.Temperature = Dbl(d, "temperature", s.Temperature);
                s.TopP = Dbl(d, "topP", s.TopP);
                s.TopK = Int(d, "topK", s.TopK);
                s.MaxTokens = Str(d, "maxTokens", s.MaxTokens);
                s.Seed = Str(d, "seed", s.Seed);
                s.ShowThinking = Bool(d, "showThinking", s.ShowThinking);
                s.ApiKey = Str(d, "apiKey", s.ApiKey);
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
                d["theme"] = Theme;
                d["temperature"] = Temperature;
                d["topP"] = TopP;
                d["topK"] = TopK;
                d["maxTokens"] = MaxTokens;
                d["seed"] = Seed;
                d["showThinking"] = ShowThinking;
                d["apiKey"] = ApiKey;
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

        static double Dbl(Dictionary<string, object> d, string key, double fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
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
