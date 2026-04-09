using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ClashControlConnector.UI
{
    public class ConnectorSettingsForm : Form
    {
        private CheckedListBox _categoryList;
        private CheckBox _includeLinkedModels;
        private ComboBox _refreshInterval;
        private ComboBox _detailLevel;
        private CheckBox _syncSelection;
        private CheckBox _syncCamera;
        private Button _connectButton;
        private Button _cancelButton;
        private Button _selectAllButton;
        private Button _selectNoneButton;

        public List<string> SelectedCategories { get; private set; }
        public bool IncludeLinkedModels { get; private set; }
        public int RefreshIntervalSeconds { get; private set; }
        public int DetailLevelIndex { get; private set; }
        public bool SyncSelection { get; private set; }
        public bool SyncCamera { get; private set; }

        private static readonly (string Label, int Seconds)[] RefreshOptions = new[]
        {
            ("On Sync with Central only", 0),
            ("Every 10 seconds", 10),
            ("Every 30 seconds", 30),
            ("Every 1 minute", 60),
            ("Every 2 minutes", 120),
            ("Every 5 minutes", 300),
        };

        private static readonly string[] DetailLevelOptions = new[]
        {
            "Coarse (fastest, fewer triangles)",
            "Medium (recommended)",
            "Fine (highest detail)",
        };

        private static readonly string[] Categories = new[]
        {
            // Architectural
            "Walls", "Floors", "Roofs", "Ceilings",
            "Doors", "Windows", "Stairs", "Railings", "Ramps",
            "Curtain Panels", "Curtain Wall Mullions",
            "Generic Models", "Furniture", "Furniture Systems",
            // Structural
            "Columns", "Structural Columns",
            "Structural Framing", "Structural Foundations",
            // MEP
            "Ducts", "Pipes", "Flex Ducts", "Flex Pipes",
            "Duct Fittings", "Pipe Fittings",
            "Duct Accessories", "Pipe Accessories",
            "Mechanical Equipment", "Plumbing Fixtures",
            "Electrical Equipment", "Electrical Fixtures",
            "Cable Trays", "Conduits", "Lighting Fixtures",
            "Fire Alarm Devices", "Sprinklers",
        };

        // Persist selections across sessions within the same Revit instance
        private static HashSet<string> _lastSelectedCategories;
        private static bool _lastIncludeLinked = false;
        private static int _lastRefreshInterval = 0;
        private static int _lastDetailLevel = 1; // Medium
        private static bool _lastSyncSelection = false;
        private static bool _lastSyncCamera = false;

        public ConnectorSettingsForm()
        {
            InitializeComponents();
            LoadDefaults();
        }

        private void InitializeComponents()
        {
            Text = "ClashControl — Export Settings";
            Size = new Size(380, 720);
            MinimumSize = new Size(340, 620);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var catLabel = new Label
            {
                Text = "Categories to export:",
                Location = new Point(12, 12),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            _categoryList = new CheckedListBox
            {
                Location = new Point(12, 34),
                Size = new Size(340, 340),
                CheckOnClick = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            _selectAllButton = new Button
            {
                Text = "Select All",
                Location = new Point(12, 380),
                Size = new Size(85, 28)
            };
            _selectAllButton.Click += (s, e) => SetAll(true);

            _selectNoneButton = new Button
            {
                Text = "Select None",
                Location = new Point(103, 380),
                Size = new Size(85, 28)
            };
            _selectNoneButton.Click += (s, e) => SetAll(false);

            _includeLinkedModels = new CheckBox
            {
                Text = "Include linked Revit models",
                Location = new Point(12, 418),
                AutoSize = true
            };

            var detailLabel = new Label
            {
                Text = "Geometry detail level:",
                Location = new Point(12, 448),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            _detailLevel = new ComboBox
            {
                Location = new Point(12, 468),
                Size = new Size(340, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var opt in DetailLevelOptions)
                _detailLevel.Items.Add(opt);

            var refreshLabel = new Label
            {
                Text = "Refresh interval:",
                Location = new Point(12, 502),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            _refreshInterval = new ComboBox
            {
                Location = new Point(12, 522),
                Size = new Size(340, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var opt in RefreshOptions)
                _refreshInterval.Items.Add(opt.Label);

            _syncSelection = new CheckBox
            {
                Text = "Sync selection from Revit to browser",
                Location = new Point(12, 558),
                AutoSize = true
            };

            _syncCamera = new CheckBox
            {
                Text = "Sync camera position with browser",
                Location = new Point(12, 582),
                AutoSize = true
            };

            _connectButton = new Button
            {
                Text = "Connect",
                Location = new Point(170, 640),
                Size = new Size(90, 32),
                DialogResult = DialogResult.OK
            };

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(266, 640),
                Size = new Size(90, 32),
                DialogResult = DialogResult.Cancel
            };

            AcceptButton = _connectButton;
            CancelButton = _cancelButton;

            Controls.AddRange(new Control[]
            {
                catLabel, _categoryList,
                _selectAllButton, _selectNoneButton,
                _includeLinkedModels,
                detailLabel, _detailLevel,
                refreshLabel, _refreshInterval,
                _syncSelection, _syncCamera,
                _connectButton, _cancelButton
            });

            foreach (var cat in Categories)
                _categoryList.Items.Add(cat);
        }

        private void LoadDefaults()
        {
            if (_lastSelectedCategories != null)
            {
                for (int i = 0; i < Categories.Length; i++)
                {
                    _categoryList.SetItemChecked(i, _lastSelectedCategories.Contains(Categories[i]));
                }
                _includeLinkedModels.Checked = _lastIncludeLinked;
                _detailLevel.SelectedIndex = _lastDetailLevel;
                _syncSelection.Checked = _lastSyncSelection;
                _syncCamera.Checked = _lastSyncCamera;

                int idx = Array.FindIndex(RefreshOptions, o => o.Seconds == _lastRefreshInterval);
                _refreshInterval.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                SetAll(true);
                _refreshInterval.SelectedIndex = 0;
                _detailLevel.SelectedIndex = 1; // Medium
            }
        }

        private void SetAll(bool check)
        {
            for (int i = 0; i < _categoryList.Items.Count; i++)
                _categoryList.SetItemChecked(i, check);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                SelectedCategories = new List<string>();
                for (int i = 0; i < Categories.Length; i++)
                {
                    if (_categoryList.GetItemChecked(i))
                        SelectedCategories.Add(Categories[i]);
                }
                IncludeLinkedModels = _includeLinkedModels.Checked;
                RefreshIntervalSeconds = RefreshOptions[_refreshInterval.SelectedIndex].Seconds;
                DetailLevelIndex = _detailLevel.SelectedIndex;
                SyncSelection = _syncSelection.Checked;
                SyncCamera = _syncCamera.Checked;

                _lastSelectedCategories = new HashSet<string>(SelectedCategories);
                _lastIncludeLinked = IncludeLinkedModels;
                _lastRefreshInterval = RefreshIntervalSeconds;
                _lastDetailLevel = DetailLevelIndex;
                _lastSyncSelection = SyncSelection;
                _lastSyncCamera = SyncCamera;
            }
            base.OnFormClosing(e);
        }
    }
}
