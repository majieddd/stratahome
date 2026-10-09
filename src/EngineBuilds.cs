using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace StrataHome
{
    /// <summary>
    /// A Strata engine build that can be chosen: the installed one (engine\strata.exe) and the one kept before the
    /// last update (engine\.previous\strata.exe). Each carries its version in BUILD.json, and the perf log holds the
    /// prefill/decode averages that build earned.
    /// </summary>
    internal sealed class EngineBuild
    {
        public string Exe = "";
        public string Version = "";
        public bool IsCurrent;      // the engine\strata.exe the install uses as it is

        public string Name { get { return Version.Length > 0 ? "Strata " + Version : "unknown version"; } }

        /// <summary>The averages this version earned, or null when nothing is recorded for it yet.</summary>
        public PerfSummary Averages { get { return PerfLog.ForVersion(Version); } }
    }

    internal static class EngineBuilds
    {
        /// <summary>Every engine build the install has, newest version first. The current one is first when versions tie.</summary>
        public static List<EngineBuild> List(string dir)
        {
            List<EngineBuild> builds = new List<EngineBuild>();
            if (string.IsNullOrEmpty(dir)) return builds;
            EngineBuild current = Read(Path.Combine(dir, "engine", "strata.exe"), true);
            EngineBuild previous = Read(Path.Combine(dir, "engine", ".previous", "strata.exe"), false);
            if (current != null) builds.Add(current);
            if (previous != null && previous.Exe != current.Exe) builds.Add(previous);
            builds.Sort(delegate(EngineBuild a, EngineBuild b)
            {
                int c = PerfLog.CompareVersion(b.Version, a.Version);
                return c != 0 ? c : (b.IsCurrent ? 1 : -1);
            });
            return builds;
        }

        static EngineBuild Read(string exe, bool isCurrent)
        {
            if (!File.Exists(exe)) return null;
            EngineBuild b = new EngineBuild();
            b.Exe = exe;
            b.IsCurrent = isCurrent;
            b.Version = VersionOf(exe);
            return b;
        }

        /// <summary>The version in the BUILD.json next to the engine binary, or "" when it is missing.</summary>
        public static string VersionOf(string exe)
        {
            try
            {
                string build = Path.Combine(Path.GetDirectoryName(exe), "BUILD.json");
                if (!File.Exists(build)) return "";
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(build)) as Dictionary<string, object>;
                return d == null ? "" : Convert.ToString(d["version"]);
            }
            catch { return ""; }
        }

        /// <summary>
        /// A copy of the model's config with "exe" pointed at the chosen build, written to
        /// %APPDATA%\StrataHome\engine-configs. The model's own config file is never touched, so a context change
        /// written there is picked up next time this derived config is written.
        /// </summary>
        public static string WriteVariant(string modelConfigPath, string exe, out string message)
        {
            message = "";
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = js.DeserializeObject(File.ReadAllText(modelConfigPath)) as Dictionary<string, object>;
                if (d == null) { message = "The model config could not be read."; return null; }
                d["exe"] = exe;
                string dir = Path.Combine(Paths.Roaming, "engine-configs");
                Directory.CreateDirectory(dir);
                string version = VersionOf(exe);
                string name = Path.GetFileNameWithoutExtension(modelConfigPath) + "-" + (version.Length > 0 ? version : "unknown") + ".json";
                string path = Path.Combine(dir, name);
                File.WriteAllText(path, js.Serialize(d), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex) { message = ex.Message; return null; }
        }

        /// <summary>The config to launch with: the model's own when the install's engine is chosen, else a derived copy
        /// pointing at the chosen build. Null with a message when the copy cannot be written.</summary>
        public static string ConfigFor(ModelEntry model, EngineBuild build, out string message)
        {
            message = "";
            if (model == null) return "";
            if (model.Mock || build == null || build.IsCurrent) return model.ConfigPath;
            return WriteVariant(model.ConfigPath, build.Exe, out message);
        }
    }
}
