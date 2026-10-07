using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace StrataHome
{
    /// <summary>One model Strata's setup has installed: a strata-*.json next to run-*.bat.</summary>
    internal sealed class ModelEntry
    {
        public string ConfigPath = "";
        public string ModelName = "";
        public int Port = 8080;
        public int MaxContext;
        public bool Mock;                       // --selftest only: Strata's built-in mock engine, no model, no GPU

        public string FileName { get { return Path.GetFileName(ConfigPath); } }

        public string Label
        {
            get
            {
                string name = ModelName.Length > 0 ? ModelName : Path.GetFileNameWithoutExtension(ConfigPath);
                return MaxContext > 0 ? name + "  (" + StrataInstall.Tokens(MaxContext) + " context)" : name;
            }
        }

        public override string ToString() { return Label; }
    }

    internal static class StrataInstall
    {
        public static string PythonPath(string dir) { return Path.Combine(dir, ".venv", "Scripts", "python.exe"); }

        public static string ServerPath(string dir) { return Path.Combine(dir, "serve", "server.py"); }

        public static bool IsValid(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(ServerPath(dir)) && File.Exists(PythonPath(dir));
        }

        public static string Tokens(int n)
        {
            return n >= 1024 ? (n / 1024) + "K" : n.ToString();
        }

        /// <summary>The saved folder if it still works, else the usual places a Strata checkout lives.</summary>
        public static string Find(string saved)
        {
            List<string> c = new List<string>();
            c.Add(saved);
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            c.Add(exeDir);
            c.Add(Path.Combine(exeDir, "Strata"));
            DirectoryInfo parent = Directory.GetParent(exeDir);
            if (parent != null)
            {
                c.Add(parent.FullName);
                c.Add(Path.Combine(parent.FullName, "Strata"));
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] roots = new string[]
            {
                home,
                Path.Combine(home, "Documents"),
                Path.Combine(home, "Desktop"),
                Path.Combine(home, "Downloads")
            };
            foreach (string root in roots)
            {
                c.Add(Path.Combine(root, "Strata"));
                try
                {
                    foreach (string d in Directory.GetDirectories(root))
                    {
                        c.Add(Path.Combine(d, "Strata"));
                        c.Add(d);
                    }
                }
                catch { }
            }
            foreach (string dir in c)
            {
                if (IsValid(dir)) return dir;
            }
            return null;
        }

        /// <summary>Every strata-*.json in the folder: the models setup has installed.</summary>
        public static List<ModelEntry> Models(string dir)
        {
            List<ModelEntry> list = new List<ModelEntry>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            JavaScriptSerializer json = new JavaScriptSerializer();
            string[] files = Directory.GetFiles(dir, "strata-*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                try
                {
                    Dictionary<string, object> cfg = json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                    if (cfg == null || !cfg.ContainsKey("exe") || !cfg.ContainsKey("args")) continue;   // not an engine config
                    ModelEntry m = new ModelEntry();
                    m.ConfigPath = file;
                    object v;
                    if (cfg.TryGetValue("model_name", out v) && v is string) m.ModelName = (string)v;
                    if (cfg.TryGetValue("port", out v) && v != null) m.Port = Convert.ToInt32(v);
                    IList args = cfg["args"] as IList;
                    if (args != null)
                    {
                        for (int i = 0; i + 1 < args.Count; i++)
                        {
                            if ("--max-context".Equals(args[i] as string))
                            {
                                int n;
                                if (int.TryParse(Convert.ToString(args[i + 1]), out n)) m.MaxContext = n;
                            }
                        }
                    }
                    list.Add(m);
                }
                catch { }
            }
            return list;
        }
    }
}
