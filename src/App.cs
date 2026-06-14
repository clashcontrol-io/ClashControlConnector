using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        public const string Version = "0.1.0";

        private static WsServer _server;
        private static ExternalEvent _externalEvent;
        private static RevitCommandHandler _commandHandler;
        private static ElementCache _cache = new ElementCache();
        private static ChangeDebouncer _debouncer;
        private static CancellationTokenSource _exportCts;
        private static string _activeProjectId;
        // Host document version captured at export start → sent on model-start so CC
        // can tag a modelInstanceId / freshness stamp (detect "different copy").
        private static string _activeDocVersion;
        private static int _activeNumberOfSaves;
        private static readonly HashSet<ElementId> _highlightedElementIds = new HashSet<ElementId>();
        private static PushButton _ribbonButton;
        private static bool _lastKnownConnected;
        private static UIControlledApplication _uiApp;
        private static HashSet<BuiltInCategory> _allowedCategories;
        // DocKey of the host Revit document from the most recent export.
        // Used to filter DocumentChanged events so that edits in unrelated
        // open docs (including linked models opened as main docs elsewhere)
        // do not pollute live updates for the current session.
        private static string _hostDocKey;
        private static string _currentDocTitle;
        private static HashSet<ElementId> _lastSelection = new HashSet<ElementId>();
        private static double[] _lastCameraEye;
        private static DateTime _lastCameraSendTime = DateTime.MinValue;
        private static DateTime _lastSelectionCheckTime = DateTime.MinValue;

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

            // Throttle all polling to avoid slowing Revit's idle loop
            if (!connected || !(sender is UIApplication uiApp)) return;

            var now = DateTime.UtcNow;

            // Selection sync: check at most 4x/sec (every 250ms)
            if (ConnectorSettings.SyncSelection
                && (now - _lastSelectionCheckTime).TotalMilliseconds >= 250)
            {
                _lastSelectionCheckTime = now;
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc != null)
                {
                    var currentSelection = new HashSet<ElementId>(uidoc.Selection.GetElementIds());
                    if (!currentSelection.SetEquals(_lastSelection))
                    {
                        _lastSelection = currentSelection;
                        var globalIds = new List<string>();
                        var revitIds = new List<long>();
                        var selDoc = uidoc.Document;
                        foreach (var eid in currentSelection)
                        {
                            revitIds.Add(eid.Value);
                            var gid = _cache.FindByElementId(selDoc, eid);
                            if (gid != null) globalIds.Add(gid);
                        }
                        _ = _server.SendAsync(Messages.SelectionChanged(globalIds, revitIds));
                    }
                }
            }

            // Camera sync: check at most 2x/sec (every 500ms)
            if (ConnectorSettings.SyncCamera
                && (now - _lastCameraSendTime).TotalMilliseconds >= 500)
            {
                var view3d = uiApp.ActiveUIDocument?.ActiveView as View3D;
                if (view3d != null)
                {
                    var orientation = view3d.GetOrientation();
                    var eye = orientation.EyePosition;

                    bool changed = _lastCameraEye == null
                        || Math.Abs(eye.X - _lastCameraEye[0]) > 0.001
                        || Math.Abs(eye.Y - _lastCameraEye[1]) > 0.001
                        || Math.Abs(eye.Z - _lastCameraEye[2]) > 0.001;

                    if (changed)
                    {
                        _lastCameraEye = new[] { eye.X, eye.Y, eye.Z };
                        _lastCameraSendTime = now;
                        SendCameraToClashControl(uiApp);
                    }
                    else
                    {
                        // No change — push next check further out
                        _lastCameraSendTime = now;
                    }
                }
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

            ApplyRefreshInterval();

            _lastKnownConnected = false;
            UpdateButtonStatus(true, false);
            Debug.WriteLine($"[CC] ClashControl Connector v{Version} started on ws://localhost:19780");
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
            _debouncer?.Clear();
            _allowedCategories = null;
            _lastSelection.Clear();
            _activeProjectId = null;
            _lastCameraEye = null;
            _hostDocKey = null;

            _lastKnownConnected = false;
            _currentDocTitle = null;
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
                        _ = _server.SendAsync(Messages.Pong(_currentDocTitle, _currentDocTitle != null));
                        break;

                    case "export":
                        var knownElements = msg["knownElements"]?.ToObject<Dictionary<string, string>>();
                        var projectId = msg["projectId"]?.ToString();
                        RevitCommandHandler.Enqueue(app => ExportModel(app, knownElements, projectId));
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

                    case "camera-sync":
                        if (ConnectorSettings.SyncCamera)
                        {
                            var pos = msg["position"]?.ToObject<double[]>();
                            var tgt = msg["target"]?.ToObject<double[]>();
                            var up = msg["up"]?.ToObject<double[]>();
                            var fov = msg["fov"]?.ToObject<double>() ?? 60;
                            if (pos != null && tgt != null)
                                RevitCommandHandler.Enqueue(app => ApplyCameraFromBrowser(app, pos, tgt, up, fov));
                        }
                        break;

                    case "push-clashes":
                        var clashes = msg["clashes"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                        var issues = msg["issues"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                        RevitCommandHandler.Enqueue(app => HandlePushClashes(app, clashes, issues));
                        break;

                    case "resume-session":
                        var resumeKnown = msg["knownElements"]?.ToObject<Dictionary<string, string>>();
                        RevitCommandHandler.Enqueue(app => HandleResumeSession(app, resumeKnown));
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

        /// <summary>
        /// One linked or host Revit document being exported as a separate
        /// ClashControl model. Each ModelInfo gets its own model-start /
        /// element-batch / model-end sequence.
        /// </summary>
        private class ModelInfo
        {
            public string ModelId;        // Stable id sent to ClashControl
            public string ModelName;      // Display name (e.g. "Architecture.rvt")
            public Document Doc;          // Revit document (host or linked)
            public bool IsLinked;
            public List<Element> Elements = new List<Element>();

            // Per-model progress
            public int ElementIndex;
            public long ExpressId;
            public int ElementsSent;
            public bool StartSent;
            public List<ElementData> PendingData = new List<ElementData>();
            public List<string> UnchangedGlobalIds = new List<string>();
            public Dictionary<string, string> ElementHashes = new Dictionary<string, string>();
        }

        /// <summary>
        /// Holds state for a chunked export that processes elements across
        /// multiple ExternalEvent callbacks to keep Revit responsive.
        /// Each linked Revit model is exported as a separate ClashControl
        /// model so they can be clashed independently.
        /// </summary>
        private class ExportState
        {
            public List<ModelInfo> Models = new List<ModelInfo>();
            public Dictionary<string, string> KnownElements;
            public bool IsDeltaExport;
            public CancellationToken Ct;

            public int ModelIndex;
            public int TotalElements;
        }

        private static void ExportModel(UIApplication uiApp, Dictionary<string, string> knownElements = null, string projectId = null)
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

            // Store projectId for use in live update messages
            if (projectId != null)
                _activeProjectId = projectId;

            // Capture the document version once → sent on every model-start.
            _activeDocVersion = null;
            _activeNumberOfSaves = 0;
            try
            {
                var dv = Document.GetDocumentVersion(doc);
                if (dv != null)
                {
                    _activeDocVersion = dv.VersionGUID.ToString();
                    _activeNumberOfSaves = dv.NumberOfSaves;
                }
            }
            catch { /* version unavailable (e.g. family doc) — leave null */ }

            bool isDeltaExport = knownElements != null && knownElements.Count > 0;

            // Only clear cache for full exports
            if (!isDeltaExport)
                _cache.Clear();

            // Collect host model elements first
            var allowed = GetAllowedCategories();
            var state = new ExportState
            {
                KnownElements = knownElements,
                IsDeltaExport = isDeltaExport,
                Ct = _exportCts.Token
            };

            // Remember which doc is "host" for live-update filtering.
            _hostDocKey = ElementCache.GetDocKey(doc);
            _currentDocTitle = (doc.Title ?? "Host") + ".rvt";

            // Host model — always present, always first
            var hostInfo = new ModelInfo
            {
                ModelId = BuildHostModelId(doc),
                ModelName = (doc.Title ?? "Host") + ".rvt",
                Doc = doc,
                IsLinked = false,
                Elements = CollectExportableElements(doc, allowed)
            };
            state.Models.Add(hostInfo);
            state.TotalElements += hostInfo.Elements.Count;
            Debug.WriteLine($"[CC] Host model '{hostInfo.ModelName}' ({hostInfo.ModelId}): {hostInfo.Elements.Count} elements");

            // Linked models — each becomes its own ClashControl model so
            // ClashControl can clash them independently.
            if (ConnectorSettings.IncludeLinkedModels)
            {
                var linkInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                // Track how many instances share the same linked Document so
                // we can disambiguate the display name (e.g. "Arch.rvt (2)").
                var nameCounts = new Dictionary<string, int>();

                foreach (var linkInst in linkInstances)
                {
                    Document linkDoc = null;
                    try { linkDoc = linkInst.GetLinkDocument(); }
                    catch { /* unloaded / not found */ }
                    if (linkDoc == null)
                    {
                        Debug.WriteLine($"[CC] Skipping unloaded link: {linkInst.Name}");
                        continue;
                    }

                    try
                    {
                        var linkElements = CollectExportableElements(linkDoc, allowed);
                        var baseName = (linkDoc.Title ?? "Linked") + ".rvt";
                        nameCounts.TryGetValue(baseName, out var seen);
                        nameCounts[baseName] = seen + 1;
                        var displayName = seen == 0 ? baseName : $"{baseName} ({seen + 1})";

                        var linkInfo = new ModelInfo
                        {
                            ModelId = BuildLinkedModelId(linkInst, linkDoc),
                            ModelName = displayName,
                            Doc = linkDoc,
                            IsLinked = true,
                            Elements = linkElements
                        };
                        state.Models.Add(linkInfo);
                        state.TotalElements += linkElements.Count;
                        Debug.WriteLine($"[CC] Linked model '{linkInfo.ModelName}' ({linkInfo.ModelId}): {linkElements.Count} elements");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CC] Error reading linked model '{linkInst.Name}': {ex.Message}");
                    }
                }
            }

            // Announce the whole export so ClashControl can prepare N model slots.
            _ = _server.SendAsync(Messages.ExportStart(state.Models.Count, state.TotalElements, _activeProjectId));

            // Start chunked processing
            ProcessExportChunk(state);
        }

        /// <summary>
        /// Stable identifier for the host Revit document. Derived from the
        /// document path (or title if unsaved) so repeated exports of the
        /// same project produce the same modelId.
        /// </summary>
        private static string BuildHostModelId(Document doc)
        {
            var key = !string.IsNullOrEmpty(doc?.PathName) ? doc.PathName : (doc?.Title ?? "host");
            return "host:" + key;
        }

        /// <summary>
        /// Stable identifier for a linked Revit model. Uses the RevitLinkInstance
        /// UniqueId so two separate instances of the same linked file (copies at
        /// different locations) still appear as distinct models in ClashControl.
        /// </summary>
        private static string BuildLinkedModelId(RevitLinkInstance linkInst, Document linkDoc)
        {
            var instPart = linkInst?.UniqueId ?? "unknown-instance";
            var docPart = !string.IsNullOrEmpty(linkDoc?.PathName) ? linkDoc.PathName : (linkDoc?.Title ?? "link");
            return "link:" + instPart + "|" + docPart;
        }

        private static void ProcessExportChunk(ExportState state)
        {
            if (state.Ct.IsCancellationRequested)
            {
                int sentSoFar = 0;
                foreach (var m in state.Models) sentSoFar += m.ElementsSent;
                _ = _server.SendAsync(Messages.ExportCancelled(sentSoFar));
                return;
            }

            RevitCommandHandler.Enqueue(app =>
            {
                if (!_server.IsClientConnected) return;
                if (state.ModelIndex >= state.Models.Count)
                {
                    FinishExport(state);
                    return;
                }

                const int chunkSize = 25;
                const int batchSize = 50;

                var model = state.Models[state.ModelIndex];

                // Emit model-start the first time we touch this model.
                if (!model.StartSent)
                {
                    _ = _server.SendAsync(Messages.ModelStart(
                        model.ModelId,
                        model.ModelName,
                        model.Elements.Count,
                        state.ModelIndex,
                        state.Models.Count,
                        model.IsLinked,
                        _activeProjectId,
                        _activeDocVersion,
                        _activeNumberOfSaves));
                    model.StartSent = true;
                }

                int end = Math.Min(model.ElementIndex + chunkSize, model.Elements.Count);

                for (int i = model.ElementIndex; i < end; i++)
                {
                    try
                    {
                        var el = model.Elements[i];
                        var elDoc = el.Document ?? model.Doc;
                        var data = PropertyExporter.ExtractProperties(el, elDoc);
                        data.ExpressId = ++model.ExpressId;
                        data.ModelId = model.ModelId;
                        data.ModelName = model.ModelName;
                        data.Geometry = GeometryExporter.ExtractGeometry(el);
                        if (data.Geometry == null)
                            data.Geometry = new ElementGeometry();
                        data.Geometry.Color = GetElementColor(el, elDoc);

                        string contentHash = ContentHasher.ComputeHash(data);
                        model.ElementHashes[data.GlobalId] = contentHash;

                        if (state.IsDeltaExport
                            && state.KnownElements.TryGetValue(data.GlobalId, out var browserHash)
                            && browserHash == contentHash)
                        {
                            model.UnchangedGlobalIds.Add(data.GlobalId);
                            int geomHash = (data.Geometry.Positions ?? "").GetHashCode();
                            _cache.Add(data.GlobalId, elDoc, el.Id, model.ModelId, model.ModelName, geomHash);
                            _cache.SetContentHash(data.GlobalId, contentHash);
                            continue;
                        }

                        int gh = (data.Geometry.Positions ?? "").GetHashCode();
                        _cache.Add(data.GlobalId, elDoc, el.Id, model.ModelId, model.ModelName, gh);
                        _cache.SetContentHash(data.GlobalId, contentHash);
                        model.PendingData.Add(data);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CC] Skip element {model.Elements[i].Id}: {ex.Message}");
                    }
                }

                model.ElementIndex = end;

                // Flush any full batches for the current model.
                int totalBatches = Math.Max(1, (int)Math.Ceiling(model.Elements.Count / (double)batchSize));
                while (model.PendingData.Count >= batchSize)
                {
                    var batch = model.PendingData.GetRange(0, batchSize);
                    model.PendingData.RemoveRange(0, batchSize);
                    int batchIdx = model.ElementsSent / batchSize;
                    _ = _server.SendAsync(Messages.ElementBatch(model.ModelId, batchIdx, totalBatches, batch, _activeProjectId));
                    model.ElementsSent += batch.Count;
                }

                // Finished the current model?
                if (model.ElementIndex >= model.Elements.Count)
                {
                    FinishModel(state, model);
                    state.ModelIndex++;
                }

                if (state.ModelIndex < state.Models.Count)
                {
                    // More models to process — yield and continue.
                    ProcessExportChunk(state);
                }
                else
                {
                    // All models done.
                    FinishExport(state);
                }
            });
        }

        /// <summary>
        /// Flush the final batch for a single model and emit its model-end,
        /// including storeys and host/child relationships scoped to that model.
        /// </summary>
        private static void FinishModel(ExportState state, ModelInfo model)
        {
            const int batchSize = 50;

            // Flush any remaining elements for this model.
            if (model.PendingData.Count > 0)
            {
                int totalBatches = Math.Max(1, (int)Math.Ceiling(model.Elements.Count / (double)batchSize));
                int batchIdx = model.ElementsSent / batchSize;
                _ = _server.SendAsync(Messages.ElementBatch(
                    model.ModelId,
                    batchIdx,
                    Math.Max(totalBatches, batchIdx + 1),
                    model.PendingData,
                    _activeProjectId));
                model.ElementsSent += model.PendingData.Count;
                model.PendingData.Clear();
            }

            // Relationships live inside a single document, so build per-model.
            var (_, _, relatedPairs) =
                RelationshipExporter.BuildRelationships(model.Elements, model.Doc, _cache);

            // Collect storeys from this model's document only.
            var levels = new FilteredElementCollector(model.Doc)
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

            var unchanged = state.IsDeltaExport && model.UnchangedGlobalIds.Count > 0
                ? model.UnchangedGlobalIds : null;

            _ = _server.SendAsync(Messages.ModelEnd(
                model.ModelId,
                storeys,
                storeyData,
                relatedPairs,
                unchanged,
                model.ElementHashes.Count > 0 ? model.ElementHashes : null,
                _activeProjectId));

            if (state.IsDeltaExport)
                Debug.WriteLine($"[CC] Delta model '{model.ModelName}': {model.ElementsSent} changed, {model.UnchangedGlobalIds.Count} unchanged");
        }

        private static void FinishExport(ExportState state)
        {
            int totalSent = 0;
            foreach (var m in state.Models) totalSent += m.ElementsSent;

            _ = _server.SendAsync(Messages.ExportEnd(_activeProjectId));

            Debug.WriteLine($"[CC] Export complete: {state.Models.Count} model(s), {totalSent} elements sent");
        }

        private static void HandleResumeSession(UIApplication uiApp, Dictionary<string, string> knownElements)
        {
            // If cache is empty (Revit was restarted, or no prior export), we can't diff
            if (_cache.IsEmpty)
            {
                _ = _server.SendAsync(Messages.SessionExpired());
                return;
            }

            // Cache exists — treat as a delta export using the browser's known hashes
            ExportModel(uiApp, knownElements);
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

            // Resolve GlobalIds to ElementIds via cache (O(1) per lookup).
            // Elements from linked models cannot be highlighted directly in
            // the host view, so we only collect entries that belong to the
            // active document.
            var hostDocKey = ElementCache.GetDocKey(doc);
            var elementIds = new List<ElementId>();
            foreach (var gid in globalIds)
            {
                var entry = _cache.FindByGlobalId(gid);
                if (entry == null) continue;
                if (entry.DocKey != hostDocKey) continue;
                elementIds.Add(new ElementId(entry.ElementIdValue));
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

        #region Camera Sync

        private static void ApplyCameraFromBrowser(UIApplication uiApp, double[] pos, double[] tgt, double[] up, double fov)
        {
            var uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return;
            var view = uidoc.ActiveView as View3D;
            if (view == null) return;

            // Convert from ClashControl meters/Y-up to Revit feet/Z-up
            var eyePoint = new XYZ(pos[0] / 0.3048, -pos[2] / 0.3048, pos[1] / 0.3048);
            var targetPoint = new XYZ(tgt[0] / 0.3048, -tgt[2] / 0.3048, tgt[1] / 0.3048);
            var upDir = up != null
                ? new XYZ(up[0], -up[2], up[1])
                : XYZ.BasisZ;

            var forward = (targetPoint - eyePoint).Normalize();

            using (var t = new Transaction(uidoc.Document, "ClashControl: Camera Sync"))
            {
                t.Start();
                view.SetOrientation(new ViewOrientation3D(eyePoint, upDir, forward));
                t.Commit();
            }
        }

        /// <summary>
        /// Send the current Revit 3D view camera to ClashControl.
        /// Called from OnIdling when camera sync is enabled.
        /// </summary>
        private static void SendCameraToClashControl(UIApplication uiApp)
        {
            var view = uiApp.ActiveUIDocument?.ActiveView as View3D;
            if (view == null) return;

            var orientation = view.GetOrientation();
            var eye = orientation.EyePosition;
            var forward = orientation.ForwardDirection;
            var upDir = orientation.UpDirection;

            // Approximate target as eye + forward * reasonable distance
            var target = eye + forward * 100;

            // Convert from Revit feet/Z-up to ClashControl meters/Y-up
            var position = new[] { eye.X * 0.3048, eye.Z * 0.3048, -eye.Y * 0.3048 };
            var tgt = new[] { target.X * 0.3048, target.Z * 0.3048, -target.Y * 0.3048 };
            var up = new[] { upDir.X, upDir.Z, -upDir.Y };

            _ = _server.SendAsync(Messages.CameraSync(position, tgt, up, 60));
        }

        #endregion

        #region Live Updates (Sync-Triggered)

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!_server.IsClientConnected) return;

            // Only accept edits from the host document that was exported for
            // this session. ElementIds are only unique within a single
            // document, so mixing events from other docs (e.g. a linked Revit
            // model opened as a main doc in another tab) would corrupt lookups.
            // Linked models inside the host doc are refreshed via full
            // re-export on reload, not via live updates.
            if (_hostDocKey == null) return;
            var changedDoc = e.GetDocument();
            if (changedDoc == null) return;
            if (ElementCache.GetDocKey(changedDoc) != _hostDocKey) return;

            _debouncer.Add(
                e.GetModifiedElementIds(),
                e.GetAddedElementIds(),
                e.GetDeletedElementIds()
            );
        }

        private static void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (!_server.IsClientConnected) return;

            // Clear the debouncer — ClashControl will pull the updated model itself
            _debouncer.Clear();

            Debug.WriteLine("[CC] Sync with Central detected — sending model-sync notification");
            _ = _server.SendAsync(Messages.ModelSync());
        }

        private static void ProcessDebouncedChanges(
            HashSet<ElementId> modified, HashSet<ElementId> added, HashSet<ElementId> deleted)
        {
            RevitCommandHandler.Enqueue(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || !_server.IsClientConnected) return;

                // Live updates are scoped to the active (host) document. Linked
                // Revit models are exported as separate ClashControl models but
                // only refresh on full re-export / reload, since Revit doesn't
                // surface live edits to link contents from inside the host doc.
                var hostModelId = BuildHostModelId(doc);
                var hostModelName = (doc.Title ?? "Host") + ".rvt";

                // Handle deletions — resolve GlobalIds from cache
                if (deleted.Count > 0)
                {
                    var deletedGids = new List<string>();
                    var deletedRevitIds = new List<long>();

                    foreach (var eid in deleted)
                    {
                        var gid = _cache.FindByElementId(doc, eid);
                        if (gid != null) deletedGids.Add(gid);
                        deletedRevitIds.Add(eid.Value);
                        _cache.Remove(doc, eid);
                    }

                    _ = _server.SendAsync(Messages.ElementUpdateDeleted(hostModelId, deletedGids, deletedRevitIds, _activeProjectId));
                }

                var addedElements = new List<Element>();
                var modifiedElements = new List<Element>();
                var allowed = GetAllowedCategories();

                foreach (var eid in added)
                {
                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;
                    if (!ShouldExport(el.Category, allowed)) continue;
                    addedElements.Add(el);
                }

                foreach (var eid in modified)
                {
                    // Skip elements not in cache (internal Revit types, views, etc.)
                    if (_cache.FindByElementId(doc, eid) == null) continue;

                    var el = doc.GetElement(eid);
                    if (el?.Category == null || IsSkippedCategory(el.Category)) continue;

                    modifiedElements.Add(el);
                }

                // Added elements always get full geometry
                if (addedElements.Count > 0)
                {
                    var batch = new List<ElementData>();
                    foreach (var el in addedElements)
                    {
                        try
                        {
                            var data = PropertyExporter.ExtractProperties(el, doc);
                            data.ModelId = hostModelId;
                            data.ModelName = hostModelName;
                            data.Geometry = GeometryExporter.ExtractGeometry(el);
                            if (data.Geometry == null) data.Geometry = new ElementGeometry();
                            data.Geometry.Color = GetElementColor(el, doc);
                            _cache.Add(data.GlobalId, doc, el.Id, hostModelId, hostModelName, (data.Geometry.Positions ?? "").GetHashCode());
                            batch.Add(data);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[CC] Update skip {el.Id}: {ex.Message}");
                        }
                    }

                    if (batch.Count > 0)
                        _ = _server.SendAsync(Messages.ElementUpdateModified(hostModelId, batch, _activeProjectId));
                }

                // Modified elements: check if geometry actually changed
                if (modifiedElements.Count > 0)
                {
                    var geometryBatch = new List<ElementData>();
                    var propertiesBatch = new List<ElementData>();

                    foreach (var el in modifiedElements)
                    {
                        try
                        {
                            var data = PropertyExporter.ExtractProperties(el, doc);
                            data.ModelId = hostModelId;
                            data.ModelName = hostModelName;
                            var geom = GeometryExporter.ExtractGeometry(el);
                            if (geom == null) geom = new ElementGeometry();
                            geom.Color = GetElementColor(el, doc);

                            int newGeomHash = (geom.Positions ?? "").GetHashCode();

                            if (_cache.HasGeometryChanged(data.GlobalId, newGeomHash))
                            {
                                // Geometry changed — full update
                                data.Geometry = geom;
                                _cache.Add(data.GlobalId, doc, el.Id, hostModelId, hostModelName, newGeomHash);
                                geometryBatch.Add(data);
                            }
                            else
                            {
                                // Only properties changed — skip geometry payload
                                _cache.UpdateGeometryHash(data.GlobalId, newGeomHash);
                                propertiesBatch.Add(data);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[CC] Update skip {el.Id}: {ex.Message}");
                        }
                    }

                    if (geometryBatch.Count > 0)
                        _ = _server.SendAsync(Messages.ElementUpdateModified(hostModelId, geometryBatch, _activeProjectId));
                    if (propertiesBatch.Count > 0)
                        _ = _server.SendAsync(Messages.ElementUpdatePropertiesOnly(hostModelId, propertiesBatch, _activeProjectId));
                }

            });
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            _cache.Clear();
            _hostDocKey = null;
            _currentDocTitle = e.Document.Title + ".rvt";
            if (!_server.IsClientConnected) return;
            _ = _server.SendAsync(Messages.Status(true, _currentDocTitle));
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            _cache.Clear();
            _hostDocKey = null;
            _currentDocTitle = null;
            _highlightedElementIds.Clear();
            if (!_server.IsClientConnected) return;
            _ = _server.SendAsync(Messages.Status(true, ""));
        }

        #endregion

        #region Category Filters

        private static List<Element> CollectExportableElements(Document doc, HashSet<BuiltInCategory> allowed)
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .Where(e => e.Category != null && ShouldExport(e.Category, allowed))
                .Where(e => !IsSkippedCategory(e.Category))
                .ToList();
        }

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
        /// Invalidate cached allowed categories (call when settings change).
        /// </summary>
        public static void InvalidateAllowedCategories()
        {
            _allowedCategories = null;
        }

        private static HashSet<BuiltInCategory> GetAllowedCategories()
        {
            if (_allowedCategories != null) return _allowedCategories;

            var selected = ConnectorSettings.SelectedCategories;
            if (selected == null || selected.Count == 0)
            {
                _allowedCategories = new HashSet<BuiltInCategory>(CategoryNameMap.Values);
            }
            else
            {
                var allowed = new HashSet<BuiltInCategory>();
                foreach (var name in selected)
                {
                    if (CategoryNameMap.TryGetValue(name, out var bic))
                        allowed.Add(bic);
                }
                _allowedCategories = allowed;
            }
            return _allowedCategories;
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
