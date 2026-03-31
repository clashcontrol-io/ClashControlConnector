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
        private Button _connectButton;
        private Button _cancelButton;
        private Button _selectAllButton;
        private Button _selectNoneButton;

        public List<string> SelectedCategories { get; private set; }
        public bool IncludeLinkedModels { get; private set; }

        private static readonly (string Name, string Label, bool DefaultOn)[] Categories = new[]
        {
            // Architectural
            ("Walls", "Walls", true),
            ("Floors", "Floors", true),
            ("Roofs", "Roofs", true),
            ("Ceilings", "Ceilings", true),
            ("Doors", "Doors", true),
            ("Windows", "Windows", true),
            ("Stairs", "Stairs", true),
            ("Railings", "Railings", true),
            ("Ramps", "Ramps", true),
            ("Curtain Panels", "Curtain Panels", true),
            ("Curtain Wall Mullions", "Curtain Wall Mullions", true),
            ("Generic Models", "Generic Models", true),
            ("Furniture", "Furniture", true),
            ("Furniture Systems", "Furniture Systems", true),

            // Structural
            ("Columns", "Columns", true),
            ("Structural Columns", "Structural Columns", true),
            ("Structural Framing", "Structural Framing", true),
            ("Structural Foundations", "Structural Foundations", true),

            // MEP
            ("Ducts", "Ducts", true),
            ("Pipes", "Pipes", true),
            ("Flex Ducts", "Flex Ducts", true),
            ("Flex Pipes", "Flex Pipes", true),
            ("Duct Fittings", "Duct Fittings", true),
            ("Pipe Fittings", "Pipe Fittings", true),
            ("Duct Accessories", "Duct Accessories", true),
            ("Pipe Accessories", "Pipe Accessories", true),
            ("Mechanical Equipment", "Mechanical Equipment", true),
            ("Plumbing Fixtures", "Plumbing Fixtures", true),
            ("Electrical Equipment", "Electrical Equipment", true),
            ("Electrical Fixtures", "Electrical Fixtures", true),
            ("Cable Trays", "Cable Trays", true),
            ("Conduits", "Conduits", true),
            ("Lighting Fixtures", "Lighting Fixtures", true),
            ("Fire Alarm Devices", "Fire Alarm Devices", true),
            ("Sprinklers", "Sprinklers", true),
        };

        // Persist selections across sessions within the same Revit instance
        private static HashSet<string> _lastSelectedCategories;
        private static bool _lastIncludeLinked = false;

        public ConnectorSettingsForm()
        {
            InitializeComponents();
            LoadDefaults();
        }

        private void InitializeComponents()
        {
            Text = "ClashControl — Export Settings";
            Size = new Size(380, 560);
            MinimumSize = new Size(340, 400);
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
                Size = new Size(340, 360),
                CheckOnClick = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            _selectAllButton = new Button
            {
                Text = "Select All",
                Location = new Point(12, 400),
                Size = new Size(85, 28)
            };
            _selectAllButton.Click += (s, e) => SetAll(true);

            _selectNoneButton = new Button
            {
                Text = "Select None",
                Location = new Point(103, 400),
                Size = new Size(85, 28)
            };
            _selectNoneButton.Click += (s, e) => SetAll(false);

            _includeLinkedModels = new CheckBox
            {
                Text = "Include linked Revit models",
                Location = new Point(12, 438),
                AutoSize = true
            };

            _connectButton = new Button
            {
                Text = "Connect",
                Location = new Point(170, 480),
                Size = new Size(90, 32),
                DialogResult = DialogResult.OK
            };

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(266, 480),
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
                _connectButton, _cancelButton
            });

            // Populate categories
            foreach (var cat in Categories)
            {
                _categoryList.Items.Add(cat.Label);
            }
        }

        private void LoadDefaults()
        {
            if (_lastSelectedCategories != null)
            {
                // Restore previous session selections
                for (int i = 0; i < Categories.Length; i++)
                {
                    _categoryList.SetItemChecked(i, _lastSelectedCategories.Contains(Categories[i].Name));
                }
                _includeLinkedModels.Checked = _lastIncludeLinked;
            }
            else
            {
                // First time — check all by default
                SetAll(true);
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
                        SelectedCategories.Add(Categories[i].Name);
                }
                IncludeLinkedModels = _includeLinkedModels.Checked;

                // Persist for next time
                _lastSelectedCategories = new HashSet<string>(SelectedCategories);
                _lastIncludeLinked = IncludeLinkedModels;
            }
            base.OnFormClosing(e);
        }
    }
}
