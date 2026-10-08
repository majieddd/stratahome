using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace StrataHome
{
    /// <summary>
    /// Edits the engine options in a model's run config (strata-*.json) the way Strata's own Settings view does
    /// (serve/runconfig.py): the earlier file is kept as &lt;name&gt;.bak, the new one is written to a temporary file and moved over
    /// the old one, and every other key is left as it was. The server reads the file when it starts.
    /// </summary>
    internal static class RunConfig
    {
        /// <summary>The sizes offered in the window: up to the 262,144 positions the model was trained for. Beyond that Strata's setup
        /// adds rope scaling, which this app does not.</summary>
        public static readonly int[] Presets = new int[] { 8192, 16384, 32768, 65536, 131072, 204800, 262144 };

        public const int Trained = 262144;

        /// <summary>Strata's setup streams the KV cache (keeps it in RAM, the part attention reads in VRAM) from 64K up.</summary>
        public const int StreamFrom = 65536;

        /// <summary>KV cache per context token in KB, from Strata's docs (13.7 KB with 8-bit, 7.5 KB with 4-bit); 0 = not known.</summary>
        public static double KvKbPerToken(string kv)
        {
            if (kv == "int8") return 13.7;
            if (kv == "q4_0") return 7.5;
            return 0;
        }

        static Dictionary<string, object> Load(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
        }

        /// <summary>The value after an engine flag in "args", or null.</summary>
        public static string ArgValue(string path, string flag)
        {
            try
            {
                Dictionary<string, object> root = Load(path);
                IList args = root == null ? null : root["args"] as IList;
                if (args == null) return null;
                for (int i = 0; i + 1 < args.Count; i++)
                    if (flag.Equals(args[i] as string)) return Convert.ToString(args[i + 1], CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        /// <summary>Sets --max-context. From 64K up on a PC with plenty of RAM it also turns on KV streaming (--kv-resident 32768), and
        /// below 64K it removes it, as Strata's setup writes it. Returns null when saved, otherwise what went wrong (nothing is changed then).</summary>
        public static string SetContext(string path, int ctx, double ramGb)
        {
            if (ctx < 1024 || ctx > Trained) return "Pick a context between 1K and 256K tokens.";
            try
            {
                Dictionary<string, object> root = Load(path);
                IList old = root == null ? null : root["args"] as IList;
                if (old == null) return Path.GetFileName(path) + " has no engine options (\"args\").";
                List<object> args = new List<object>();
                foreach (object o in old) args.Add(o);

                int i = args.IndexOf("--max-context");
                if (i >= 0 && i + 1 < args.Count) args[i + 1] = ctx.ToString(CultureInfo.InvariantCulture);
                else { args.Add("--max-context"); args.Add(ctx.ToString(CultureInfo.InvariantCulture)); }

                int j = args.IndexOf("--kv-resident");
                if (ctx < StreamFrom && j >= 0) args.RemoveRange(j, Math.Min(2, args.Count - j));
                else if (ctx >= StreamFrom && j < 0 && ramGb >= 48) { args.Add("--kv-resident"); args.Add("32768"); }

                root["args"] = args;
                string text = Pretty(root);
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> check = js.DeserializeObject(text) as Dictionary<string, object>;   // what we are about to save must read back
                if (check == null || check.Count != root.Count) return "The new config did not read back; nothing was changed.";

                File.Copy(path, path + ".bak", true);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ------------------------------------------------------------------ JSON the way Python's json.dumps(indent=1) writes it

        public static string Pretty(object o)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int depth) { sb.Append(' ', depth); }

        static void Write(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            string s = o as string;
            if (s != null) { Str(sb, s); return; }
            IDictionary<string, object> d = o as IDictionary<string, object>;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int n = 0;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    Indent(sb, depth + 1);
                    Str(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    sb.Append(++n < d.Count ? ",\n" : "\n");
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            IList l = o as IList;
            if (l != null)
            {
                if (l.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int k = 0; k < l.Count; k++)
                {
                    Indent(sb, depth + 1);
                    Write(sb, l[k], depth + 1);
                    sb.Append(k + 1 < l.Count ? ",\n" : "\n");
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4"));       // ensure_ascii, as Python does
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
