// ArboristMcp - stdio MCP server that forwards tool calls to the ArboristBridge plugin
// running inside Arborist (http://127.0.0.1:47811/). Runs on the built-in .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace ArboristBridge
{
    static class Program
    {
        static string _baseUrl = "http://127.0.0.1:47811/";
        static StreamWriter _out;

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        static List<object> L(params object[] xs) { return new List<object>(xs); }
        static Dictionary<string, object> P(string type, string desc) { return D("type", type, "description", desc); }

        static string _logPath;
        static void Log(string m)
        {
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + m + Environment.NewLine); } catch { }
        }

        static int Main(string[] args)
        {
            try { _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ArboristMcp.log"); } catch { _logPath = Path.Combine(Path.GetTempPath(), "ArboristMcp.log"); }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UNHANDLED: " + e.ExceptionObject);
            Log("start; clr=" + Environment.Version + " args=" + string.Join(" ", args));
            try { return Run(args); }
            catch (Exception e) { Log("FATAL: " + e); return 1; }
            finally { Log("exit"); }
        }

        static int Run(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("http")) _baseUrl = args[0].TrimEnd('/') + "/";
            var port = Environment.GetEnvironmentVariable("ARBORIST_BRIDGE_PORT");
            if (!string.IsNullOrEmpty(port)) _baseUrl = "http://127.0.0.1:" + port + "/";
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            new System.Threading.Thread(WatchTools) { IsBackground = true }.Start();
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                Log("<- " + (line.Length > 300 ? line.Substring(0, 300) + "..." : line));
                Dictionary<string, object> msg;
                try { msg = MiniJson.Obj(MiniJson.Parse(line)); }
                catch (Exception e) { Send(D("jsonrpc", "2.0", "id", null, "error", D("code", -32700, "message", "Parse error: " + e.Message))); continue; }
                object id = MiniJson.Get(msg, "id");
                string method = MiniJson.Str(msg, "method", "");
                if (id == null) continue; // notification
                try
                {
                    object result = HandleRequest(method, MiniJson.Obj(MiniJson.Get(msg, "params")));
                    if (result == null) Send(D("jsonrpc", "2.0", "id", id, "error", D("code", -32601, "message", "Method not found: " + method)));
                    else Send(D("jsonrpc", "2.0", "id", id, "result", result));
                }
                catch (Exception e) { Send(D("jsonrpc", "2.0", "id", id, "error", D("code", -32603, "message", e.Message))); }
            }
            Log("stdin closed");
            return 0;
        }

        static void Send(object o) { lock (_out) _out.WriteLine(MiniJson.Serialize(o)); }

        static object HandleRequest(string method, Dictionary<string, object> p)
        {
            switch (method)
            {
                case "initialize":
                    string pv = MiniJson.Str(p, "protocolVersion", "2025-06-18");
                    return D("protocolVersion", pv,
                             "capabilities", D("tools", D("listChanged", true)),
                             "serverInfo", D("name", "arborist", "version", "0.4.0"),
                             "instructions", "Controls the running Arborist (OPCD/GSPro vegetation tool). Units are meters in Arborist world space (x/z horizontal, y up). Call arborist_status first; use arborist_list_catalog to get item ids. Changes are live but only written to the course when arborist_save is called. arborist_undo reverts the bridge's own changes.");
                case "ping": return D();
                case "tools/list": { var t = ListTools(); _lastToolSig = Sig(t); return D("tools", t); }
                case "tools/call": return CallTool(MiniJson.Str(p, "name"), MiniJson.Obj(MiniJson.Get(p, "arguments")));
                case "resources/list": return D("resources", L());
                case "prompts/list": return D("prompts", L());
                default: return null;
            }
        }

        static volatile string _lastToolSig;
        static string Sig(List<object> tools)
        {
            var names = new List<string>();
            foreach (var t in tools) names.Add(MiniJson.Str(MiniJson.Obj(t), "name", ""));
            names.Sort();
            return string.Join(",", names.ToArray());
        }

        // Every 10 s, check whether Arborist's tool list changed (e.g. after an Arborist restart with a newer
        // plugin) and tell Claude to re-fetch it.
        static void WatchTools()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(10000);
                if (_lastToolSig == null) continue;
                try
                {
                    var r = MiniJson.Obj(MiniJson.Parse(Post("{\"cmd\":\"list_tools\"}", 3000)));
                    var t = MiniJson.Get(r, "tools") as List<object>;
                    if (!MiniJson.Bool(r, "ok", false) || t == null || t.Count == 0) continue;
                    string sig = Sig(t);
                    if (sig != _lastToolSig)
                    {
                        Log("tool list changed; notifying client");
                        _lastToolSig = sig;
                        Send(D("jsonrpc", "2.0", "method", "notifications/tools/list_changed"));
                    }
                }
                catch { }
            }
        }

        // Ask the plugin for its current tool list so new tools only need an Arborist restart.
        static List<object> ListTools()
        {
            try
            {
                var r = MiniJson.Obj(MiniJson.Parse(Post("{\"cmd\":\"list_tools\"}", 3000)));
                var t = MiniJson.Get(r, "tools") as List<object>;
                if (MiniJson.Bool(r, "ok", false) && t != null && t.Count > 0) { Log("tools from plugin: " + t.Count); return t; }
            }
            catch (Exception e) { Log("list_tools from plugin failed: " + e.Message); }
            return ToolDefs.Tools();
        }

        static object CallTool(string name, Dictionary<string, object> args)
        {
            if (name == null || !name.StartsWith("arborist_")) return ToolResult("Unknown tool: " + name, true);
            string cmd = name.Substring("arborist_".Length);
            string resp;
            try { resp = Post(MiniJson.Serialize(D("cmd", cmd, "args", args))); }
            catch (Exception e)
            {
                return ToolResult("Could not reach Arborist bridge at " + _baseUrl + " (" + e.Message + "). Is Arborist running with the ArboristBridge plugin installed?", true);
            }
            Dictionary<string, object> r;
            try { r = MiniJson.Obj(MiniJson.Parse(resp)); } catch { return ToolResult(resp, true); }
            bool ok = MiniJson.Bool(r, "ok", false);
            if (!ok) return ToolResult("Arborist error: " + MiniJson.Str(r, "error", resp), true);
            if (cmd == "screenshot")
            {
                string b64 = MiniJson.Str(r, "png_base64");
                r.Remove("png_base64");
                return D("content", L(D("type", "image", "data", b64, "mimeType", "image/png"), D("type", "text", "text", MiniJson.Serialize(r))), "isError", false);
            }
            return ToolResult(MiniJson.Serialize(r), false);
        }

        static Dictionary<string, object> ToolResult(string text, bool isError)
        {
            return D("content", L(D("type", "text", "text", text)), "isError", isError);
        }

        static string Post(string body, int timeoutMs = 130000)
        {
            var req = (HttpWebRequest)WebRequest.Create(_baseUrl);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Proxy = null;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }
    }
}
