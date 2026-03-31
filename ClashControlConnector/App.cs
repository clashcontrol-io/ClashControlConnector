using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ClashControlConnector.Commands;
using ClashControlConnector.Core;
using ClashControlConnector.Protocol;

namespace ClashControlConnector
{
    public class App : IExternalApplication
    {
        private static WsServer _server;
        private static ExternalEvent _externalEvent;
        private static RevitCommandHandler _commandHandler;
        private static ElementCache _cache = new ElementCache();
        private static ChangeDebouncer _debouncer;
        private static CancellationTokenSource _exportCts;
        private static readonly HashSet<ElementId> _highlightedElementIds = new HashSet<ElementId>();

        public static WsServer Server => _server;
        public static ElementCache Cache => _cache;

        #region Startup / Shutdown

        public Result OnStartup(UIControlledApplication application)
        {
            // Register ExternalEvent for thread marshalling
            _commandHandler = new RevitCommandHandler();
            _externalEvent = ExternalEvent.Create(_commandHandler);
            RevitCommandHandler.Event = _externalEvent;

            // Initialize debouncer (500ms window)
            _debouncer = new ChangeDebouncer(500, ProcessDebouncedChanges);

            // Start WebSocket server
            _server = new WsServer(19780);
            _server.OnMessage += HandleMessage;
            _server.Start();

            // Listen for document events
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;

            // Create ribbon tab & button
            try
            {
                application.CreateRibbonTab("ClashControl");
                var panel = application.CreateRibbonPanel("ClashControl", "Connector");

                var buttonData = new PushButtonData(
                    "ClashControlToggle",
                    "ClashControl\nConnector",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(ToggleCommand).FullName);

                buttonData.ToolTip = "Toggle ClashControl live connection (ws://localhost:19780)";
                panel.AddItem(buttonData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CC] Ribbon error: {ex.Message}");
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
            _exportCts?.Cancel();
            _debouncer?.Dispose();
            _server?.Stop();
            return Result.Succeeded;
        }

        #endregion

        #region Message Router

        private static void HandleMessage(string json)
        {
            try
            {
                var msg = JObject.Parse(json);
                var type = msg["type"]?.ToString();

                switch (type)
                {
                    case "ping":
                        _ = _server.SendAsync(Messages.Pong());
                        break;

                    case "export":
                        var categories = msg["categories"]?.ToObject<List<string>>() ?? new List<string> { "all" };
                        RevitCommandHandler.Enqueue(app => ExportModel(app, categories));
                        break;

                    case "cancel-export":
                        _exportCts?.Cancel();
                        break;

                    case "highlight":
                        var globalIds = msg["globalIds"]?.ToObject<List<string>>() ?? new List<string>();
                        RevitCommandHandler.Enqueue(app => HighlightElements(app, globalIds));
                        break;

                    case "clear-highlights":
                        RevitCommandHandler.Enqueue(app => ClearAllHighlights(app));
                        break;

                    case "push-clashes":
                        var clashes = msg["clashes"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                        var issues = msg["issues"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                        RevitCommandHandler.Enqueue(app => HandlePushClashes(app, clashes, issues));
                        break;
                }
            }
            catch (Exception ex)
            {
                _ = _server.SendAsync(Messages.Error(ex.Message));
            }
        }

        #endregion

        #region Export

        private static void ExportModel(UIApplication uiApp, List<string> categoryFilter)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                _ = _server.SendAsync(Messages.Error("No document open in Revit"));
                return;
            }

            // Cancel any in-progress export
            _exportCts?.Cancel();
            _exportCts = new CancellationTokenSource();
            var ct = _exportCts.Token;

            // Clear cache for fresh export
            _cache.Clear();

            // Collect elements
            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .Where(e => e.Category != null && ShouldExport(e.Category, categoryFilter))
                .Where(e => !IsSkippedCategory(e.Category))
                .ToList();

            // Send model-start
            _ = _server.SendAsync(Messages.ModelStart(doc.Title + ".rvt", elements.Count));

            int batchSize = 50;
            int totalBatches = (int)Math.Ceiling(elements.Count / (double)batchSize);
            if (totalBatches == 0) totalBatches = 1;
            long expressId = 1;
            int elementsSent = 0;

            for (int batchIdx = 0; batchIdx < totalBatches; batchIdx++)
            {
                if (ct.IsCancellationRequested)
                {
                    _ = _server.SendAsync(Messages.ExportCancelled(elementsSent));
                    return;
                }

                var batch = new List<ElementData>();
                int start = batchIdx * batchSize;
                int end = Math.Min(start + batchSize, elements.Count);

                for (int j = start; j < end; j++)
                {
                    try
                    {
                        var el = elements[j];
                        var data = PropertyExporter.ExtractProperties(el, doc);
                        data.ExpressId = expressId++;
                        data.Geometry = GeometryExporter.ExtractGeometry(el);
                        if (data.Geometry == null)
                            data.Geometry = new ElementGeometry();
                        data.Geometry.Color = GetElementColor(el, doc);

                        // Populate cache
                        int geomHash = (data.Geometry.Positions ?? "").GetHashCode();
                        _cache.Add(data.GlobalId, el.Id, geomHash);

                        batch.Add(data);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CC] Skip element {elements[j].Id}: {ex.Message}");
                    }
                }

                // Send batch — stop if client disconnected
                var sent = _server.SendAsync(Messages.ElementBatch(batchIdx, totalBatches, batch)).Result;
                if (!sent)
                {
                    Debug.WriteLine("[CC] Client disconnected during export, aborting");
                    return;
                }

                elementsSent += batch.Count;
            }

            // Build relationships using the now-populated cache
            var (hostIds, hostRelationships, relatedPairs) =
                RelationshipExporter.BuildRelationships(elements, doc, _cache);

            // Collect storeys
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var storeys = levels.Select(l => l.Name).ToList();
            var storeyData = levels.Select(l => (object)new
            {
                name = l.Name,
                elevation = Math.Round(l.Elevation * 304.8, 1)
            }).ToList();

            _ = _server.SendAsync(Messages.ModelEnd(storeys, storeyData, relatedPairs));
        }

