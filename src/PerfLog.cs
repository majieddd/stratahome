using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace StrataHome
{
    /// <summary>One finished request: what the engine read, what it produced, and its own clock for each part.</summary>
    internal sealed class PerfRecord
    {
        public double Time;              // the server's own clock (unix seconds)
        public string Version = "";      // the Strata engine version that served it
        public string Model = "";
        public long PromptRead;          // prompt tokens the engine actually read (not the ones the cache held)
        public double PromptMs;
        public long Output;
        public double DecodeMs;

        public double PrefillTokS { get { return PromptMs > 0 && PromptRead > 0 ? PromptRead * 1000.0 / PromptMs : 0; } }
        public double DecodeTokS { get { return DecodeMs > 0 && Output > 0 ? Output * 1000.0 / DecodeMs : 0; } }
    }

    /// <summary>The average for one (Strata version, model): the engine's own totals, so the rate is tokens over the
    /// time they took, not a mean of per-request rates (which weights a 2-token answer like a 2,000-token one).</summary>
    internal sealed class PerfSummary
    {
        public string Version = "", Model = "";
        public int Requests;
        public long PromptTokens, OutputTokens;
        public double PromptMs, DecodeMs;

        public double PrefillTokS { get { return PromptMs > 0 && PromptTokens > 0 ? PromptTokens * 1000.0 / PromptMs : 0; } }
        public double DecodeTokS { get { return DecodeMs > 0 && OutputTokens > 0 ? OutputTokens * 1000.0 / DecodeMs : 0; } }
    }

    /// <summary>
    /// Records every finished request the server reports, keyed by the Strata version that served it, and keeps them
    /// in %LOCALAPPDATA%\StrataHome\perf\requests.jsonl. The averages answer "is this version actually slower for me
    /// than the one before" for both prefill and decode, per model.
    /// </summary>
    internal static class PerfLog
    {
        static readonly HashSet<string> seen = new HashSet<string>();
        static readonly List<PerfRecord> records = new List<PerfRecord>();
        static bool loaded;

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "perf"); } }

        /// <summary>Test hook: keep the recorded requests somewhere other than the user's file.</summary>
        public static string PathOverride = "";

        public static string FilePath { get { return string.IsNullOrEmpty(PathOverride) ? Path.Combine(Dir, "requests.jsonl") : PathOverride; } }

        /// <summary>Test hook: forget everything loaded/recorded, so a test starts from an empty log.</summary>
        public static void ResetForTest() { seen.Clear(); records.Clear(); loaded = false; }

        /// <summary>A request the server has already reported is recorded once, across restarts.</summary>
        public static string KeyOf(IDictionary<string, object> r)
        {
            return Convert.ToString(r["time"], CultureInfo.InvariantCulture) + "|" +
                   Convert.ToString(r["prompt_tokens"], CultureInfo.InvariantCulture) + "|" +
                   Convert.ToString(r["output_tokens"], CultureInfo.InvariantCulture) + "|" +
                   Convert.ToString(r["finish"], CultureInfo.InvariantCulture);
        }

        /// <summary>One /metrics request entry as a record. Null when it has no engine clock (the mock engine) or no
        /// work to measure. prompt_n follows the server's own rule: the prompt minus what the cache already held,
        /// and for a cancelled read only what was actually read.</summary>
        public static PerfRecord Parse(IDictionary<string, object> r, string version, string model)
        {
            PerfRecord rec = new PerfRecord();
            rec.Version = version ?? "";
            rec.Model = model ?? "";
            rec.Time = D(r, "time");
            rec.PromptMs = D(r, "prompt_ms");
            rec.DecodeMs = D(r, "decode_ms");
            long prompt = L(r, "prompt_tokens"), reused = L(r, "reused"), read = L(r, "prompt_read");
            string finish = r["finish"] as string ?? "";
            rec.PromptRead = finish == "cancel" && read > 0 ? Math.Min(prompt, reused + read) : Math.Max(0, prompt - reused);
            rec.Output = Math.Max(0, L(r, "engine_generated") > 0 ? L(r, "engine_generated") : L(r, "output_tokens"));
            if (rec.PromptRead <= 0 && rec.Output <= 0) return null;
            if (rec.PromptRead > 0 && rec.PromptMs <= 0) return null;
            if (rec.Output > 0 && rec.DecodeMs <= 0) return null;
            return rec;
        }

        /// <summary>Loads the file once, so a restart does not record the last requests again.</summary>
        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>;
                    if (d == null) continue;
                    PerfRecord rec = FromJson(d);
                    if (rec == null) continue;
                    records.Add(rec);
                    seen.Add(KeyOf(d));
                }
            }
            catch { }
        }

        /// <summary>Records the requests in a /metrics sample that have not been seen. Returns how many were added.</summary>
        public static int Record(object metrics)
        {
            Load();
            Dictionary<string, object> m = metrics as Dictionary<string, object>;
            if (m == null) return 0;
            string version = J.Str(m, "engine", "version") ?? "";
            string model = J.Str(m, "engine", "model") ?? "";
            IList requests = J.List(m, "requests");
            List<string> lines = new List<string>();
            foreach (object o in requests)
            {
                IDictionary<string, object> r = o as IDictionary<string, object>;
                if (r == null) continue;
                string key = KeyOf(r);
                if (seen.Contains(key)) continue;
                PerfRecord rec = Parse(r, version, model);
                if (rec == null) continue;
                seen.Add(key);
                records.Add(rec);
                lines.Add(ToJson(rec));
            }
            if (lines.Count == 0) return 0;
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, string.Join("\n", lines.ToArray()) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) { Paths.Diag("perf log NOT written: " + ex.Message); }
            return lines.Count;
        }

        /// <summary>Every (version, model) seen, newest version first, best decode first inside a version.</summary>
        public static List<PerfSummary> Summaries()
        {
            Load();
            Dictionary<string, PerfSummary> by = new Dictionary<string, PerfSummary>();
            foreach (PerfRecord r in records)
            {
                string key = r.Version + "\n" + r.Model;
                PerfSummary s;
                if (!by.TryGetValue(key, out s)) { s = new PerfSummary(); s.Version = r.Version; s.Model = r.Model; by[key] = s; }
                s.Requests++;
                s.PromptTokens += r.PromptRead;
                s.OutputTokens += r.Output;
                s.PromptMs += r.PromptMs;
                s.DecodeMs += r.DecodeMs;
            }
            List<PerfSummary> list = new List<PerfSummary>(by.Values);
            list.Sort(delegate(PerfSummary a, PerfSummary b)
            {
                int c = CompareVersion(b.Version, a.Version);
                if (c != 0) return c;
                c = b.DecodeTokS.CompareTo(a.DecodeTokS);
                return c != 0 ? c : b.PrefillTokS.CompareTo(a.PrefillTokS);
            });
            return list;
        }

        /// <summary>The averages for one version over every model, or null when nothing is recorded for it.</summary>
        public static PerfSummary ForVersion(string version)
        {
            PerfSummary s = new PerfSummary();
            s.Version = version;
            foreach (PerfRecord r in records)
            {
                if (r.Version != version) continue;
                s.Requests++;
                s.PromptTokens += r.PromptRead;
                s.OutputTokens += r.Output;
                s.PromptMs += r.PromptMs;
                s.DecodeMs += r.DecodeMs;
            }
            return s.Requests == 0 ? null : s;
        }

        /// <summary>"v0.1.41" / "0.1.41" -> 0,1,41,0 so versions sort the way the releases are numbered. A version
        /// that is not a dotted number sorts last, so the newest real version stays on top.</summary>
        public static int CompareVersion(string a, string b)
        {
            int[] x = VersionKey(a), y = VersionKey(b);
            for (int i = 0; i < 4; i++) if (x[i] != y[i]) return x[i] > y[i] ? 1 : -1;
            return 0;
        }

        static int[] VersionKey(string value)
        {
            string v = (value ?? "").Trim().TrimStart('v', 'V');
            string[] parts = v.Split('.');
            int[] key = new int[4];
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n)) return new int[] { -1, -1, -1, -1 };
                key[i] = n;
            }
            return key;
        }

        static string ToJson(PerfRecord r)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["time"] = r.Time; d["version"] = r.Version; d["model"] = r.Model;
            d["prompt_read"] = r.PromptRead; d["prompt_ms"] = r.PromptMs;
            d["output"] = r.Output; d["decode_ms"] = r.DecodeMs;
            return new JavaScriptSerializer().Serialize(d);
        }

        static PerfRecord FromJson(Dictionary<string, object> d)
        {
            PerfRecord r = new PerfRecord();
            r.Time = D(d, "time");
            r.Version = d["version"] as string ?? "";
            r.Model = d["model"] as string ?? "";
            r.PromptRead = L(d, "prompt_read");
            r.PromptMs = D(d, "prompt_ms");
            r.Output = L(d, "output");
            r.DecodeMs = D(d, "decode_ms");
            if (r.PromptRead <= 0 && r.Output <= 0) return null;
            return r;
        }

        static double D(IDictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null || v is string || v is bool) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static long L(IDictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null || v is string || v is bool) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }
}
