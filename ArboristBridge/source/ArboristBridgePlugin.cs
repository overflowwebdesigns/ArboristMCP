// ArboristBridge - BepInEx 5 plugin that exposes Arborist's planting system over a
// localhost-only HTTP API (http://127.0.0.1:47811/) so an MCP server can drive it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using AwesomeTechnologies.VegetationSystem;
using AwesomeTechnologies.Vegetation.PersistentStorage;
using AwesomeTechnologies;
using AwesomeTechnologies.VegetationSystem.Biomes;
using OPCD_Arborist;
using UnityEngine.Rendering.PostProcessing;

namespace ArboristBridge
{
    [BepInPlugin("com.sableoaks.arboristbridge", "Arborist Bridge", "0.5.1")]
    public class ArboristBridgePlugin : BaseUnityPlugin
    {
        public const byte SourceId = 5;        // same source ID Arborist's paint brush uses
        ConfigEntry<int> _port;
        HttpListener _listener;
        Thread _thread;
        volatile bool _running;

        class Job { public string Body; public string Result; public ManualResetEvent Done = new ManualResetEvent(false); }
        readonly Queue<Job> _jobs = new Queue<Job>();

        // Undo history: each entry is one plant/remove/move command.
        class ItemRec { public string Id; public Vector3 Pos; public Vector3 Scale; public Quaternion Rot; public byte Src; public float Falloff; }
        class Op { public string Kind; public List<ItemRec> Added = new List<ItemRec>(); public List<ItemRec> Removed = new List<ItemRec>(); public DateTime When = DateTime.Now; }
        readonly List<Op> _history = new List<Op>();

        void Awake()
        {
            _port = Config.Bind("Server", "Port", 47811, "Localhost port for the Arborist bridge HTTP API");
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + _port.Value + "/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true, Name = "ArboristBridgeHttp" };
                _thread.Start();
                Logger.LogInfo("Arborist Bridge listening on http://127.0.0.1:" + _port.Value + "/");
            }
            catch (Exception e) { Logger.LogError("Arborist Bridge failed to start: " + e); }
        }

