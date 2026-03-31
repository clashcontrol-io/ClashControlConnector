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
        private static PushButton _ribbonButton;
        private static bool _lastKnownConnected;
        private static UIControlledApplication _uiApp;

        public static WsServer Server => _server;
        public static ElementCache Cache => _cache;
        public static bool IsServerRunning => _server != null;

        #region Startup / Shutdown

        public Result OnStartup(UIControlledApplication application)
        {
            _uiApp = application;

            // Register ExternalEvent for thread marshalling
            _commandHandler = new RevitCommandHandler();
            _externalEvent = ExternalEvent.Create(_commandHandler);
            RevitCommandHandler.Event = _externalEvent;

            // Initialize change accumulator (flushes on sync with central)
            _debouncer = new ChangeDebouncer(ProcessDebouncedChanges);

            // Do NOT start the server automatically — user must click the button

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

                buttonData.ToolTip = "Click to start ClashControl connector (ws://localhost:19780)";
                _ribbonButton = panel.AddItem(buttonData) as PushButton;
                UpdateButtonStatus(false, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CC] Ribbon error: {ex.Message}");
            }

            // Poll connection status to update ribbon button
            application.Idling += OnIdling;

            return Result.Succeeded;
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            bool running = _server != null;
            bool connected = _server?.IsClientConnected ?? false;
            if (connected != _lastKnownConnected)
            {
                _lastKnownConnected = connected;
                UpdateButtonStatus(running, connected);
            }
        }

        private static void UpdateButtonStatus(bool running, bool connected)
        {
            if (_ribbonButton == null) return;

            if (!running)
            {
                _ribbonButton.ItemText = "ClashControl\n○ Off";
                _ribbonButton.ToolTip = "Click to start ClashControl connector.";
            }
            else if (connected)
            {
                _ribbonButton.ItemText = "ClashControl\n● Connected";
                _ribbonButton.ToolTip = "ClashControl is connected on ws://localhost:19780\nClick to manage connection.";
            }
            else
            {
                _ribbonButton.ItemText = "ClashControl\n◌ Listening";
                _ribbonButton.ToolTip = "Waiting for ClashControl to connect on ws://localhost:19780\nOpen ClashControl in your browser and click 'Connect to Revit'.";
            }
        }

        /// <summary>
        /// Start the WebSocket server. Returns true on success.
        /// </summary>
        public static bool StartServer()
        {
            if (_server != null) return true;

            var server = new WsServer(19780);
            server.OnMessage += HandleMessage;

            if (!server.Start())
            {
                server.Dispose();
                return false;
            }

            _server = server;

            // Register document events
            _uiApp.ControlledApplication.DocumentChanged += OnDocumentChanged;
            _uiApp.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynced;
            _uiApp.ControlledApplication.DocumentOpened += OnDocumentOpened;
            _uiApp.ControlledApplication.DocumentClosing += OnDocumentClosing;

            // Apply refresh interval from settings
            ApplyRefreshInterval();

            _lastKnownConnected = false;
            UpdateButtonStatus(true, false);
            Debug.WriteLine("[CC] Server started on ws://localhost:19780");
            return true;
        }

        /// <summary>
        /// Apply the current refresh interval setting to the debouncer.
        /// Call this when settings change while connected.
        /// </summary>
        public static void ApplyRefreshInterval()
        {
            _debouncer?.SetInterval(ConnectorSettings.RefreshIntervalSeconds);
            Debug.WriteLine($"[CC] Refresh interval set to {ConnectorSettings.RefreshIntervalSeconds}s (0 = sync only)");
        }

        public static void StopServer()
        {
            if (_server == null) return;

            // Unregister document events
            _uiApp.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            _uiApp.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynced;
            _uiApp.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            _uiApp.ControlledApplication.DocumentClosing -= OnDocumentClosing;

            _exportCts?.Cancel();
            _server.Stop();
            _server = null;
            _cache.Clear();
            _highlightedElementIds.Clear();

            _lastKnownConnected = false;
            UpdateButtonStatus(false, false);
            Debug.WriteLine("[CC] Server stopped");
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            StopServer(); // safely stops and unregisters events if running
            _debouncer?.Dispose();
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
                        RevitCommandHandler.Enqueue(app => ExportModel(app));
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

        private static void ExportModel(UIApplication uiApp)
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

            // Build allowed categories from user settings
            var allowed = GetAllowedCategories();

            // Collect elements from host document
            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .Where(e => e.Category != null && ShouldExport(e.Category, allowed))
                .Where(e => !IsSkippedCategory(e.Category))
                .ToList();

            // Collect elements from linked Revit models if enabled
            if (ConnectorSettings.IncludeLinkedModels)
            {
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                foreach (var linkInst in linkInstances)
                {
                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;

                    try
                    {
                        var linkElements = new FilteredElementCollector(linkDoc)
                            .WhereElementIsNotElementType()
                            .WhereElementIsViewIndependent()
                            .Where(e => e.Category != null && ShouldExport(e.Category, allowed))
                            .Where(e => !IsSkippedCategory(e.Category))
                            .ToList();

                        elements.AddRange(linkElements);
                        Debug.WriteLine($"[CC] Linked model '{linkDoc.Title}': {linkElements.Count} elements");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CC] Error reading linked model: {ex.Message}");
                    }
                }
            }

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
                        // Use element's own document (handles linked model elements)
                        var elDoc = el.Document ?? doc;
                        var data = PropertyExporter.ExtractProperties(el, elDoc);
                        data.ExpressId = expressId++;
                        data.Geometry = GeometryExporter.ExtractGeometry(el);
                        if (data.Geometry == null)
                            data.Geometry = new ElementGeometry();
                        data.Geometry.Color = GetElementColor(el, elDoc);

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

                // Send batch — stop if client disconnected or send times out
                var sendTask = _server.SendAsync(Messages.ElementBatch(batchIdx, totalBatches, batch));
                if (!sendTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    Debug.WriteLine("[CC] Send timed out during export, aborting");
                    _ = _server.SendAsync(Messages.ModelError("Send timed out", elementsSent));
                    return;
                }
                if (!sendTask.Result)
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

        #region Live Updates (Sync-Triggered)

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!_server.IsClientConnected) return;

            // Only accumulate — changes are flushed when user syncs with central
            _debouncer.Add(
                e.GetModifiedElementIds(),
                e.GetAddedElementIds(),
                e.GetDeletedElementIds()
            );
        }

        private static void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (!_server.IsClientConnected) return;
            if (!_debouncer.HasChanges) return;

            Debug.WriteLine("[CC] Sync with Central detected — flushing accumulated changes");
            _debouncer.Flush();
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
                var allowed = GetAllowedCategories();

                foreach (var eid in added)
                {
                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;
                    if (!ShouldExport(el.Category, allowed)) continue;
                    fullUpdateElements.Add(el);
                }

                foreach (var eid in modified)
                {
                    // Only process elements we've already exported (in cache)
                    // This skips internal Revit elements, types, views, etc.
                    if (_cache.FindByElementId(eid) == null) continue;

                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;

                    // Always send as full update — the geometry extraction
                    // only happens once below, not twice for diffing
                    fullUpdateElements.Add(el);
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

        /// <summary>
        /// Maps friendly category names (from settings UI) to BuiltInCategory enums.
        /// </summary>
        private static readonly Dictionary<string, BuiltInCategory> CategoryNameMap =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["Walls"] = BuiltInCategory.OST_Walls,
            ["Floors"] = BuiltInCategory.OST_Floors,
            ["Roofs"] = BuiltInCategory.OST_Roofs,
            ["Ceilings"] = BuiltInCategory.OST_Ceilings,
            ["Doors"] = BuiltInCategory.OST_Doors,
            ["Windows"] = BuiltInCategory.OST_Windows,
            ["Columns"] = BuiltInCategory.OST_Columns,
            ["Structural Columns"] = BuiltInCategory.OST_StructuralColumns,
            ["Structural Framing"] = BuiltInCategory.OST_StructuralFraming,
            ["Structural Foundations"] = BuiltInCategory.OST_StructuralFoundation,
            ["Stairs"] = BuiltInCategory.OST_Stairs,
            ["Railings"] = BuiltInCategory.OST_StairsRailing,
            ["Ramps"] = BuiltInCategory.OST_Ramps,
            ["Curtain Panels"] = BuiltInCategory.OST_CurtainWallPanels,
            ["Curtain Wall Mullions"] = BuiltInCategory.OST_CurtainWallMullions,
            ["Generic Models"] = BuiltInCategory.OST_GenericModel,
            ["Ducts"] = BuiltInCategory.OST_DuctCurves,
            ["Pipes"] = BuiltInCategory.OST_PipeCurves,
            ["Flex Ducts"] = BuiltInCategory.OST_FlexDuctCurves,
            ["Flex Pipes"] = BuiltInCategory.OST_FlexPipeCurves,
            ["Duct Fittings"] = BuiltInCategory.OST_DuctFitting,
            ["Pipe Fittings"] = BuiltInCategory.OST_PipeFitting,
            ["Duct Accessories"] = BuiltInCategory.OST_DuctAccessory,
            ["Pipe Accessories"] = BuiltInCategory.OST_PipeAccessory,
            ["Mechanical Equipment"] = BuiltInCategory.OST_MechanicalEquipment,
            ["Plumbing Fixtures"] = BuiltInCategory.OST_PlumbingFixtures,
            ["Electrical Equipment"] = BuiltInCategory.OST_ElectricalEquipment,
            ["Electrical Fixtures"] = BuiltInCategory.OST_ElectricalFixtures,
            ["Cable Trays"] = BuiltInCategory.OST_CableTray,
            ["Conduits"] = BuiltInCategory.OST_Conduit,
            ["Lighting Fixtures"] = BuiltInCategory.OST_LightingFixtures,
            ["Fire Alarm Devices"] = BuiltInCategory.OST_FireAlarmDevices,
            ["Sprinklers"] = BuiltInCategory.OST_Sprinklers,
            ["Furniture"] = BuiltInCategory.OST_Furniture,
            ["Furniture Systems"] = BuiltInCategory.OST_FurnitureSystems,
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

        /// <summary>
        /// Build the set of allowed BuiltInCategories from ConnectorSettings.
        /// </summary>
        private static HashSet<BuiltInCategory> GetAllowedCategories()
        {
            var selected = ConnectorSettings.SelectedCategories;
            if (selected == null || selected.Count == 0)
            {
                // No selection = export all known categories
                return new HashSet<BuiltInCategory>(CategoryNameMap.Values);
            }

            var allowed = new HashSet<BuiltInCategory>();
            foreach (var name in selected)
            {
                if (CategoryNameMap.TryGetValue(name, out var bic))
                    allowed.Add(bic);
            }
            return allowed;
        }

        private static bool ShouldExport(Category cat, HashSet<BuiltInCategory> allowed)
        {
            return allowed.Contains((BuiltInCategory)cat.Id.Value);
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
