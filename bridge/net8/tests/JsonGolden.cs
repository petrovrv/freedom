// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F3a: JSON parity cases. The same code runs against the real JavaScriptSerializer (.NET Framework 4.8,
// make-json-golden.cmd writes json-golden.txt) and against net8/JavaScriptSerializerCompat.cs (Net8Tests compares
// with json-golden.txt). C# 5 on purpose: csc of .NET Framework compiles it. Output has no local paths or times.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AiWorkbenchBridge.Tests
{
    public enum GoldenColor { Red, Green = 5 }

    public class GoldenInner
    {
        public string name { get; set; }
        public int n;
    }

    public class GoldenHolder
    {
        public object any { get; set; }
        public List<int> li { get; set; }
        public int[] ai { get; set; }
        public long l { get; set; }
        public double d { get; set; }
        public string s { get; set; }
        public bool b { get; set; }
        public GoldenColor c { get; set; }
        public int? ni { get; set; }
        public DateTime dt { get; set; }
        public Dictionary<string, object> dict { get; set; }
        public Dictionary<string, int> dicti { get; set; }
        public List<GoldenInner> inners { get; set; }
        public IList<string> ils { get; set; }
        public decimal dec { get; set; }
        public string ro { get { return "x"; } }
        [ScriptIgnore]
        public string hidden { get; set; }
        public float f;
    }

    public class GoldenContainers
    {
        public List<object> lo { get; set; }
        public object[] ao { get; set; }
        public Dictionary<string, List<string>> dls { get; set; }
        public Dictionary<string, GoldenInner> din { get; set; }
        public IDictionary<string, object> idict { get; set; }
        public Hashtable ht { get; set; }
        public ArrayList al { get; set; }
        public IEnumerable<int> ien { get; set; }
        public string[] sa { get; set; }
        public GoldenInner one { get; set; }
        public object objd { get; set; }
    }

    internal static class JsonGolden
    {
        // Lines of "<case> | <result>"; the bridge folder holds config.json.example and tests/samples.
        public static List<string> Produce(string bridgeDir)
        {
            List<string> o = new List<string>();
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = Int32.MaxValue;
            js.RecursionLimit = 100;

            string[] docs = new string[]
            {
                "{\"a\":1,\"b\":2147483648,\"c\":1.5,\"d\":1e3,\"e\":12345678901234567890123,\"f\":[1,[2,\"x\"],{\"g\":null}],\"h\":true,\"i\":\"\\/Date(1700000000000)\\/\",\"j\":\"/Date(5)/\",\"k\":-0,\"l\":0.0,\"m\":1E-7,\"a\":7}",
                "[1,2]", "\"str\"", "3", "{'q':'single'}", "{\"u\":\"\\u0041\\/\\b\"}",
                "", "  ", "{", "{\"a\":1,}", "[1,]", "{a:1}", "{\"a\":01}", "{\"a\":.5}", "{\"a\":+1}", "/*c*/{\"a\":1}",
                "{\"a\":1} x", "{\"a\":tru}", "{\"a\":\"\\x\"}", "{\"a\":undefined}", "{\"a\":NaN}", "{\"a\":\"\\u00e9\"}", "{\"a\":1}\n",
                "{\"big\":9223372036854775807,\"bigger\":9223372036854775808,\"neg\":-2147483649,\"exp\":2.5e-3,\"dot\":3.0,\"z\":0}",
                "{\"d\":\"\\/Date(-5)\\/\",\"d2\":\"\\/Date(1700000000000+0100)\\/\",\"d3\":\"\\/Date(x)\\/\"}",
                "{\"nested\":{\"a\":{\"b\":{\"c\":[[[]]]}}},\"empty\":{},\"arr\":[]}",
                "{\"s\":\"tab\\tnl\\ncr\\rq\\\"bs\\\\sl\\/u\\u2028\"}",
            };
            for (int i = 0; i < docs.Length; i++)
            {
                o.Add("DO" + i + " | " + Try(delegate { return T(js.DeserializeObject(docs[i])); }));
                o.Add("Dict" + i + " | " + Try(delegate { return T(js.Deserialize<Dictionary<string, object>>(docs[i])); }));
            }

            string tree = "{\"x\":[1,[2,[3]],{\"q\":[3,{\"r\":[4]}]}],\"y\":{\"z\":[5]},\"n\":null}";
            o.Add("tree<object> | " + T(js.Deserialize<object>(tree)));
            o.Add("tree<IDictionary> | " + T(js.Deserialize<IDictionary<string, object>>(tree)));

            string h = "{\"ANY\":[1,{\"z\":[2]}],\"li\":[1,\"2\"],\"ai\":[3],\"l\":5,\"d\":2,\"s\":7,\"b\":\"true\",\"c\":\"green\",\"ni\":null,\"dt\":\"2026-01-02T03:04:05Z\",\"dict\":{\"x\":[1],\"y\":{\"w\":1}},\"dicti\":{\"k\":\"4\"},\"inners\":[{\"NAME\":\"a\",\"n\":1}],\"ils\":[\"a\"],\"dec\":1.25,\"ro\":\"y\",\"hidden\":\"h\",\"f\":0.1,\"unknown\":1}";
            o.Add("holder | " + Try(delegate
            {
                GoldenHolder x = js.Deserialize<GoldenHolder>(h);
                return T(x.any) + " li=" + T(x.li) + " ai=" + T(x.ai) + " l=" + x.l + " d=" + Inv(x.d) + " s=" + x.s + " b=" + x.b +
                    " c=" + x.c + " ni=" + x.ni + " dt=" + x.dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + " " + x.dt.Kind +
                    " dict=" + T(x.dict) + " dicti=" + T(x.dicti) + " inner=" + x.inners[0].name + x.inners[0].n + " ils=" + x.ils.GetType().Name +
                    " dec=" + Inv(x.dec) + " hidden=" + x.hidden + " f=" + Inv(x.f);
            }));
            string[] bad = new string[] { "{\"l\":null}", "{\"b\":1}", "{\"c\":9}", "{\"l\":1.5}", "{\"d\":\"abc\"}", "{\"inners\":5}", "{\"li\":{}}", "[1]", "null", "{\"ni\":\"7\"}", "{\"s\":true}", "{\"d\":\"1.5\"}", "{\"l\":\"12\"}" };
            foreach (string b in bad)
            {
                string doc = b;
                o.Add("holder " + doc + " | " + Try(delegate
                {
                    GoldenHolder x = js.Deserialize<GoldenHolder>(doc);
                    return x == null ? "null" : ("l=" + x.l + " b=" + x.b + " c=" + x.c + " ni=" + x.ni + " s=" + x.s + " d=" + Inv(x.d));
                }));
            }

            string c = "{\"lo\":[1,[2],{\"a\":[3]}],\"ao\":[1,[2],{\"a\":[3]}],\"dls\":{\"k\":[\"a\"]},\"din\":{\"k\":{\"name\":\"v\"}},\"idict\":{\"a\":[1,{\"b\":[2]}]},\"ht\":{\"a\":[1]},\"al\":[1,[2],{\"a\":[3]}],\"ien\":[1],\"sa\":[\"s\",1],\"one\":null,\"objd\":{\"a\":[1,{\"b\":[2]}]}}";
            o.Add("containers | " + Try(delegate
            {
                GoldenContainers x = js.Deserialize<GoldenContainers>(c);
                return "lo=" + T(x.lo) + " ao=" + T(x.ao) + " dls=" + T(x.dls) + " din=" + x.din["k"].name + " idict=" + T(x.idict) +
                    " ht=" + T(x.ht) + " al=" + T(x.al) + " ien=" + x.ien.GetType().Name + " sa=" + T(x.sa) + " objd=" + T(x.objd);
            }));

            js.RecursionLimit = 3;
            o.Add("depth4 | " + Try(delegate { return T(js.DeserializeObject("[[[[1]]]]")); }));
            o.Add("depth3 | " + Try(delegate { return T(js.DeserializeObject("[[[1]]]")); }));
            o.Add("depth2 | " + Try(delegate { return T(js.DeserializeObject("[[1]]")); }));
            js.RecursionLimit = 100;

            // Serialize
            GoldenHolder ho = new GoldenHolder();
            ho.s = "a\"b\\c\n\r\t\b\f<>&'\u0001\u001f\u007f\u0085\u2028\u2029\u00e9/\ud83d\ude00";
            ho.d = 0.1;
            ho.f = 0.1f;
            ho.dt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            ho.c = GoldenColor.Green;
            ho.dict = new Dictionary<string, object>();
            ho.dict["k"] = new object[] { 1L, 2.5m, null, 'c', '\0', Guid.Empty, (byte)7, (short)-3, 4u, ulong.MaxValue, true };
            ho.dec = 1.10m;
            ho.ni = 3;
            ho.inners = new List<GoldenInner>();
            ho.inners.Add(new GoldenInner());
            o.Add("ser holder | " + js.Serialize(ho));
            Dictionary<string, object> sd = new Dictionary<string, object>();
            ArrayList al = new ArrayList();
            al.Add(1);
            al.Add("x");
            sd["a"] = al;
            sd["b"] = new Uri("http://h/a b?x=1");
            sd["c"] = double.NaN;
            sd["d"] = 1e20;
            sd["e"] = 1.0;
            sd["f"] = new Hashtable();
            sd["g"] = 1.0 / 3;
            sd["h"] = -0.0;
            sd["i"] = 123456789.123456789;
            sd["j"] = 5e-324;
            sd["k"] = double.MaxValue;
            sd["l"] = 0.1f + 0.2f;
            sd["m"] = new int[0];
            sd["n"] = new Dictionary<string, int>();
            sd["o"] = "";
            o.Add("ser dict | " + js.Serialize(sd));
            o.Add("ser inf | " + Try(delegate { return js.Serialize(double.PositiveInfinity); }));
            o.Add("ser null | " + js.Serialize(null));
            StringBuilder all = new StringBuilder();
            for (int ch = 0; ch < 0x100; ch++)
                all.Append((char)ch);
            o.Add("ser chars | " + js.Serialize(all.ToString()));
            o.Add("roundtrip chars | " + (js.Deserialize<string>(js.Serialize(all.ToString())) == all.ToString()));

            // Bridge's own types and the stream samples
            string example = Path.Combine(bridgeDir, "config.json.example");
            if (File.Exists(example))
            {
                string text = File.ReadAllText(example, Encoding.UTF8);
                o.Add("config DO | " + js.Serialize(js.DeserializeObject(text)));
                o.Add("config BridgeConfig | " + Try(delegate { return JsonUtil.Serialize(JsonUtil.Deserialize<BridgeConfig>(text)); }));
            }
            JobRequest jr = new JobRequest();
            jr.jobId = "JOB-1";
            jr.instruction = "line1\nline2 <tag> & 'q'";
            o.Add("JobRequest | " + JsonUtil.Serialize(jr));
            o.Add("JobRequest back | " + JsonUtil.Serialize(JsonUtil.Deserialize<JobRequest>(JsonUtil.Serialize(jr))));
            JobResult res = new JobResult();
            o.Add("JobResult | " + JsonUtil.Serialize(res));
            string samples = Path.Combine(Path.Combine(bridgeDir, "tests"), "samples");
            if (Directory.Exists(samples))
            {
                List<string> files = new List<string>(Directory.GetFiles(samples));
                files.Sort(StringComparer.Ordinal);
                foreach (string f in files)
                {
                    int n = 0;
                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        if (line.Trim().Length == 0)
                            continue;
                        n++;
                        string l = line;
                        o.Add(Path.GetFileName(f) + "#" + n + " | " + Try(delegate { return T(js.DeserializeObject(l)) + " => " + js.Serialize(js.DeserializeObject(l)); }));
                    }
                }
            }
            return o;
        }

        private delegate string Case();

        private static string Try(Case c)
        {
            try
            {
                return c();
            }
            catch (Exception)
            {
                // Exception types differ between the .NET Framework and .NET 8 converters; only failure is compared.
                return "ERR";
            }
        }

        private static string Inv(IFormattable v)
        {
            return v.ToString(null, CultureInfo.InvariantCulture);
        }

        // Type tree: "Dictionary`2{k:Int32=1,}" / "Object[][...]" / "ArrayList[...]".
        private static string T(object o)
        {
            if (o == null)
                return "null";
            IDictionary d = o as IDictionary;
            if (d != null)
            {
                StringBuilder sb = new StringBuilder(o.GetType().Name + "{");
                List<string> keys = new List<string>();
                foreach (object k in d.Keys)
                    keys.Add(Convert.ToString(k, CultureInfo.InvariantCulture));
                if (o is Hashtable)
                    keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                    sb.Append(k).Append(':').Append(T(d[k])).Append(',');
                return sb.Append('}').ToString();
            }
            IList l = o as IList;
            if (l != null)
            {
                StringBuilder sb = new StringBuilder(o.GetType().Name + "[");
                foreach (object x in l)
                    sb.Append(T(x)).Append(',');
                return sb.Append(']').ToString();
            }
            if (o is DateTime)
                return "DateTime=" + ((DateTime)o).ToString("o", CultureInfo.InvariantCulture) + "/" + ((DateTime)o).Kind;
            return o.GetType().Name + "=" + Printable(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        // Control and line-separator chars as \uXXXX, so a case stays on one line of json-golden.txt.
        private static string Printable(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch < ' ' || ch == (char)0x85 || ch == (char)0x2028 || ch == (char)0x2029)
                    sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        // make-json-golden.cmd: JsonGolden.exe <bridge dir> <out file>
        public static int Main(string[] argv)
        {
            if (argv.Length != 2)
            {
                Console.WriteLine("usage: JsonGolden <bridge dir> <out file>");
                return 2;
            }
            File.WriteAllLines(argv[1], Produce(argv[0]).ToArray(), new UTF8Encoding(false));
            return 0;
        }
    }
}