        void OnDestroy()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); } catch { if (!_running) return; continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            string result;
            try
            {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                if (ctx.Request.HttpMethod == "GET") body = "{\"cmd\":\"status\"}";
                var job = new Job { Body = body };
                lock (_jobs) _jobs.Enqueue(job);
                if (!job.Done.WaitOne(120000)) result = Err("Timed out waiting for Arborist's main thread (is the window frozen or minimized?)");
                else result = job.Result;
            }
            catch (Exception e) { result = Err(e.Message); }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(result);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        void Update()
        {
            // Process a few jobs per frame on Unity's main thread.
            for (int n = 0; n < 4; n++)
            {
                Job job = null;
                lock (_jobs) if (_jobs.Count > 0) job = _jobs.Dequeue();
                if (job == null) break;
                if (job.Body != null && job.Body.Contains("\"screenshot\""))
                {
                    try
                    {
                        var req = MiniJson.Obj(MiniJson.Parse(job.Body));
                        if (MiniJson.Str(req, "cmd") == "screenshot") { StartCoroutine(ScreenshotJob(job, MiniJson.Obj(MiniJson.Get(req, "args")))); continue; }
                    }
                    catch (Exception e) { job.Result = Err(e.Message); job.Done.Set(); continue; }
                }
                try { job.Result = Dispatch(job.Body); }
                catch (Exception e) { job.Result = Err(e.GetType().Name + ": " + e.Message); Logger.LogError(e); }
                job.Done.Set();
            }
        }

        // ------------------------------------------------------------------ dispatch
        static string Err(string m) { return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", m } }); }
        static Dictionary<string, object> Ok() { return new Dictionary<string, object> { { "ok", true } }; }

        string Dispatch(string body)
        {
            var req = MiniJson.Obj(MiniJson.Parse(string.IsNullOrEmpty(body) ? "{}" : body));
            string cmd = MiniJson.Str(req, "cmd", "status");
            var a = MiniJson.Obj(MiniJson.Get(req, "args"));
            object res;
            switch (cmd)
            {
                case "status": res = CmdStatus(); break;
                case "call":
                    {
                        // Generic passthrough so new plugin commands work even when the client's tool list is stale.
                        string inner = MiniJson.Str(a, "cmd");
                        if (string.IsNullOrEmpty(inner) || inner == "call") return Err("call needs a 'cmd' (e.g. pp_get)");
                        if (inner == "screenshot") return Err("Use arborist_screenshot for screenshots");
                        return Dispatch(MiniJson.Serialize(new Dictionary<string, object> { { "cmd", inner }, { "args", MiniJson.Obj(MiniJson.Get(a, "args")) } }));
                    }
                case "list_commands":
                    {
                        var t = Ok(); t["commands"] = ToolDefs.Tools().Select(x => (object)MiniJson.Str(MiniJson.Obj(x), "name", "").Replace("arborist_", "")).ToList(); res = t;
                    }
                    break;
                case "list_catalog": res = CmdCatalog(a); break;
                case "list_planted": res = CmdListPlanted(a); break;
                case "ground_height": res = CmdGround(a); break;
                case "plant": res = CmdPlant(a); break;
                case "remove": res = CmdRemove(a); break;
                case "move": res = CmdMove(a); break;
                case "undo": res = CmdUndo(a); break;
                case "get_camera": res = CmdGetCamera(); break;
                case "set_camera": res = CmdSetCamera(a); break;
                case "screenshot": res = CmdScreenshot(a); break;
                case "save": res = CmdSave(); break;
                case "list_biomes": res = CmdListBiomes(a); break;
                case "list_tools": { var t = Ok(); t["tools"] = ToolDefs.Tools(); res = t; } break;
                case "list_masks": res = CmdListMasks(a); break;
                case "find_objects": res = CmdFindObjects(a); break;
                case "pp_get": res = CmdPPGet(a); break;
                case "pp_set": res = CmdPPSet(a); break;
                case "pp_undo": res = CmdPPUndo(a); break;
                case "pp_revert_to_saved": res = CmdPPRevert(a); break;
                case "mesh_outline": res = CmdMeshOutline(a); break;
                case "export_mesh": res = CmdExportMesh(a); break;
                case "create_vegetation_mask": res = CmdCreateVegMask(a); break;
                case "create_biome_mask": res = CmdCreateBiomeMask(a); break;
                case "delete_masks": res = CmdDeleteMasks(a); break;
                default: return Err("Unknown command: " + cmd);
            }
            return MiniJson.Serialize(res);
        }

        // ------------------------------------------------------------------ access to Arborist internals
        static readonly FieldInfo FStorage = typeof(MenuSystem).GetField("_persistentVegetationStorage", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo FSystem = typeof(MenuSystem).GetField("_vegetationSystemPro", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        static PersistentVegetationStorage Storage()
        {
            var s = FStorage != null ? FStorage.GetValue(null) as PersistentVegetationStorage : null;
            if (s == null) s = UnityEngine.Object.FindObjectOfType<PersistentVegetationStorage>();
            return s;
        }
        static VegetationSystemPro VSys()
        {
            var v = FSystem != null ? FSystem.GetValue(null) as VegetationSystemPro : null;
            if (v == null) { var st = Storage(); if (st != null) v = st.VegetationSystemPro; }
            if (v == null) v = UnityEngine.Object.FindObjectOfType<VegetationSystemPro>();
            return v;
        }
        static void RequireCourse(out PersistentVegetationStorage st, out VegetationSystemPro vs)
        {
            st = Storage(); vs = VSys();
            if (st == null || vs == null || st.PersistentVegetationStoragePackage == null || st.PersistentVegetationStoragePackage.PersistentVegetationCellList.Count == 0)
                throw new Exception("No course with vegetation is loaded in Arborist yet");
        }

        static List<object> V3(Vector3 v) { return new List<object> { R(v.x), R(v.y), R(v.z) }; }
        static double R(float f) { return Math.Round(f, 3); }

        // ------------------------------------------------------------------ status / catalog
        object CmdStatus()
        {
            var st = Storage(); var vs = VSys();
            var d = Ok();
            d["arborist_version"] = Application.version;
            d["unity_version"] = Application.unityVersion;
            d["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var mm = MenuSystem.mainmenu;
            d["course_path"] = mm != null ? mm.coursepath : null;
            bool loaded = st != null && vs != null && st.PersistentVegetationStoragePackage != null && st.PersistentVegetationStoragePackage.PersistentVegetationCellList.Count > 0;
            d["course_loaded"] = loaded;
            if (vs != null)
            {
                var b = vs.VegetationSystemBounds;
                d["bounds_min"] = V3(b.min); d["bounds_max"] = V3(b.max);
                d["vegetation_system_position"] = V3(vs.VegetationSystemPosition);
                d["cell_size"] = vs.VegetationCellSize;
                d["biome_count"] = vs.VegetationPackageProList.Count;
                d["terrain_collider_count"] = vs.meshTerrainColliders.Count;
            }
            if (loaded)
            {
                int total = 0;
                foreach (var info in st.PersistentVegetationStoragePackage.GetPersistentVegetationInstanceInfoList()) total += info.Count;
                d["planted_total"] = total;
            }
            d["undo_steps"] = _history.Count;
            var cam = Cam();
            if (cam != null) { d["camera_position"] = V3(cam.transform.position); d["camera_euler"] = V3(cam.transform.eulerAngles); }
            return d;
        }

        object CmdCatalog(Dictionary<string, object> a)
        {
            var vs = VSys();
            if (vs == null) throw new Exception("Vegetation system not found (load a course first)");
            string filter = (MiniJson.Str(a, "filter", "") ?? "").ToLowerInvariant();
            string type = (MiniJson.Str(a, "type", "") ?? "").ToLowerInvariant();
            var counts = new Dictionary<string, int>();
            var st = Storage();
            if (st != null && st.PersistentVegetationStoragePackage != null)
                foreach (var info in st.PersistentVegetationStoragePackage.GetPersistentVegetationInstanceInfoList()) counts[info.VegetationItemID] = info.Count;
            var items = new List<object>();
            for (int bi = 0; bi < vs.VegetationPackageProList.Count; bi++)
            {
                var pkg = vs.VegetationPackageProList[bi];
                foreach (var it in pkg.VegetationInfoList)
                {
                    string t = it.VegetationType.ToString();
                    if (type != "" && t.ToLowerInvariant() != type) continue;
                    if (filter != "" && (it.Name ?? "").ToLowerInvariant().IndexOf(filter) < 0 && (pkg.PackageName ?? "").ToLowerInvariant().IndexOf(filter) < 0) continue;
                    int c; counts.TryGetValue(it.VegetationItemID, out c);
                    items.Add(new Dictionary<string, object> {
                        { "id", it.VegetationItemID }, { "name", it.Name }, { "type", t },
                        { "biome", pkg.PackageName }, { "biome_index", bi },
                        { "min_scale", R(it.MinScale) }, { "max_scale", R(it.MaxScale) },
                        { "planted_count", c }
                    });
                }
            }
            var d = Ok(); d["count"] = items.Count; d["items"] = items; return d;
        }

        // ------------------------------------------------------------------ query planted items
        struct Found { public string Id; public Vector3 World; public PersistentVegetationItem Item; public float Dist; }

        IEnumerable<Found> Enumerate(PersistentVegetationStorage st, VegetationSystemPro vs, HashSet<string> ids, Vector2? center, float radius, Rect? rect)
        {
            Vector3 off = vs.VegetationSystemPosition;
            foreach (var cell in st.PersistentVegetationStoragePackage.PersistentVegetationCellList)
            {
                foreach (var info in cell.PersistentVegetationInfoList)
                {
                    if (ids != null && !ids.Contains(info.VegetationItemID)) continue;
                    foreach (var item in info.VegetationItemList)
                    {
                        Vector3 w = item.Position + off;
                        float dist = 0f;
                        if (center.HasValue)
                        {
                            dist = Vector2.Distance(new Vector2(w.x, w.z), center.Value);
                            if (dist > radius) continue;
                        }
                        if (rect.HasValue && !rect.Value.Contains(new Vector2(w.x, w.z))) continue;
                        yield return new Found { Id = info.VegetationItemID, World = w, Item = item, Dist = dist };
                    }
                }
            }
        }

        static HashSet<string> IdSet(Dictionary<string, object> a)
        {
            var l = MiniJson.Get(a, "item_ids") as List<object>;
            string one = MiniJson.Str(a, "item_id");
            if (l == null && one == null) return null;
            var h = new HashSet<string>();
            if (l != null) foreach (var x in l) h.Add(Convert.ToString(x));
            if (one != null) h.Add(one);
            return h;
        }

        static void Area(Dictionary<string, object> a, out Vector2? center, out float radius, out Rect? rect)
        {
            center = null; rect = null; radius = 0;
            if (MiniJson.Has(a, "x") && MiniJson.Has(a, "z"))
            {
                center = new Vector2((float)MiniJson.Num(a, "x", 0), (float)MiniJson.Num(a, "z", 0));
                radius = (float)MiniJson.Num(a, "radius", 25);
            }
            if (MiniJson.Has(a, "min_x"))
            {
                float x0 = (float)MiniJson.Num(a, "min_x", 0), z0 = (float)MiniJson.Num(a, "min_z", 0);
                float x1 = (float)MiniJson.Num(a, "max_x", 0), z1 = (float)MiniJson.Num(a, "max_z", 0);
                rect = Rect.MinMaxRect(Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1));
            }
        }

        object CmdListPlanted(Dictionary<string, object> a)
        {
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            Vector2? c; float rad; Rect? rect; Area(a, out c, out rad, out rect);
            int limit = (int)MiniJson.Num(a, "limit", 200);
            bool summary = MiniJson.Bool(a, "summary_only", false);
            var names = NameMap(vs);
            var all = Enumerate(st, vs, IdSet(a), c, rad, rect).ToList();
            if (c.HasValue) all.Sort((p, q) => p.Dist.CompareTo(q.Dist));
            var byId = new Dictionary<string, int>();
            foreach (var f in all) { int n; byId.TryGetValue(f.Id, out n); byId[f.Id] = n + 1; }
            var d = Ok();
            d["total_matching"] = all.Count;
            var summ = new List<object>();
            foreach (var kv in byId.OrderByDescending(k => k.Value))
                summ.Add(new Dictionary<string, object> { { "id", kv.Key }, { "name", Lookup(names, kv.Key) }, { "count", kv.Value } });
            d["by_item"] = summ;
            if (!summary)
            {
                var items = new List<object>();
                foreach (var f in all.Take(limit))
                {
                    var e = new Dictionary<string, object> {
                        { "id", f.Id }, { "name", Lookup(names, f.Id) }, { "position", V3(f.World) },
                        { "scale", V3(f.Item.Scale) }, { "rotation_y", R(f.Item.Rotation.eulerAngles.y) }, { "source_id", (int)f.Item.VegetationSourceID }
                    };
                    if (c.HasValue) e["distance"] = R(f.Dist);
                    items.Add(e);
                }
                d["items"] = items;
                d["truncated"] = all.Count > limit;
            }
            return d;
        }

        static Dictionary<string, string> NameMap(VegetationSystemPro vs)
        {
            var m = new Dictionary<string, string>();
            foreach (var p in vs.VegetationPackageProList) foreach (var i in p.VegetationInfoList) if (!m.ContainsKey(i.VegetationItemID)) m[i.VegetationItemID] = i.Name;
            return m;
        }
        static string Lookup(Dictionary<string, string> m, string id) { string n; return m.TryGetValue(id, out n) ? n : null; }

        // ------------------------------------------------------------------ ground
        struct GroundHit { public bool Hit; public Vector3 Point; public Vector3 Normal; public string Collider; }

        static GroundHit Ground(VegetationSystemPro vs, float x, float z)
        {
            float top = vs.VegetationSystemBounds.max.y + 1000f;
            var hits = Physics.RaycastAll(new Vector3(x, top, z), Vector3.down, top - vs.VegetationSystemBounds.min.y + 2000f, ~0, QueryTriggerInteraction.Ignore);
            GroundHit best = new GroundHit();
            float bestY = float.NegativeInfinity;
            foreach (var h in hits)
            {
                bool terrain = (h.collider is MeshCollider && vs.meshTerrainColliders.Contains((MeshCollider)h.collider)) || MenuSystem.isTerrainHit(h.collider);
                if (!terrain) continue;
                if (h.point.y > bestY) { bestY = h.point.y; best = new GroundHit { Hit = true, Point = h.point, Normal = h.normal, Collider = h.collider.name }; }
            }
            return best;
        }

        object CmdGround(Dictionary<string, object> a)
        {
            var vs = VSys();
            if (vs == null) throw new Exception("Vegetation system not found (load a course first)");
            var pts = MiniJson.Arr(MiniJson.Get(a, "points"));
            if (pts.Count == 0 && MiniJson.Has(a, "x")) pts.Add(a);
            var outl = new List<object>();
            foreach (var po in pts)
            {
                var p = MiniJson.Obj(po);
                float x = (float)MiniJson.Num(p, "x", 0), z = (float)MiniJson.Num(p, "z", 0);
                var g = Ground(vs, x, z);
                var e = new Dictionary<string, object> { { "x", x }, { "z", z }, { "hit", g.Hit } };
                if (g.Hit) { e["y"] = R(g.Point.y); e["surface"] = g.Collider; e["slope_deg"] = R(Vector3.Angle(g.Normal, Vector3.up)); }
                outl.Add(e);
            }
            var d = Ok(); d["points"] = outl; return d;
        }

        // ------------------------------------------------------------------ plant / remove / move
        static System.Random _rng = new System.Random();
        static float Rand(float a, float b) { return a + (float)_rng.NextDouble() * (b - a); }

        object CmdPlant(Dictionary<string, object> a)
        {
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            var list = MiniJson.Arr(MiniJson.Get(a, "items"));
            if (list.Count == 0) throw new Exception("'items' is empty");
            var b = vs.VegetationSystemBounds;
            var op = new Op { Kind = "plant" };
            var results = new List<object>();
            int okCount = 0;
            foreach (var o in list)
            {
                var p = MiniJson.Obj(o);
                var r = new Dictionary<string, object>();
                results.Add(r);
                string id = MiniJson.Str(p, "item_id");
                var info = id != null ? vs.GetVegetationItemInfo(id) : null;
                if (info == null) { r["ok"] = false; r["error"] = "Unknown item_id " + id; continue; }
                float x = (float)MiniJson.Num(p, "x", float.NaN), z = (float)MiniJson.Num(p, "z", float.NaN);
                if (float.IsNaN(x) || float.IsNaN(z)) { r["ok"] = false; r["error"] = "x and z are required"; continue; }
                if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) { r["ok"] = false; r["error"] = "Outside the vegetation area"; continue; }
                Vector3 pos;
                string surface = null;
                bool snap = MiniJson.Bool(p, "snap", !MiniJson.Has(p, "y"));
                if (!snap && MiniJson.Has(p, "y"))
                    pos = new Vector3(x, (float)MiniJson.Num(p, "y", 0), z);
                else
                {
                    var g = Ground(vs, x, z);
                    if (!g.Hit) { r["ok"] = false; r["error"] = "No terrain under this point"; continue; }
                    pos = g.Point; surface = g.Collider;
                }
                pos += info.Offset;
                pos.y += (float)MiniJson.Num(p, "y_offset", 0);
                Vector3 scale;
                if (MiniJson.Has(p, "scale_xyz"))
                {
                    var s = MiniJson.Arr(MiniJson.Get(p, "scale_xyz"));
                    scale = new Vector3(Convert.ToSingle(s[0]), Convert.ToSingle(s[1]), Convert.ToSingle(s[2]));
                }
                else
                {
                    float sc = MiniJson.Has(p, "scale") ? (float)MiniJson.Num(p, "scale", 1) : Rand(info.MinScale, info.MaxScale);
                    scale = Vector3.Scale(new Vector3(sc, sc, sc), info.ScaleMultiplier);
                }
                float ry = MiniJson.Has(p, "rotation_y") ? (float)MiniJson.Num(p, "rotation_y", 0) : Rand(0f, 360f);
                Quaternion rot = Quaternion.Euler(0f, ry, 0f);
                st.AddVegetationItemInstance(id, pos, scale, rot, false, SourceId, 1f, true);
                op.Added.Add(new ItemRec { Id = id, Pos = pos, Scale = scale, Rot = rot, Src = SourceId, Falloff = 1f });
                okCount++;
                r["ok"] = true; r["item_id"] = id; r["name"] = info.Name; r["position"] = V3(pos); r["scale"] = R(scale.y); r["rotation_y"] = R(ry);
                if (surface != null) r["surface"] = surface;
            }
            Finish(st, op);
            var d = Ok(); d["planted"] = okCount; d["failed"] = list.Count - okCount; d["results"] = results; return d;
        }

        static void RemoveExact(PersistentVegetationStorage st, string id, Vector3 world)
        {
            st.RemoveVegetationItemInstance(id, world, 0.01f, true);
        }

        object CmdRemove(Dictionary<string, object> a)
        {
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            Vector2? c; float rad; Rect? rect; Area(a, out c, out rad, out rect);
            if (!c.HasValue && !rect.HasValue) throw new Exception("Give x/z (+radius) or min_x/min_z/max_x/max_z");
            if (c.HasValue && !MiniJson.Has(a, "radius")) rad = 1.0f;
            var ids = IdSet(a);
            int max = (int)MiniJson.Num(a, "max_count", 1000000);
            bool dry = MiniJson.Bool(a, "dry_run", false);
            var found = Enumerate(st, vs, ids, c, rad, rect).ToList();
            if (c.HasValue) found.Sort((p, q) => p.Dist.CompareTo(q.Dist));
            found = found.Take(max).ToList();
            var names = NameMap(vs);
            var op = new Op { Kind = "remove" };
            var outl = new List<object>();
            foreach (var f in found)
            {
                if (!dry) RemoveExact(st, f.Id, f.World);
                op.Removed.Add(new ItemRec { Id = f.Id, Pos = f.World, Scale = f.Item.Scale, Rot = f.Item.Rotation, Src = f.Item.VegetationSourceID, Falloff = f.Item.DistanceFalloff });
                if (outl.Count < 200) outl.Add(new Dictionary<string, object> { { "id", f.Id }, { "name", Lookup(names, f.Id) }, { "position", V3(f.World) } });
            }
            if (!dry) Finish(st, op);
            var d = Ok(); d[dry ? "would_remove" : "removed"] = found.Count; d["items"] = outl; return d;
        }

        object CmdMove(Dictionary<string, object> a)
        {
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            var from = new Vector2((float)MiniJson.Num(a, "x", float.NaN), (float)MiniJson.Num(a, "z", float.NaN));
            if (float.IsNaN(from.x) || float.IsNaN(from.y)) throw new Exception("x and z of the existing item are required");
            float rad = (float)MiniJson.Num(a, "radius", 1.0);
            var found = Enumerate(st, vs, IdSet(a), from, rad, null).OrderBy(f => f.Dist).FirstOrDefault();
            if (found.Id == null) throw new Exception("No planted item within " + rad + " m of that point");
            float nx = (float)MiniJson.Num(a, "new_x", found.World.x), nz = (float)MiniJson.Num(a, "new_z", found.World.z);
            var info = vs.GetVegetationItemInfo(found.Id);
            Vector3 np;
            var g = Ground(vs, nx, nz);
            if (g.Hit) np = g.Point + (info != null ? info.Offset : Vector3.zero);
            else np = new Vector3(nx, found.World.y, nz);
            np.y += (float)MiniJson.Num(a, "y_offset", 0);
            Vector3 scale = found.Item.Scale;
            if (MiniJson.Has(a, "scale")) { float s = (float)MiniJson.Num(a, "scale", 1); scale = info != null ? Vector3.Scale(new Vector3(s, s, s), info.ScaleMultiplier) : new Vector3(s, s, s); }
            Quaternion rot = found.Item.Rotation;
            if (MiniJson.Has(a, "rotation_y")) rot = Quaternion.Euler(0f, (float)MiniJson.Num(a, "rotation_y", 0), 0f);
            var op = new Op { Kind = "move" };
            RemoveExact(st, found.Id, found.World);
            op.Removed.Add(new ItemRec { Id = found.Id, Pos = found.World, Scale = found.Item.Scale, Rot = found.Item.Rotation, Src = found.Item.VegetationSourceID, Falloff = found.Item.DistanceFalloff });
            st.AddVegetationItemInstance(found.Id, np, scale, rot, false, found.Item.VegetationSourceID, found.Item.DistanceFalloff, true);
            op.Added.Add(new ItemRec { Id = found.Id, Pos = np, Scale = scale, Rot = rot, Src = found.Item.VegetationSourceID, Falloff = found.Item.DistanceFalloff });
            Finish(st, op);
            var d = Ok(); d["item_id"] = found.Id; d["from"] = V3(found.World); d["to"] = V3(np); d["scale"] = R(scale.y); d["rotation_y"] = R(rot.eulerAngles.y); return d;
        }

        object CmdUndo(Dictionary<string, object> a)
        {
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            int steps = Math.Max(1, (int)MiniJson.Num(a, "steps", 1));
            var undone = new List<object>();
            for (int s = 0; s < steps && _history.Count > 0; s++)
            {
                var op = _history[_history.Count - 1];
                _history.RemoveAt(_history.Count - 1);
                foreach (var r in op.Added) RemoveExact(st, r.Id, r.Pos);
                foreach (var r in op.Removed) st.AddVegetationItemInstance(r.Id, r.Pos, r.Scale, r.Rot, false, r.Src, r.Falloff, true);
                undone.Add(new Dictionary<string, object> { { "kind", op.Kind }, { "added_reverted", op.Added.Count }, { "removed_restored", op.Removed.Count } });
            }
            st.PersistentVegetationStoragePackage.SetInstanceInfoDirty();
            var d = Ok(); d["undone"] = undone; d["remaining_steps"] = _history.Count; return d;
        }

        void Finish(PersistentVegetationStorage st, Op op)
        {
            st.PersistentVegetationStoragePackage.SetInstanceInfoDirty();
            if (op.Added.Count + op.Removed.Count > 0) { _history.Add(op); if (_history.Count > 100) _history.RemoveAt(0); }
        }

        // ------------------------------------------------------------------ camera / screenshot / save
        static Camera Cam()
        {
            var cp = MenuSystem.CameraParent;
            if (cp != null) { var c = cp.GetComponentInChildren<Camera>(); if (c != null && c.enabled) return c; }
            return Camera.main;
        }

        object CmdGetCamera()
        {
            var d = Ok();
            var cp = MenuSystem.CameraParent;
            var cam = Cam();
            if (cp != null) { d["rig_position"] = V3(cp.transform.position); d["rig_euler"] = V3(cp.transform.eulerAngles); }
            if (cam != null) { d["camera_position"] = V3(cam.transform.position); d["camera_euler"] = V3(cam.transform.eulerAngles); d["fov"] = R(cam.fieldOfView); }
            return d;
        }

        object CmdSetCamera(Dictionary<string, object> a)
        {
            var cp = MenuSystem.CameraParent;
            Transform t = cp != null ? cp.transform : (Cam() != null ? Cam().transform : null);
            if (t == null) throw new Exception("Camera not found");
            Vector3 pos = t.position;
            if (MiniJson.Has(a, "x")) pos.x = (float)MiniJson.Num(a, "x", 0);
            if (MiniJson.Has(a, "z")) pos.z = (float)MiniJson.Num(a, "z", 0);
            if (MiniJson.Has(a, "y")) pos.y = (float)MiniJson.Num(a, "y", 0);
            else if (MiniJson.Has(a, "height_above_ground"))
            {
                var vs = VSys();
                var g = vs != null ? Ground(vs, pos.x, pos.z) : new GroundHit();
                pos.y = (g.Hit ? g.Point.y : pos.y) + (float)MiniJson.Num(a, "height_above_ground", 2);
            }
            t.position = pos;
            if (MiniJson.Has(a, "look_at_x") && MiniJson.Has(a, "look_at_z"))
            {
                float lx = (float)MiniJson.Num(a, "look_at_x", 0), lz = (float)MiniJson.Num(a, "look_at_z", 0);
                float ly;
                if (MiniJson.Has(a, "look_at_y")) ly = (float)MiniJson.Num(a, "look_at_y", 0);
                else { var vs = VSys(); var g = vs != null ? Ground(vs, lx, lz) : new GroundHit(); ly = g.Hit ? g.Point.y + 1.5f : pos.y; }
                t.rotation = Quaternion.LookRotation(new Vector3(lx, ly, lz) - pos, Vector3.up);
            }
            else if (MiniJson.Has(a, "yaw") || MiniJson.Has(a, "pitch"))
            {
                var e = t.eulerAngles;
                t.rotation = Quaternion.Euler((float)MiniJson.Num(a, "pitch", e.x), (float)MiniJson.Num(a, "yaw", e.y), 0f);
            }
            return CmdGetCamera();
        }

        object CmdScreenshot(Dictionary<string, object> a)
        {
            var cam = Cam();
            if (cam == null) throw new Exception("Camera not found");
            int w = (int)MiniJson.Num(a, "width", 1280), h = (int)MiniJson.Num(a, "height", 720);
            w = Mathf.Clamp(w, 64, 3840); h = Mathf.Clamp(h, 64, 2160);
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevT = cam.targetTexture; var prevA = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
            }
            finally { cam.targetTexture = prevT; RenderTexture.active = prevA; RenderTexture.ReleaseTemporary(rt); }
            byte[] png = ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            var d = Ok(); d["width"] = w; d["height"] = h; d["png_base64"] = Convert.ToBase64String(png);
            d["camera_position"] = V3(cam.transform.position); d["camera_euler"] = V3(cam.transform.eulerAngles);
            return d;
        }

        // ------------------------------------------------------------------ read-only inspection of biomes & masks
        static bool Simple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Color) || t == typeof(decimal);
        }
        static object SimpleVal(object v)
        {
            if (v == null) return null;
            if (v is Vector3) return V3((Vector3)v);
            if (v is Vector2) { var q = (Vector2)v; return new List<object> { R(q.x), R(q.y) }; }
            if (v is Color) { var c = (Color)v; return new List<object> { R(c.r), R(c.g), R(c.b), R(c.a) }; }
            if (v is float) return R((float)v);
            if (v.GetType().IsEnum) return v.ToString();
            return v;
        }
        // Dumps public instance fields that are simple values; lists of simple-field objects one level deep.
        static Dictionary<string, object> Dump(object o, int depth = 1)
        {
            var d = new Dictionary<string, object>();
            if (o == null) return d;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (Simple(f.FieldType)) { d[f.Name] = SimpleVal(v); continue; }
                if (v is AnimationCurve)
                {
                    var keys = new List<object>();
                    foreach (var k in ((AnimationCurve)v).keys) keys.Add(new List<object> { R(k.time), R(k.value) });
                    d[f.Name] = keys; continue;
                }
                if (depth > 0 && v is System.Collections.IList && f.FieldType.IsGenericType)
                {
                    var et = f.FieldType.GetGenericArguments()[0];
                    if (et.IsClass && et != typeof(string) && !typeof(UnityEngine.Object).IsAssignableFrom(et))
                    {
                        var l = new List<object>();
                        foreach (var x in (System.Collections.IList)v) l.Add(Dump(x, depth - 1));
                        if (l.Count > 0) d[f.Name] = l;
                    }
                    else if (Simple(et))
                    {
                        var l = new List<object>();
                        foreach (var x in (System.Collections.IList)v) l.Add(SimpleVal(x));
                        if (l.Count > 0) d[f.Name] = l;
                    }
                }
            }
            return d;
        }

        object CmdListBiomes(Dictionary<string, object> a)
        {
            var vs = VSys();
            if (vs == null) throw new Exception("Vegetation system not found (load a course first)");
            bool rules = MiniJson.Bool(a, "include_rules", true);
            string filter = (MiniJson.Str(a, "filter", "") ?? "").ToLowerInvariant();
            var outl = new List<object>();
            for (int bi = 0; bi < vs.VegetationPackageProList.Count; bi++)
            {
                var pkg = vs.VegetationPackageProList[bi];
                if (filter != "" && (pkg.PackageName ?? "").ToLowerInvariant().IndexOf(filter) < 0) continue;
                var pd = new Dictionary<string, object> {
                    { "index", bi }, { "name", pkg.PackageName }, { "biome_type", pkg.BiomeType.ToString() },
                    { "sort_order", pkg.BiomeSortOrder }
                };
                var groups = new List<object>();
                foreach (var g in pkg.TextureMaskGroupList) groups.Add(Dump(g, 0));
                if (groups.Count > 0) pd["texture_mask_groups"] = groups;
                var items = new List<object>();
                foreach (var it in pkg.VegetationInfoList)
                {
                    if (rules) { var dd = Dump(it, 1); dd.Remove("ShaderControllerSettings"); items.Add(dd); }
                    else items.Add(new Dictionary<string, object> { { "id", it.VegetationItemID }, { "name", it.Name }, { "type", it.VegetationType.ToString() } });
                }
                pd["items"] = items;
                outl.Add(pd);
            }
            var d = Ok(); d["biomes"] = outl; return d;
        }

        static Dictionary<string, object> MaskCommon(Component c, List<Vector3> pts, bool nodes)
        {
            var d = new Dictionary<string, object> { { "object", c.gameObject.name }, { "active", c.gameObject.activeInHierarchy } };
            if (pts.Count > 0)
            {
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue; Vector3 sum = Vector3.zero;
                foreach (var p in pts) { x0 = Math.Min(x0, p.x); z0 = Math.Min(z0, p.z); x1 = Math.Max(x1, p.x); z1 = Math.Max(z1, p.z); sum += p; }
                d["node_count"] = pts.Count;
                d["bbox"] = new List<object> { R(x0), R(z0), R(x1), R(z1) };
                d["center"] = V3(sum / pts.Count);
                double perim = 0, area = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                    perim += Vector2.Distance(new Vector2(p.x, p.z), new Vector2(q.x, q.z));
                    area += (double)p.x * q.z - (double)q.x * p.z;
                }
                d["perimeter_m"] = Math.Round(perim, 1);
                d["area_m2"] = Math.Round(Math.Abs(area) / 2, 1);
                if (nodes) { var l = new List<object>(); foreach (var p in pts) l.Add(V3(p)); d["nodes"] = l; }
            }
            return d;
        }

        static bool Near(List<Vector3> pts, Vector2? c, float rad)
        {
            if (!c.HasValue) return true;
            foreach (var p in pts) if (Vector2.Distance(new Vector2(p.x, p.z), c.Value) <= rad) return true;
            // also include if center is inside the polygon
            return PointInPoly(pts, c.Value);
        }
        static bool PointInPoly(List<Vector3> pts, Vector2 pt)
        {
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                if (((pts[i].z > pt.y) != (pts[j].z > pt.y)) && (pt.x < (pts[j].x - pts[i].x) * (pt.y - pts[i].z) / (pts[j].z - pts[i].z + 1e-9f) + pts[i].x)) inside = !inside;
            }
            return inside;
        }

        object CmdListMasks(Dictionary<string, object> a)
        {
            string kind = (MiniJson.Str(a, "kind", "all") ?? "all").ToLowerInvariant();
            bool nodes = MiniJson.Bool(a, "include_nodes", false);
            string filter = (MiniJson.Str(a, "filter", "") ?? "").ToLowerInvariant();
            int limit = (int)MiniJson.Num(a, "limit", 500);
            Vector2? c; float rad; Rect? rect; Area(a, out c, out rad, out rect);
            var outl = new List<object>();
            var counts = new Dictionary<string, int>();
            if (kind == "all" || kind == "biome")
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<BiomeMaskArea>())
                {
                    if (m == null || !m.gameObject.scene.IsValid()) continue;
                    var pts = m.GetWorldSpaceNodePositions();
                    if (!Near(pts, c, rad)) continue;
                    if (filter != "" && (m.MaskName ?? "").ToLowerInvariant().IndexOf(filter) < 0 && m.BiomeType.ToString().ToLowerInvariant().IndexOf(filter) < 0) continue;
                    int n; counts.TryGetValue("biome", out n); counts["biome"] = n + 1;
                    if (outl.Count >= limit) continue;
                    var d = MaskCommon(m, pts, nodes);
                    d["kind"] = "biome"; d["name"] = m.MaskName; d["biome_type"] = m.BiomeType.ToString();
                    d["blend_distance"] = R(m.BlendDistance); d["use_noise"] = m.UseNoise; d["noise_scale"] = R(m.NoiseScale);
                    d["tight_edges"] = m.tightEdges; d["closed"] = m.ClosedArea;
                    var bc = new List<object>(); foreach (var k in m.BlendCurve.keys) bc.Add(new List<object> { R(k.time), R(k.value) }); d["blend_curve"] = bc;
                    outl.Add(d);
                }
            }
            if (kind == "all" || kind == "vegetation" || kind == "line")
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<VegetationMask>())
                {
                    if (m == null || !m.gameObject.scene.IsValid()) continue;
                    bool isLine = m is VegetationMaskLine;
                    if (kind == "line" && !isLine) continue;
                    if (kind == "vegetation" && isLine) continue;
                    var pts = m.GetWorldSpaceNodePositions();
                    if (!Near(pts, c, rad)) continue;
                    if (filter != "" && (m.MaskName ?? "").ToLowerInvariant().IndexOf(filter) < 0) continue;
                    string k2 = isLine ? "line" : "vegetation";
                    int n; counts.TryGetValue(k2, out n); counts[k2] = n + 1;
                    if (outl.Count >= limit) continue;
                    var d = MaskCommon(m, pts, nodes);
                    d["kind"] = k2; d["name"] = m.MaskName;
                    d["include_mode"] = m.IncludeVegetationType;
                    d["remove"] = new Dictionary<string, object> { { "grass", m.RemoveGrass }, { "plants", m.RemovePlants }, { "trees", m.RemoveTrees }, { "objects", m.RemoveObjects }, { "large_objects", m.RemoveLargeObjects } };
                    d["perimeter_extra"] = new Dictionary<string, object> {
                        { "grass", new List<object>{ R(m.AdditionalGrassPerimiter), R(m.AdditionalGrassPerimiterMax) } },
                        { "plants", new List<object>{ R(m.AdditionalPlantPerimiter), R(m.AdditionalPlantPerimiterMax) } },
                        { "trees", new List<object>{ R(m.AdditionalTreePerimiter), R(m.AdditionalTreePerimiterMax) } } };
                    var vt = new List<object>();
                    foreach (var t in m.VegetationTypeList) vt.Add(new Dictionary<string, object> { { "index", t.Index.ToString() }, { "density", R(t.Density) }, { "size", R(t.Size) } });
                    d["vegetation_types"] = vt;
                    if (isLine) d["line_width"] = R(((VegetationMaskLine)m).LineWidth);
                    if (isLine)
                    {
                        var widths = new List<object>(); bool any = false;
                        foreach (var nd in m.Nodes) { widths.Add(nd.OverrideWidth ? (object)R(nd.CustomWidth) : null); if (nd.OverrideWidth) any = true; }
                        if (any && nodes) d["node_widths"] = widths;
                    }
                    outl.Add(d);
                }
            }
            var r = Ok(); r["counts"] = counts; r["returned"] = outl.Count; r["masks"] = outl; return r;
        }

        // ------------------------------------------------------------------ scene objects & mask creation
        static string PathOf(Transform t) { var sb = new StringBuilder(t.name); while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); } return sb.ToString(); }

        object CmdFindObjects(Dictionary<string, object> a)
        {
            string filter = (MiniJson.Str(a, "filter", "") ?? "").ToLowerInvariant();
            int limit = (int)MiniJson.Num(a, "limit", 50);
            Vector2? c; float rad; Rect? rect; Area(a, out c, out rad, out rect);
            var found = new List<KeyValuePair<float, Dictionary<string, object>>>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                string n = mf.gameObject.name;
                if (filter != "" && n.ToLowerInvariant().IndexOf(filter) < 0) continue;
                var r = mf.GetComponent<Renderer>();
                Bounds b = r != null ? r.bounds : new Bounds(mf.transform.position, Vector3.zero);
                float dist = 0;
                if (c.HasValue)
                {
                    float dx = Math.Max(Math.Max(b.min.x - c.Value.x, 0), c.Value.x - b.max.x);
                    float dz = Math.Max(Math.Max(b.min.z - c.Value.y, 0), c.Value.y - b.max.z);
                    dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist > rad) continue;
                }
                var d = new Dictionary<string, object> {
                    { "name", n }, { "path", PathOf(mf.transform) }, { "active", mf.gameObject.activeInHierarchy },
                    { "bounds_min", V3(b.min) }, { "bounds_max", V3(b.max) },
                    { "vertices", mf.sharedMesh.vertexCount }, { "readable", mf.sharedMesh.isReadable },
                    { "has_collider", mf.GetComponent<Collider>() != null }
                };
                if (c.HasValue) d["distance"] = R(dist);
                found.Add(new KeyValuePair<float, Dictionary<string, object>>(dist, d));
            }
            found.Sort((x, y) => x.Key.CompareTo(y.Key));
            var res = Ok(); res["count"] = found.Count; res["objects"] = found.Take(limit).Select(k => (object)k.Value).ToList(); return res;
        }

        static GameObject FindObj(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) throw new Exception("object name/path required");
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                if (mf.gameObject.name == nameOrPath || PathOf(mf.transform) == nameOrPath) return mf.gameObject;
            throw new Exception("No mesh object named " + nameOrPath);
        }

        // Writes a mesh's world-space vertices (x,y,z) and triangles to a JSON file under BepInEx\\exports.
        object CmdExportMesh(Dictionary<string, object> a)
        {
            var go = FindObj(MiniJson.Str(a, "object"));
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var dir = Path.Combine(Paths.BepInExRootPath, "exports");
            Directory.CreateDirectory(dir);
            string safe = new string(go.name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
            string file = Path.Combine(dir, safe + ".json");
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(MiniJson.Serialize(go.name)).Append(",\"v\":[");
            var vs = mesh.vertices; var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < vs.Length; i++)
            {
                var w = go.transform.TransformPoint(vs[i]);
                if (i > 0) sb.Append(',');
                sb.Append(w.x.ToString("0.###", ci)).Append(',').Append(w.y.ToString("0.###", ci)).Append(',').Append(w.z.ToString("0.###", ci));
            }
            sb.Append("],\"t\":[");
            var t = mesh.triangles;
            for (int i = 0; i < t.Length; i++) { if (i > 0) sb.Append(','); sb.Append(t[i]); }
            sb.Append("]}");
            File.WriteAllText(file, sb.ToString());
            var r = Ok(); r["file"] = file; r["vertices"] = vs.Length; r["triangles"] = t.Length / 3; return r;
        }

        // Same point-thinning Arborist uses for "mask from mesh".
        static List<Vector3> ArboristSimplify(Vector3[] pts)
        {
            var sel = new List<Vector3> { pts[0] };
            float straight = 10f, last = 0f, curveTh = 0.5f, maxCurv = 5f;
            for (int i = 1; i < pts.Length - 1; i++)
            {
                Vector3 n1 = (pts[i] - pts[i - 1]).normalized, n2 = (pts[i + 1] - pts[i]).normalized;
                float ang = Vector3.Angle(n1, n2);
                float f = Mathf.Clamp(ang / curveTh, 0f, maxCurv);
                float need = straight * (1f / (f + 0.1f));
                if (last >= need) { sel.Add(pts[i]); last = 0f; }
                else last += Vector3.Distance(pts[i - 1], pts[i]);
            }
            sel.Add(pts[pts.Length - 1]);
            return sel;
        }

        static double PolyArea(List<Vector3> p)
        {
            double s = 0; for (int i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; s += (double)p[i].x * q.z - (double)q.x * p[i].z; }
            return Math.Abs(s) / 2;
        }

        // Boundary tracer: welds vertices within 1 cm, keeps edges used by exactly one triangle, then walks
        // the (undirected) boundary graph into loops. Tolerates pinch points; open chains are kept as a fallback.
        class LoopStats { public int Verts, Welded, BoundaryEdges, OddVerts, Closed, Open; }
        static List<Vector3[]> FastLoops(Mesh mesh, LoopStats st)
        {
            var verts = mesh.vertices; var tris = mesh.triangles;
            var weld = new int[verts.Length];
            var map = new Dictionary<string, int>();
            var welded = new List<Vector3>();
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                string key = Math.Round(v.x * 100).ToString() + "," + Math.Round(v.y * 100).ToString() + "," + Math.Round(v.z * 100).ToString();
                int id;
                if (!map.TryGetValue(key, out id)) { id = welded.Count; welded.Add(v); map[key] = id; }
                weld[i] = id;
            }
            var count = new Dictionary<long, int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[tris[t + e]], b = weld[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int c; count.TryGetValue(k, out c); count[k] = c + 1;
                }
            var adj = new Dictionary<int, List<int>>();
            var unused = new HashSet<long>();
            foreach (var kv in count)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                if (!adj.ContainsKey(a)) adj[a] = new List<int>();
                if (!adj.ContainsKey(b)) adj[b] = new List<int>();
                adj[a].Add(b); adj[b].Add(a); unused.Add(kv.Key);
            }
            st.Verts = verts.Length; st.Welded = welded.Count; st.BoundaryEdges = unused.Count;
            st.OddVerts = adj.Count(kv => kv.Value.Count % 2 == 1);
            Func<int, int, long> K = (x, y) => x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
            var closed = new List<Vector3[]>(); var open = new List<Vector3[]>();
            // start open walks at odd vertices first so chains are traced end to end
            var starts = adj.Keys.OrderBy(v => adj[v].Count % 2 == 1 ? 0 : 1).ToList();
            foreach (int s0 in starts)
            {
                while (true)
                {
                    int nb = adj[s0].FirstOrDefault(n => unused.Contains(K(s0, n)));
                    if (!unused.Contains(K(s0, nb))) break;
                    var loop = new List<Vector3> { welded[s0] };
                    int prev = s0, cur = nb; unused.Remove(K(s0, nb));
                    int guard = 0;
                    while (cur != s0 && guard++ < 2000000)
                    {
                        loop.Add(welded[cur]);
                        int nx = -1;
                        foreach (var n in adj[cur]) if (unused.Contains(K(cur, n))) { nx = n; break; }
                        if (nx < 0) break;
                        unused.Remove(K(cur, nx)); prev = cur; cur = nx;
                    }
                    if (cur == s0 && loop.Count >= 3) closed.Add(loop.ToArray());
                    else if (loop.Count >= 3) open.Add(loop.ToArray());
                }
            }
            st.Closed = closed.Count; st.Open = open.Count;
            if (closed.Count == 0) return open;
            return closed;
        }

        static LoopStats _lastLoopStats;
        static List<List<Vector3>> ObjectLoops(GameObject go, bool simplify)
        {
            var loops = new List<List<Vector3>>();
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) throw new Exception(go.name + " has no mesh");
            var stats = new LoopStats();
            _lastLoopStats = stats;
            foreach (var l in FastLoops(mf.sharedMesh, stats))
            {
                if (l == null || l.Length < 3) continue;
                var world = l.Select(v => go.transform.TransformPoint(v)).ToArray();
                loops.Add(simplify ? ArboristSimplify(world) : world.ToList());
            }
            loops.Sort((x, y) => PolyArea(y).CompareTo(PolyArea(x)));
            return loops;
        }

        object CmdMeshOutline(Dictionary<string, object> a)
        {
            var go = FindObj(MiniJson.Str(a, "object"));
            bool simplify = MiniJson.Bool(a, "simplify", true);
            bool pts = MiniJson.Bool(a, "include_points", true);
            var loops = ObjectLoops(go, simplify);
            var outl = new List<object>();
            foreach (var l in loops)
            {
                var d = MaskCommon(go.transform, l, pts);
                d.Remove("object"); d.Remove("active");
                outl.Add(d);
            }
            var r = Ok(); r["object"] = PathOf(go.transform); r["loop_count"] = loops.Count; r["loops"] = outl;
            var stt = _lastLoopStats;
            if (stt != null) r["mesh_stats"] = new Dictionary<string, object> { { "vertices", stt.Verts }, { "welded", stt.Welded }, { "boundary_edges", stt.BoundaryEdges }, { "odd_vertices", stt.OddVerts }, { "closed_loops", stt.Closed }, { "open_chains", stt.Open } };
            return r;
        }

        // Builds a world-space polygon from "points" ([[x,z] or [x,y,z]]) or "from_object" (+loop_index), then optional "inflate" meters.
        static List<Vector3> PolygonFrom(Dictionary<string, object> a, out string source)
        {
            List<Vector3> poly;
            if (MiniJson.Has(a, "from_object"))
            {
                var go = FindObj(MiniJson.Str(a, "from_object"));
                var loops = ObjectLoops(go, true);
                int li = (int)MiniJson.Num(a, "loop_index", 0);
                if (li < 0 || li >= loops.Count) throw new Exception("Object has " + loops.Count + " outline loops; loop_index out of range");
                poly = loops[li];
                source = go.name;
            }
            else
            {
                poly = new List<Vector3>();
                foreach (var po in MiniJson.Arr(MiniJson.Get(a, "points")))
                {
                    var l = MiniJson.Arr(po);
                    if (l.Count == 2) poly.Add(new Vector3(Convert.ToSingle(l[0]), 0, Convert.ToSingle(l[1])));
                    else if (l.Count >= 3) poly.Add(new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])));
                }
                source = "points";
            }
            if (poly.Count < 3) throw new Exception("Need at least 3 outline points");
            double inflate = MiniJson.Num(a, "inflate", 0);
            if (Math.Abs(inflate) > 1e-6)
            {
                const float k = 10000f;   // same fixed-point trick Arborist uses for its grow/shrink buttons
                var scaled = poly.Select(p => new Vector3(p.x * k, 0, p.z * k)).ToList();
                var inf = AwesomeTechnologies.Utility.PolygonUtility.InflatePolygon(scaled, inflate * k, true);
                if (inf.Count < 3) throw new Exception("Inflate produced an empty polygon");
                poly = inf.Select(p => new Vector3(p.x / k, 0, p.z / k)).ToList();
            }
            return poly;
        }

        static readonly FieldInfo FVegeParent = typeof(MenuSystem).GetField("vegemasksparent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static GameObject VegeParent() { var g = FVegeParent != null ? FVegeParent.GetValue(null) as GameObject : null; return g ?? GameObject.Find("VegetationMasks"); }
        static GameObject BiomeParent() { return MenuSystem.biomemasksparent ?? GameObject.Find("BiomeMasks"); }

        readonly List<GameObject> _createdMasks = new List<GameObject>();

        object CmdCreateVegMask(Dictionary<string, object> a)
        {
            var mm = MenuSystem.mainmenu; var vs = VSys();
            if (mm == null || vs == null || mm.vegeMaskPrefab == null) throw new Exception("Arborist menu/vegetation system not ready");
            string src; var poly = PolygonFrom(a, out src);
            string name = MiniJson.Str(a, "name", "Claude-VegeMask");
            var parent = VegeParent(); if (parent == null) throw new Exception("VegetationMasks parent not found");
            var go = UnityEngine.Object.Instantiate(mm.vegeMaskPrefab);
            go.transform.position = poly[0];
            go.transform.SetParent(parent.transform);
            var m = go.GetComponent<VegetationMaskArea>();
            if (m.arbVegetationSystemPro == null) m.arbVegetationSystemPro = vs;
            while (m.Nodes.Count > 0) m.DeleteNode(m.Nodes[0]);
            foreach (var p in poly) m.AddNodeToEndNoLines(p);
            m.MaskName = name; go.name = name;
            m.RemoveGrass = MiniJson.Bool(a, "remove_grass", true);
            m.RemovePlants = MiniJson.Bool(a, "remove_plants", true);
            m.RemoveTrees = MiniJson.Bool(a, "remove_trees", false);
            m.RemoveObjects = MiniJson.Bool(a, "remove_objects", false);
            m.RemoveLargeObjects = MiniJson.Bool(a, "remove_large_objects", false);
            m.GrassTightEdges = m.PlantTightEdges = m.TreeTightEdges = m.ObjectsTightEdges = m.LargeObjectsTightEdges = false;
            m.AdditionalGrassPerimiter = m.AdditionalGrassPerimiterMax = 0;
            m.AdditionalPlantPerimiter = m.AdditionalPlantPerimiterMax = 0;
            m.AdditionalTreePerimiter = m.AdditionalTreePerimiterMax = 0;
            m.AdditionalObjectPerimiter = m.AdditionalObjectPerimiterMax = 0;
            m.AdditionalLargeObjectPerimiter = m.AdditionalLargeObjectPerimiterMax = 0;
            m.PositionNodes(vs.meshTerrainColliders, vs);
            m.RedrawNodeLines();
            m.UpdateVegetationMask();
            HideMaskVisuals(go, m.Nodes.Select(n => n.Marker));
            _createdMasks.Add(go);
            var r = Ok(); r["name"] = name; r["source"] = src;
            var info = MaskCommon(m, m.GetWorldSpaceNodePositions(), MiniJson.Bool(a, "include_nodes", false));
            foreach (var kv in info) r[kv.Key] = kv.Value;
            return r;
        }

        static BiomeType ParseBiome(VegetationSystemPro vs, string b)
        {
            if (string.IsNullOrEmpty(b)) throw new Exception("biome required (biome name like 'lake edges' or type like OPCD_Biome5)");
            foreach (var p in vs.VegetationPackageProList) if (string.Equals(p.PackageName, b, StringComparison.OrdinalIgnoreCase)) return p.BiomeType;
            return (BiomeType)Enum.Parse(typeof(BiomeType), b, true);
        }

        object CmdCreateBiomeMask(Dictionary<string, object> a)
        {
            var mm = MenuSystem.mainmenu; var vs = VSys();
            if (mm == null || vs == null || mm.biomeMaskPrefab == null) throw new Exception("Arborist menu/vegetation system not ready");
            var bt = ParseBiome(vs, MiniJson.Str(a, "biome"));
            string src; var poly = PolygonFrom(a, out src);
            string name = MiniJson.Str(a, "name", "Claude-BiomeMask");
            var parent = BiomeParent(); if (parent == null) throw new Exception("BiomeMasks parent not found");
            var go = UnityEngine.Object.Instantiate(mm.biomeMaskPrefab);
            go.transform.position = poly[0];
            go.transform.parent = parent.transform;
            var m = go.GetComponent<BiomeMaskArea>();
            if (m.arbVegetationSystemPro == null) m.arbVegetationSystemPro = vs;
            // Like Arborist's mask-from-mesh: keep the template's first node at the origin, drop any extras, append the rest.
            while (m.Nodes.Count > 1) m.DeleteNode(m.Nodes[m.Nodes.Count - 1]);
            if (m.Nodes.Count == 1) { m.Nodes[0].Position = Vector3.zero; if (m.Nodes[0].Marker != null) m.Nodes[0].Marker.transform.localPosition = Vector3.zero; }
            for (int i = (m.Nodes.Count == 1 ? 1 : 0); i < poly.Count; i++) m.AddNodeToEndNoLines(poly[i]);
            m.MaskName = name; go.name = name;
            m.BiomeType = bt;
            m.BlendDistance = (float)MiniJson.Num(a, "blend_distance", 0);
            m.UseNoise = MiniJson.Bool(a, "use_noise", true);
            m.NoiseScale = (float)MiniJson.Num(a, "noise_scale", 20);
            m.tightEdges = MiniJson.Bool(a, "tight_edges", false);
            string copyFrom = MiniJson.Str(a, "copy_curves_from");
            if (copyFrom != null)
            {
                var srcMask = Resources.FindObjectsOfTypeAll<BiomeMaskArea>().FirstOrDefault(x => x != null && x.MaskName == copyFrom && x.gameObject.scene.IsValid());
                if (srcMask != null)
                {
                    m.BlendCurve = new AnimationCurve(srcMask.BlendCurve.keys);
                    m.InverseBlendCurve = new AnimationCurve(srcMask.InverseBlendCurve.keys);
                    m.TextureBlendCurve = new AnimationCurve(srcMask.TextureBlendCurve.keys);
                }
            }
            m.PositionNodes(vs.meshTerrainColliders, vs);
            m.RedrawNodeLines();
            m.UpdateBiomeMask();
            var markers = m.Nodes.Select(n => n.Marker).ToList();
            HideMaskVisuals(go, markers);
            var blend = go.transform.Find("Blend");
            if (blend != null) { var lr = blend.GetComponent<LineRenderer>(); if (lr != null) lr.enabled = false; }
            var label = go.transform.Find("Label"); if (label != null) label.gameObject.SetActive(false);
            _createdMasks.Add(go);
            var r = Ok(); r["name"] = name; r["source"] = src; r["biome_type"] = bt.ToString();
            var info = MaskCommon(m, m.GetWorldSpaceNodePositions(), MiniJson.Bool(a, "include_nodes", false));
            foreach (var kv in info) r[kv.Key] = kv.Value;
            return r;
        }

        static void HideMaskVisuals(GameObject go, IEnumerable<GameObject> markers)
        {
            var lr = go.GetComponent<LineRenderer>(); if (lr != null) lr.enabled = false;
            foreach (var mk in markers) if (mk != null) mk.SetActive(false);
        }

        object CmdDeleteMasks(Dictionary<string, object> a)
        {
            // Only masks created by this bridge (this Arborist session) can be deleted here.
            var names = MiniJson.Arr(MiniJson.Get(a, "names")).Select(x => Convert.ToString(x)).ToList();
            bool all = MiniJson.Bool(a, "all_created", false);
            var vs = VSys();
            var deleted = new List<object>();
            foreach (var go in _createdMasks.ToList())
            {
                if (go == null) { _createdMasks.Remove(go); continue; }
                if (!all && !names.Contains(go.name)) continue;
                var bm = go.GetComponent<BiomeMaskArea>();
                var vm = go.GetComponent<VegetationMask>();
                deleted.Add(go.name);
                if (bm != null) bm.ClearNodes();
                if (vm != null) vm.ClearNodes();
                UnityEngine.Object.DestroyImmediate(go);
                _createdMasks.Remove(go);
            }
            if (vs != null) vs.RefreshVegetationSystem();
            var r = Ok(); r["deleted"] = deleted; r["remaining_created"] = _createdMasks.Where(g => g != null).Select(g => (object)g.name).ToList(); return r;
        }

        // ------------------------------------------------------------------ post-processing (Unity PPv2 profile Arborist edits)
        // Only parameters that Arborist writes to arborist_postprocess.dat (so they reach GSPro). Textures excluded.
        static readonly Dictionary<string, string[]> PPSaved = new Dictionary<string, string[]> {
            { "AmbientOcclusion", new[] { "ambientOnly", "color", "intensity", "mode", "thicknessModifier" } },
            { "Bloom", new[] { "anamorphicRatio", "clamp", "color", "diffusion", "dirtIntensity", "fastMode", "intensity", "softKnee", "threshold" } },
            { "AutoExposure", new[] { "eyeAdaptation", "filtering", "keyValue", "maxLuminance", "minLuminance", "speedDown", "speedUp" } },
            { "ChromaticAberration", new[] { "fastMode", "intensity" } },
            { "ColorGrading", new[] { "brightness", "colorFilter", "contrast", "gain", "gamma", "gradingMode", "hueShift", "ldrLutContribution", "lift", "mixerBlueOutBlueIn", "mixerBlueOutGreenIn", "mixerBlueOutRedIn", "mixerGreenOutBlueIn", "mixerGreenOutGreenIn", "mixerGreenOutRedIn", "mixerRedOutBlueIn", "mixerRedOutGreenIn", "mixerRedOutRedIn", "postExposure", "saturation", "temperature", "tint", "tonemapper" } },
            { "DepthOfField", new[] { "aperture", "focalLength", "focusDistance", "kernelSize" } },
            { "Grain", new[] { "colored", "intensity", "lumContrib", "size" } },
            { "LensDistortion", new[] { "centerX", "centerY", "intensity", "intensityX", "intensityY", "scale" } },
            { "MotionBlur", new[] { "sampleCount", "shutterAngle" } },
            { "ScreenSpaceReflections", new[] { "distanceFade", "maximumIterationCount", "maximumMarchDistance", "preset", "resolution", "thickness", "vignette" } },
            { "Vignette", new[] { "center", "color", "intensity", "mode", "opacity", "rounded", "roundness", "smoothness" } }
        };
        static readonly List<string> _ppHistory = new List<string>();

        static PostProcessProfile PPProfile()
        {
            var p = MenuSystem.postProcessing_Profile ?? Arb_PostProcessing.postProcessing_Profile;
            if (p == null) { var vol = UnityEngine.Object.FindObjectOfType<PostProcessVolume>(); if (vol != null) p = vol.profile; }
            if (p == null) throw new Exception("Post-processing profile not found (load a course first)");
            return p;
        }

        static PostProcessEffectSettings PPEffect(PostProcessProfile prof, string name)
        {
            foreach (var e in prof.settings) if (e != null && string.Equals(e.GetType().Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            throw new Exception("Effect not found: " + name + ". Effects: " + string.Join(", ", prof.settings.Where(x => x != null).Select(x => x.GetType().Name).ToArray()));
        }

        static object PPValueOut(object v)
        {
            if (v == null) return null;
            if (v is Vector4) { var q = (Vector4)v; return new List<object> { R(q.x), R(q.y), R(q.z), R(q.w) }; }
            return SimpleVal(v);
        }

        static Dictionary<string, object> PPDescribe(PostProcessEffectSettings e)
        {
            var d = new Dictionary<string, object> { { "effect", e.GetType().Name }, { "active", e.active }, { "enabled", e.enabled.value } };
            string[] fields; PPSaved.TryGetValue(e.GetType().Name, out fields);
            var ps = new Dictionary<string, object>();
            foreach (var fn in fields ?? new string[0])
            {
                var f = e.GetType().GetField(fn, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) continue;
                var po = f.GetValue(e) as ParameterOverride;
                if (po == null) continue;
                var vf = po.GetType().GetField("value");
                var entry = new Dictionary<string, object> { { "value", PPValueOut(vf != null ? vf.GetValue(po) : null) }, { "override", po.overrideState } };
                var rng = f.GetCustomAttributes(typeof(RangeAttribute), true).FirstOrDefault() as RangeAttribute;
                if (rng != null) entry["range"] = new List<object> { R(rng.min), R(rng.max) };
                var minA = f.GetCustomAttributes(typeof(MinAttribute), true).FirstOrDefault() as MinAttribute;
                if (minA != null) entry["min"] = R(minA.min);
                if (vf != null && vf.FieldType.IsEnum) entry["options"] = Enum.GetNames(vf.FieldType).Cast<object>().ToList();
                ps[fn] = entry;
            }
            d["params"] = ps;
            return d;
        }

        object CmdPPGet(Dictionary<string, object> a)
        {
            var prof = PPProfile();
            string eff = MiniJson.Str(a, "effect");
            var list = new List<object>();
            if (eff != null) list.Add(PPDescribe(PPEffect(prof, eff)));
            else foreach (var e in prof.settings) if (e != null) list.Add(PPDescribe(e));
            var r = Ok(); r["effects"] = list; r["unsaved_changes"] = Arb_PostProcessing.isDirty; r["undo_steps"] = _ppHistory.Count;
            return r;
        }

        static float ToF(object o) { return Convert.ToSingle(o, CultureInfo.InvariantCulture); }

        static object PPConvert(object v, Type t, string name)
        {
            if (t == typeof(float)) return ToF(v);
            if (t == typeof(int)) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            if (t.IsEnum)
            {
                if (v is string) return Enum.Parse(t, (string)v, true);
                return Enum.ToObject(t, Convert.ToInt32(v, CultureInfo.InvariantCulture));
            }
            if (t == typeof(Color))
            {
                if (v is string) { Color c; if (ColorUtility.TryParseHtmlString((string)v, out c)) return c; throw new Exception(name + ": bad color " + v); }
                var l = MiniJson.Arr(v);
                return new Color(ToF(l[0]), ToF(l[1]), ToF(l[2]), l.Count > 3 ? ToF(l[3]) : 1f);
            }
            var arr = MiniJson.Arr(v);
            if (t == typeof(Vector2)) return new Vector2(ToF(arr[0]), ToF(arr[1]));
            if (t == typeof(Vector3)) return new Vector3(ToF(arr[0]), ToF(arr[1]), ToF(arr[2]));
            if (t == typeof(Vector4)) return new Vector4(ToF(arr[0]), ToF(arr[1]), ToF(arr[2]), arr.Count > 3 ? ToF(arr[3]) : 0f);
            throw new Exception(name + ": unsupported type " + t.Name);
        }

        static void PPRefreshUI()
        {
            try { MenuSystem.SetPostProcessingUISettings(); } catch (Exception e) { Debug.Log("[ArboristBridge] PP UI refresh failed: " + e.Message); }
        }

        object CmdPPSet(Dictionary<string, object> a)
        {
            var prof = PPProfile();
            var changes = MiniJson.Arr(MiniJson.Get(a, "changes"));
            if (changes.Count == 0 && MiniJson.Has(a, "effect")) changes.Add(a);
            if (changes.Count == 0) throw new Exception("Give effect + params, or changes: [{effect, active?, params}]");
            // validate everything first so a bad value doesn't leave a half-applied change
            var plan = new List<Action>();
            var applied = new List<object>();
            foreach (var co in changes)
            {
                var c = MiniJson.Obj(co);
                var e = PPEffect(prof, MiniJson.Str(c, "effect"));
                string en = e.GetType().Name;
                string[] allowed; PPSaved.TryGetValue(en, out allowed); allowed = allowed ?? new string[0];
                bool setOverride = MiniJson.Bool(c, "set_override", true);
                if (MiniJson.Has(c, "active")) { bool act = MiniJson.Bool(c, "active", true); plan.Add(() => { e.active = act; e.enabled.value = true; }); applied.Add(en + ".active=" + act); }
                foreach (var kv in MiniJson.Obj(MiniJson.Get(c, "params")))
                {
                    string pn = allowed.FirstOrDefault(x => string.Equals(x, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (pn == null) throw new Exception(en + "." + kv.Key + " is not a setting Arborist saves (allowed: " + string.Join(", ", allowed) + ")");
                    var f = e.GetType().GetField(pn, BindingFlags.Public | BindingFlags.Instance);
                    var po = f.GetValue(e) as ParameterOverride;
                    var vf = po.GetType().GetField("value");
                    object val = PPConvert(kv.Value, vf.FieldType, en + "." + pn);
                    var rng = f.GetCustomAttributes(typeof(RangeAttribute), true).FirstOrDefault() as RangeAttribute;
                    if (rng != null && val is float) val = Mathf.Clamp((float)val, rng.min, rng.max);
                    var minA = f.GetCustomAttributes(typeof(MinAttribute), true).FirstOrDefault() as MinAttribute;
                    if (minA != null && val is float) val = Math.Max((float)val, minA.min);
                    var po2 = po; var vf2 = vf; var val2 = val;
                    plan.Add(() => { vf2.SetValue(po2, val2); if (setOverride) po2.overrideState = true; });
                    applied.Add(en + "." + pn + "=" + MiniJson.Serialize(PPValueOut(val)));
                }
            }
            _ppHistory.Add(Arb_PostProcessing.PostProcessingToJSON(prof));
            if (_ppHistory.Count > 50) _ppHistory.RemoveAt(0);
            foreach (var act in plan) act();
            Arb_PostProcessing.isDirty = true;   // Arborist will ask the user to save when leaving the PP panel / exiting
            PPRefreshUI();
            var touched = changes.Select(co => MiniJson.Str(MiniJson.Obj(co), "effect")).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .Select(n => (object)PPDescribe(PPEffect(prof, n))).ToList();
            var r = Ok(); r["applied"] = applied; r["effects"] = touched; r["undo_steps"] = _ppHistory.Count;
            r["note"] = "Live in Arborist only. Saved to arborist_postprocess.dat only when the user saves post processing in Arborist.";
            return r;
        }

        object CmdPPUndo(Dictionary<string, object> a)
        {
            var prof = PPProfile();
            int steps = Math.Max(1, (int)MiniJson.Num(a, "steps", 1));
            if (_ppHistory.Count == 0) throw new Exception("Nothing to undo");
            string snap = null;
            for (int i = 0; i < steps && _ppHistory.Count > 0; i++) { snap = _ppHistory[_ppHistory.Count - 1]; _ppHistory.RemoveAt(_ppHistory.Count - 1); }
            Arb_PostProcessing.JSONToPostProcessing(snap, prof);
            Arb_PostProcessing.isDirty = true;
            PPRefreshUI();
            var r = Ok(); r["remaining_steps"] = _ppHistory.Count; return r;
        }

        object CmdPPRevert(Dictionary<string, object> a)
        {
            var prof = PPProfile();
            _ppHistory.Add(Arb_PostProcessing.PostProcessingToJSON(prof));
            MenuSystem._LoadPostProcessingFromFile();
            Arb_PostProcessing.isDirty = false;
            PPRefreshUI();
            var r = Ok(); r["message"] = "Post processing reloaded from the course's saved file (pp_undo can bring back what was live)."; return r;
        }

        // Captures the real rendered frame (Vegetation Studio draws per-camera, so an off-screen
        // Camera.Render() misses the vegetation). Optionally hides Arborist's UI for that frame.
        System.Collections.IEnumerator ScreenshotJob(Job job, Dictionary<string, object> a)
        {
            bool hideUi = MiniJson.Bool(a, "hide_ui", true);
            var hidden = new List<Canvas>();
            if (hideUi)
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                    if (c.enabled && c.isRootCanvas && c.renderMode != RenderMode.WorldSpace) { c.enabled = false; hidden.Add(c); }
            yield return null;                       // let one frame render without UI
            yield return new WaitForEndOfFrame();
            try
            {
                int sw = Screen.width, sh = Screen.height;
                var full = new Texture2D(sw, sh, TextureFormat.RGB24, false);
                full.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
                full.Apply();
                int w = (int)MiniJson.Num(a, "width", Math.Min(sw, 1280));
                w = Mathf.Clamp(w, 64, sw);
                int h = Mathf.RoundToInt(w * (float)sh / sw);
                Texture2D outTex = full;
                if (w != sw)
                {
                    var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    var prev = RenderTexture.active;
                    Graphics.Blit(full, rt);
                    RenderTexture.active = rt;
                    outTex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    outTex.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    UnityEngine.Object.Destroy(full);
                }
                byte[] png = ImageConversion.EncodeToPNG(outTex);
                UnityEngine.Object.Destroy(outTex);
                var d = Ok(); d["width"] = w; d["height"] = h; d["png_base64"] = Convert.ToBase64String(png);
                var cam = Cam();
                if (cam != null) { d["camera_position"] = V3(cam.transform.position); d["camera_euler"] = V3(cam.transform.eulerAngles); }
                job.Result = MiniJson.Serialize(d);
            }
            catch (Exception e) { job.Result = Err("Screenshot failed: " + e.Message); }
            finally
            {
                foreach (var c in hidden) if (c != null) c.enabled = true;
                job.Done.Set();
            }
        }

        object CmdSave()
        {
            var mm = MenuSystem.mainmenu;
            if (mm == null) throw new Exception("Arborist menu not found");
            PersistentVegetationStorage st; VegetationSystemPro vs; RequireCourse(out st, out vs);
            mm.SaveJSON(false);
            var d = Ok(); d["message"] = "Save started (Arborist shows 'Saved' when done; a beta-bundle confirmation may appear on screen)"; d["course_path"] = mm.coursepath; return d;
        }
    }
}
