using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

namespace StrataHome
{
    /// <summary>Safe reads from the parsed JSON (JavaScriptSerializer gives Dictionary / ArrayList / int / double ...).</summary>
    internal static class J
    {
        public static object Get(object root, params string[] path)
        {
            object o = root;
            foreach (string k in path)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out o)) return null;
            }
            return o;
        }

        public static double? Dbl(object root, params string[] path)
        {
            object o = Get(root, path);
            if (o == null || o is string || o is bool) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static string Str(object root, params string[] path)
        {
            object o = Get(root, path);
            return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static bool Bool(object root, params string[] path)
        {
            object o = Get(root, path);
            return o is bool && (bool)o;
        }

        public static List<double> Series(object root, params string[] path)
        {
            List<double> r = new List<double>();
            IList l = Get(root, path) as IList;
            if (l == null) return r;
            foreach (object x in l)
            {
                double? d = null;
                if (x != null && !(x is string)) { try { d = Convert.ToDouble(x, CultureInfo.InvariantCulture); } catch { } }
                r.Add(d ?? 0);
            }
            return r;
        }

        public static IList List(object root, params string[] path)
        {
            return Get(root, path) as IList ?? new ArrayList();
        }
    }

    /// <summary>
    /// Polls the server's /metrics (the same endpoint the web Monitor uses, once a second) and hands the parsed JSON to the UI
    /// thread. Idle while the server is not running.
    /// </summary>
    internal sealed class MetricsPoller
    {
        public event Action<Dictionary<string, object>> Updated;
        public event Action Lost;

        Thread thread;
        volatile bool stop;
        Func<int> port;
        Func<string> key;
        Func<bool> active;
        int failures;
        public volatile bool ShowAll;
        public volatile bool Slow;                       // window hidden: ask the server every 5 s instead of every second
        public Dictionary<string, object> Last;

        public void Start(Func<int> getPort, Func<string> getKey, Func<bool> isActive)
        {
            port = getPort; key = getKey; active = isActive;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "strata-metrics";
            thread.Start();
        }

        public void Stop() { stop = true; }

        void Loop()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            while (!stop)
            {
                if (active())
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port() + (ShowAll ? "/metrics?requests=all" : "/metrics"));
                        req.Proxy = null;
                        req.Timeout = 3000;
                        req.KeepAlive = false;
                        string k = key();
                        if (!string.IsNullOrEmpty(k)) req.Headers["Authorization"] = "Bearer " + k;
                        using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                        using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        {
                            Dictionary<string, object> m = js.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                            if (m != null)
                            {
                                failures = 0;
                                Last = m;
                                Post(delegate { Action<Dictionary<string, object>> h = Updated; if (h != null) h(m); });
                            }
                        }
                    }
                    catch
                    {
                        if (++failures == 3) Post(delegate { Action h = Lost; if (h != null) h(); });
                    }
                }
                else failures = 0;
                for (int waited = 0; !stop && waited < (Slow ? 5000 : 1000); waited += 250) Thread.Sleep(250);   // wakes within 250 ms when the window comes back
            }
        }

        static void Post(Action a)
        {
            Application app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
        }
    }
}