        private static float[] GetElementColor(Element element, Document doc)
        {
            var matIds = element.GetMaterialIds(false);
            if (matIds.Count == 0) return new float[] { 0.65f, 0.65f, 0.65f, 1.0f };

            var mat = doc.GetElement(matIds.First()) as Material;
            if (mat == null) return new float[] { 0.65f, 0.65f, 0.65f, 1.0f };

            var color = mat.Color;
            return new float[]
            {
                color.Red / 255f,
                color.Green / 255f,
                color.Blue / 255f,
                1.0f - (mat.Transparency / 100f)
            };
        }

        #endregion

        #region Highlight Management

        private static void ClearAllHighlights(UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null || _highlightedElementIds.Count == 0) return;

            using (var t = new Transaction(doc, "ClashControl: Clear Highlights"))
            {
                t.Start();
                var view = doc.ActiveView;
                var defaultOgs = new OverrideGraphicSettings();

                foreach (var eid in _highlightedElementIds)
                {
                    try { view.SetElementOverrides(eid, defaultOgs); }
                    catch { /* element may have been deleted */ }
                }

                t.Commit();
            }

            _highlightedElementIds.Clear();
        }

        private static void HighlightElements(UIApplication uiApp, List<string> globalIds)
        {
            var uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return;
            var doc = uidoc.Document;

            ClearAllHighlights(uiApp);

            // Resolve GlobalIds to ElementIds via cache (O(1) per lookup)
            var elementIds = new List<ElementId>();
            foreach (var gid in globalIds)
            {
                var eid = _cache.FindByGlobalId(gid);
                if (eid != null) elementIds.Add(eid);
            }

            if (elementIds.Count == 0) return;

            uidoc.Selection.SetElementIds(elementIds);

            using (var t = new Transaction(doc, "ClashControl: Highlight"))
            {
                t.Start();
                var view = doc.ActiveView;

                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(new Color(239, 68, 68));
                ogs.SetSurfaceForegroundPatternColor(new Color(239, 68, 68));

                var solidFill = FindSolidFillPattern(doc);
                if (solidFill != null)
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);

                ogs.SetSurfaceTransparency(0);

                foreach (var eid in elementIds)
                {
                    view.SetElementOverrides(eid, ogs);
                    _highlightedElementIds.Add(eid);
                }

                t.Commit();
            }
        }

        private static void HandlePushClashes(UIApplication uiApp, List<JObject> clashes, List<JObject> issues)
        {
            var uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return;
            var doc = uidoc.Document;

            ClearAllHighlights(uiApp);

            var solidFill = FindSolidFillPattern(doc);
            var errors = new List<string>();

            using (var t = new Transaction(doc, "ClashControl: Mark Clashes"))
            {
                t.Start();
                var view = doc.ActiveView;

                // Hard clash style (red)
                var hardOgs = new OverrideGraphicSettings();
                hardOgs.SetProjectionLineColor(new Color(239, 68, 68));
                hardOgs.SetSurfaceForegroundPatternColor(new Color(239, 68, 68));
                if (solidFill != null) hardOgs.SetSurfaceForegroundPatternId(solidFill.Id);

                // Clearance clash style (amber)
                var clearanceOgs = new OverrideGraphicSettings();
                clearanceOgs.SetProjectionLineColor(new Color(245, 158, 11));
                clearanceOgs.SetSurfaceForegroundPatternColor(new Color(245, 158, 11));
                if (solidFill != null) clearanceOgs.SetSurfaceForegroundPatternId(solidFill.Id);

                // Issue style (purple)
                var issueOgs = new OverrideGraphicSettings();
                issueOgs.SetProjectionLineColor(new Color(139, 92, 246));
                issueOgs.SetSurfaceForegroundPatternColor(new Color(139, 92, 246));
                if (solidFill != null) issueOgs.SetSurfaceForegroundPatternId(solidFill.Id);

                int clashesApplied = 0;

                foreach (var clash in clashes)
                {
                    var clashType = clash["type"]?.ToString() ?? "hard";
                    var ogs = clashType == "clearance" ? clearanceOgs : hardOgs;

                    bool applied = false;
                    applied |= ApplyOverrideByRevitId(view, clash["elementA"], ogs);
                    applied |= ApplyOverrideByRevitId(view, clash["elementB"], ogs);

                    if (applied) clashesApplied++;
                }

                int issuesApplied = 0;

                foreach (var issue in issues)
                {
                    var elementRefs = issue["elementIds"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                    bool applied = false;

                    foreach (var elRef in elementRefs)
                    {
                        applied |= ApplyOverrideByRevitId(view, elRef, issueOgs);
                    }

                    if (applied) issuesApplied++;
                }

                t.Commit();

                _ = _server.SendAsync(Messages.PushClashesAck(clashesApplied, issuesApplied, errors));
            }

            Debug.WriteLine($"[CC] Highlighted {clashes.Count} clashes + {issues.Count} issues");
        }

        private static bool ApplyOverrideByRevitId(View view, JToken elementToken, OverrideGraphicSettings ogs)
        {
            var revitId = elementToken?["revitId"]?.ToObject<long?>() ?? 0;
            if (revitId <= 0) return false;

            var eid = new ElementId(revitId);
            try
            {
                view.SetElementOverrides(eid, ogs);
                _highlightedElementIds.Add(eid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static FillPatternElement FindSolidFillPattern(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
        }

        #endregion

        #region Live Updates (Debounced)

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!_server.IsClientConnected) return;

            _debouncer.Add(
                e.GetModifiedElementIds(),
                e.GetAddedElementIds(),
                e.GetDeletedElementIds()
            );
        }

        private static void ProcessDebouncedChanges(
            HashSet<ElementId> modified, HashSet<ElementId> added, HashSet<ElementId> deleted)
        {
            RevitCommandHandler.Enqueue(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || !_server.IsClientConnected) return;

                // Handle deletions — resolve GlobalIds from cache
                if (deleted.Count > 0)
                {
                    var deletedGids = new List<string>();
                    var deletedRevitIds = new List<long>();

                    foreach (var eid in deleted)
                    {
                        var gid = _cache.FindByElementId(eid);
                        if (gid != null) deletedGids.Add(gid);
                        deletedRevitIds.Add(eid.Value);
                        _cache.Remove(eid);
                    }

                    _ = _server.SendAsync(Messages.ElementUpdateDeleted(deletedGids, deletedRevitIds));
                }

                // Handle added + modified
                var fullUpdateElements = new List<Element>();
                var propertyOnlyElements = new List<Element>();

                foreach (var eid in added)
                {
                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;
                    fullUpdateElements.Add(el);
                }

                foreach (var eid in modified)
                {
                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;

                    var geom = GeometryExporter.ExtractGeometry(el);
                    int newHash = (geom?.Positions ?? "").GetHashCode();

                    if (_cache.HasGeometryChanged(eid, newHash))
                    {
                        fullUpdateElements.Add(el);
                        _cache.UpdateGeometryHash(eid, newHash);
                    }
                    else
                    {
                        propertyOnlyElements.Add(el);
                    }
                }

                // Send full updates (geometry + properties)
                if (fullUpdateElements.Count > 0)
                {
                    var batch = new List<ElementData>();
                    foreach (var el in fullUpdateElements)
                    {
                        try
                        {
                            var data = PropertyExporter.ExtractProperties(el, doc);
                            data.Geometry = GeometryExporter.ExtractGeometry(el);
                            if (data.Geometry == null) data.Geometry = new ElementGeometry();
                            data.Geometry.Color = GetElementColor(el, doc);
                            _cache.Add(data.GlobalId, el.Id, (data.Geometry.Positions ?? "").GetHashCode());
                            batch.Add(data);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[CC] Update skip {el.Id}: {ex.Message}");
                        }
                    }

                    if (batch.Count > 0)
                        _ = _server.SendAsync(Messages.ElementUpdateModified(batch));
                }

                // Send property-only updates (no geometry)
                if (propertyOnlyElements.Count > 0)
                {
                    var propBatch = new List<ElementData>();
                    foreach (var el in propertyOnlyElements)
                    {
                        try
                        {
                            propBatch.Add(PropertyExporter.ExtractProperties(el, doc));
                        }
                        catch { }
                    }

                    if (propBatch.Count > 0)
                        _ = _server.SendAsync(Messages.ElementUpdatePropertiesOnly(propBatch));
                }
            });
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            _cache.Clear();
            if (!_server.IsClientConnected) return;
            _ = _server.SendAsync(Messages.Status(true, e.Document.Title + ".rvt"));
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            _cache.Clear();
            _highlightedElementIds.Clear();
            if (!_server.IsClientConnected) return;
            _ = _server.SendAsync(Messages.Status(true, ""));
        }

        #endregion

        #region Category Filters

        private static readonly HashSet<BuiltInCategory> ExportCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_StairsRailing,
            BuiltInCategory.OST_Ramps,
            BuiltInCategory.OST_CurtainWallPanels,
            BuiltInCategory.OST_CurtainWallMullions,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_FurnitureSystems,
        };

        private static readonly HashSet<BuiltInCategory> SkipCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_Areas,
            BuiltInCategory.OST_Grids,
            BuiltInCategory.OST_Levels,
            BuiltInCategory.OST_DetailComponents,
            BuiltInCategory.OST_Lines,
        };

        private static bool ShouldExport(Category cat, List<string> filter)
        {
            if (filter.Contains("all"))
                return ExportCategories.Contains((BuiltInCategory)cat.Id.Value);
            return filter.Any(f => cat.Name.Equals(f, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSkippedCategory(Category cat)
        {
            return SkipCategories.Contains((BuiltInCategory)cat.Id.Value);
        }

        #endregion
    }

    #region External Event Handler

    public class RevitCommandHandler : IExternalEventHandler
    {
        private static readonly ConcurrentQueue<Action<UIApplication>> _queue
            = new ConcurrentQueue<Action<UIApplication>>();

        public static ExternalEvent Event { get; set; }

        public static void Enqueue(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            Event?.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex) { Debug.WriteLine($"[CC] Handler error: {ex.Message}"); }
            }
        }

        public string GetName() => "ClashControlCommandHandler";
    }

    #endregion
}
