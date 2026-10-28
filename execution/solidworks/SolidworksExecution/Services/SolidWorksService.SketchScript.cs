using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: sketch_script - build a FULLY-DEFINED 2D sketch in one batch.
    // Entities get caller-chosen ids; relations and dimensions reference those ids (object-based
    // selection via Select4, never a coordinate pick), so coincident/overlapping points and tiny
    // segments are selected exactly. All lengths in MILLIMETRES, angles in DEGREES, sketch space.
    public partial class SolidWorksService
    {
        // STATIC: the Web API controller (and with it this service) is created per request, so an
        // instance field would lose the ids between calls. Keyed by document title + sketch name.
        private static readonly Dictionary<string, object> _skIds = new Dictionary<string, object>();
        private static string _skIdsSketch = null;

        private static readonly Dictionary<int, string> _constrainedNames = new Dictionary<int, string>
        {
            { 1, "Unknown" }, { 2, "UnderDefined" }, { 3, "FullyDefined" }, { 4, "OverDefined" },
            { 5, "NoSolution" }, { 6, "InvalidSolution" }, { 7, "AutosolveOff" }
        };

        private static readonly Dictionary<string, string> _relNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "coincident", "sgCOINCIDENT" }, { "tangent", "sgTANGENT" },
            { "horizontal", "sgHORIZONTAL2D" }, { "vertical", "sgVERTICAL2D" },
            { "horizontal_points", "sgHORIZONTALPOINTS2D" }, { "vertical_points", "sgVERTICALPOINTS2D" },
            { "equal", "sgSAMELENGTH" }, { "concentric", "sgCONCENTRIC" }, { "coradial", "sgCORADIAL" },
            { "parallel", "sgPARALLEL" }, { "perpendicular", "sgPERPENDICULAR" }, { "midpoint", "sgATMIDDLE" },
            { "collinear", "sgCOLINEAR" }, { "symmetric", "sgSYMMETRIC" }, { "intersection", "sgATINTERSECT" }
        };

        public ExecutionResponse SketchScript(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                var ops = p?["ops"] as JArray;
                bool resetIds = p?.Value<bool?>("reset_ids") ?? false;
                if (ops == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "ops (array of operations) is required.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var sketch = modelDoc.SketchManager.ActiveSketch;
                if (sketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch or edit_sketch first.");

                string skName = (sketch as IFeature)?.Name ?? "Sketch";
                string skKey = modelDoc.GetTitle() + "|" + skName;
                if (resetIds || _skIdsSketch != skKey)
                {
                    _skIds.Clear();
                    _skIdsSketch = skKey;
                }

                var log = new List<string>();
                string failMsg = null;
                int done = 0;

                bool prevAddToDb = modelDoc.SketchManager.AddToDB;
                bool prevInputDim = _solidWorks.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                // Speed: no per-entity redraw during the batch (234 ops took ~31 s with live graphics).
                var view = modelDoc.ActiveView as IModelView;
                bool prevDisplayWhenAdded = modelDoc.SketchManager.DisplayWhenAdded;
                try { modelDoc.SketchManager.DisplayWhenAdded = false; } catch { }
                try { if (view != null) view.EnableGraphicsUpdate = false; } catch { }
                try
                {
                    for (int i = 0; i < ops.Count; i++)
                    {
                        var o = ops[i] as JObject;
                        string op = o?.Value<string>("op") ?? "?";
                        try
                        {
                            string r = RunSketchOp(modelDoc, o);
                            log.Add($"{i}:{op}:{r}");
                            done++;
                        }
                        catch (Exception ex)
                        {
                            failMsg = $"ops[{i}] ({op}) failed: {ex.Message}";
                            break;
                        }
                    }
                }
                finally
                {
                    try { modelDoc.SketchManager.DisplayWhenAdded = prevDisplayWhenAdded; } catch { }
                    try { if (view != null) view.EnableGraphicsUpdate = true; } catch { }
                    modelDoc.SketchManager.AddToDB = prevAddToDb;
                    _solidWorks.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDim);
                    try { modelDoc.ClearSelection2(true); } catch { }
                    try { modelDoc.GraphicsRedraw2(); } catch { }
                }

                string status = SketchStatus(sketch);
                ExecLog.Write($"sketch_script: {done}/{ops.Count} ops, status={status}" + (failMsg != null ? " | " + failMsg : ""));

                if (failMsg != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_OP_FAILED", $"{failMsg} | {done} earlier ops applied (they remain) | status={status} | ids={string.Join(",", _skIds.Keys)}");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = skName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["ops_done"] = done,
                        ["constrained_status"] = status,
                        ["log"] = new JArray(log.ToArray())
                    },
                    Error = null
                };
                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        private static string SketchStatus(object sketch)
        {
            try
            {
                object r = sketch.GetType().InvokeMember("GetConstrainedStatus", BindingFlags.InvokeMethod, null, sketch, null);
                int s = Convert.ToInt32(r);
                string n;
                return _constrainedNames.TryGetValue(s, out n) ? n : s.ToString();
            }
            catch (Exception ex) { return "unavailable(" + ex.Message + ")"; }
        }

        private static double Mm(JObject o, string key)
        {
            var t = o[key];
            if (t == null) throw new ArgumentException($"missing '{key}'");
            return t.Value<double>() / 1000.0;
        }

        private string RunSketchOp(IModelDoc2 modelDoc, JObject o)
        {
            string op = (o.Value<string>("op") ?? "").ToLowerInvariant();
            string id = o.Value<string>("id");
            bool construction = o.Value<bool?>("construction") ?? false;
            var sm = modelDoc.SketchManager;

            switch (op)
            {
                case "line":
                case "centerline":
                {
                    sm.AddToDB = true;
                    var seg = sm.CreateLine(Mm(o, "x1"), Mm(o, "y1"), 0, Mm(o, "x2"), Mm(o, "y2"), 0) as ISketchSegment;
                    if (seg == null) throw new Exception("CreateLine returned null");
                    if (construction || op == "centerline") seg.ConstructionGeometry = true;
                    Store(id, seg);
                    return id ?? "line";
                }
                case "arc":
                {
                    // dir: +1 counter-clockwise from (x1,y1) to (x2,y2), -1 clockwise.
                    sm.AddToDB = true;
                    short dir = (short)(o.Value<int?>("dir") ?? 1);
                    var seg = sm.CreateArc(Mm(o, "cx"), Mm(o, "cy"), 0, Mm(o, "x1"), Mm(o, "y1"), 0, Mm(o, "x2"), Mm(o, "y2"), 0, dir) as ISketchSegment;
                    if (seg == null) throw new Exception("CreateArc returned null");
                    if (construction) seg.ConstructionGeometry = true;
                    Store(id, seg);
                    return id ?? "arc";
                }
                case "circle":
                {
                    sm.AddToDB = true;
                    var seg = sm.CreateCircleByRadius(Mm(o, "cx"), Mm(o, "cy"), 0, Mm(o, "r")) as ISketchSegment;
                    if (seg == null) throw new Exception("CreateCircleByRadius returned null");
                    if (construction) seg.ConstructionGeometry = true;
                    Store(id, seg);
                    return id ?? "circle";
                }
                case "point":
                {
                    sm.AddToDB = true;
                    var pt = sm.CreatePoint(Mm(o, "x"), Mm(o, "y"), 0) as ISketchPoint;
                    if (pt == null) throw new Exception("CreatePoint returned null");
                    Store(id, pt);
                    return id ?? "point";
                }
                case "construction":
                {
                    foreach (var r in RefList(o))
                    {
                        var seg = Resolve(r) as ISketchSegment;
                        if (seg == null) throw new Exception($"'{r}' is not a segment");
                        seg.ConstructionGeometry = o.Value<bool?>("value") ?? true;
                    }
                    return "ok";
                }
                case "rel":
                {
                    string type = o.Value<string>("type") ?? "";
                    string sg;
                    if (!_relNames.TryGetValue(type, out sg)) sg = type; // allow raw sgXXX names
                    SelectRefs(modelDoc, RefList(o));
                    modelDoc.SketchAddConstraints(sg);
                    return sg;
                }
                case "dim":
                {
                    var refs = RefList(o);
                    SelectRefs(modelDoc, refs);
                    string kind = (o.Value<string>("kind") ?? "smart").ToLowerInvariant();
                    double lx = Mm(o, "lx"), ly = Mm(o, "ly");
                    IDisplayDimension dd;
                    if (kind == "horizontal") dd = modelDoc.AddHorizontalDimension2(lx, ly, 0) as IDisplayDimension;
                    else if (kind == "vertical") dd = modelDoc.AddVerticalDimension2(lx, ly, 0) as IDisplayDimension;
                    else dd = modelDoc.AddDimension2(lx, ly, 0) as IDisplayDimension;
                    if (dd == null) throw new Exception($"dimension not created for [{string.Join(",", refs)}] kind={kind}");
                    var dim = dd.GetDimension2(0) as IDimension;
                    var vt = o["value"];
                    if (dim != null && vt != null)
                    {
                        double v = vt.Value<double>();
                        dim.SystemValue = (kind == "angle") ? v * Math.PI / 180.0 : v / 1000.0;
                    }
                    modelDoc.ClearSelection2(true);
                    return dim != null ? dim.GetNameForSelection() : "dim";
                }
                case "status":
                    return SketchStatus(modelDoc.SketchManager.ActiveSketch);
                default:
                    throw new Exception($"unknown op '{op}' (line|centerline|arc|circle|point|construction|rel|dim|status)");
            }
        }

        private void Store(string id, object entity)
        {
            if (!string.IsNullOrEmpty(id)) _skIds[id] = entity;
        }

        private static List<string> RefList(JObject o)
        {
            var list = new List<string>();
            var arr = o["ents"] as JArray;
            if (arr == null) throw new ArgumentException("missing 'ents'");
            foreach (var t in arr) list.Add(t.Value<string>());
            return list;
        }

        // "L1" -> segment/point; "L1.start" / "L1.end" / "A1.center" -> sketch point; "ORIGIN" -> handled in SelectRefs.
        private object Resolve(string reference)
        {
            string baseId = reference, part = null;
            int dot = reference.LastIndexOf('.');
            if (dot > 0) { baseId = reference.Substring(0, dot); part = reference.Substring(dot + 1).ToLowerInvariant(); }
            object ent;
            if (!_skIds.TryGetValue(baseId, out ent)) throw new Exception($"unknown id '{baseId}'");
            if (part == null) return ent;

            var line = ent as ISketchLine;
            var arc = ent as ISketchArc;
            if (part == "start") return line != null ? line.GetStartPoint2() : arc?.GetStartPoint2();
            if (part == "end") return line != null ? line.GetEndPoint2() : arc?.GetEndPoint2();
            if (part == "center")
            {
                if (arc == null) throw new Exception($"'{baseId}' has no center");
                return arc.GetCenterPoint2();
            }
            throw new Exception($"unknown point '{part}' (start|end|center)");
        }

        private void SelectRefs(IModelDoc2 modelDoc, List<string> refs)
        {
            modelDoc.ClearSelection2(true);
            var selMgr = modelDoc.SelectionManager as ISelectionMgr;
            bool append = false;
            foreach (var r in refs)
            {
                bool ok;
                if (string.Equals(r, "ORIGIN", StringComparison.OrdinalIgnoreCase))
                {
                    ok = modelDoc.Extension.SelectByID2("Point1@\u0418\u0441\u0445\u043e\u0434\u043d\u0430\u044f \u0442\u043e\u0447\u043a\u0430", "EXTSKETCHPOINT", 0, 0, 0, append, 0, null, 0)
                      || modelDoc.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, append, 0, null, 0);
                }
                else
                {
                    object ent = Resolve(r);
                    var sd = selMgr.CreateSelectData() as SelectData;
                    var seg = ent as ISketchSegment;
                    var pt = ent as ISketchPoint;
                    if (seg != null) ok = seg.Select4(append, sd);
                    else if (pt != null) ok = pt.Select4(append, sd);
                    else throw new Exception($"'{r}' resolved to an unsupported object");
                }
                if (!ok) throw new Exception($"could not select '{r}'");
                append = true;
            }
        }
    }
}
