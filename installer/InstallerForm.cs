using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ClashControlInstaller
{
    /// <summary>
    /// One-click installer UI. Lists every Revit version the embedded
    /// resources can install for, lets the user tick any combination,
    /// and installs or uninstalls all selected versions in a single go.
    /// </summary>
    public class InstallerForm : Form
    {
        // Supported Revit years. To add a new year, drop its build into
        // installer/Resources/<year>/ and add the year to this array.
        private static readonly string[] SupportedVersions =
            { "2024", "2025", "2026", "2027" };

        // Files that must be written into the Revit addins folder for each year.
        private static readonly string[] PayloadFiles =
        {
            "ClashControlConnector.dll",
            "ClashControlConnector.addin",
            "Newtonsoft.Json.dll",
        };

        private readonly Dictionary<string, VersionRow> _rows =
            new Dictionary<string, VersionRow>();

        private RichTextBox _log;
        private Button _installBtn;
        private Button _uninstallBtn;
        private Button _closeBtn;

        public InstallerForm()
        {
            BuildUi();
        }

        // ---------- UI construction ----------

        private void BuildUi()
        {
            Text = "ClashControl Connector — Installer";
            ClientSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var header = new Label
            {
                Text = "Choose which Revit versions to install for:",
                Location = new Point(16, 14),
                Size = new Size(520, 22),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            };
            Controls.Add(header);

            var sub = new Label
            {
                Text = "Tick any combination. You can install multiple versions at once.",
                Location = new Point(16, 36),
                Size = new Size(520, 18),
                ForeColor = Color.DimGray,
            };
            Controls.Add(sub);

            int y = 66;
            foreach (var version in SupportedVersions)
            {
                var row = BuildRow(version, y);
                _rows[version] = row;
                y += 28;
            }

            var logLabel = new Label
            {
                Text = "Progress:",
                Location = new Point(16, y + 12),
                Size = new Size(100, 18),
            };
            Controls.Add(logLabel);

            _log = new RichTextBox
            {
                Location = new Point(16, y + 32),
                Size = new Size(528, 220),
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 9f),
                Text = "Ready. Select one or more versions above and click Install.",
            };
            Controls.Add(_log);

            _installBtn = new Button
            {
                Text = "Install",
                Location = new Point(232, y + 264),
                Size = new Size(100, 32),
            };
            _installBtn.Click += OnInstallClicked;
            Controls.Add(_installBtn);

            _uninstallBtn = new Button
            {
                Text = "Uninstall",
                Location = new Point(340, y + 264),
                Size = new Size(100, 32),
            };
            _uninstallBtn.Click += OnUninstallClicked;
            Controls.Add(_uninstallBtn);

            _closeBtn = new Button
            {
                Text = "Close",
                Location = new Point(448, y + 264),
                Size = new Size(96, 32),
            };
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);

            // Resize form to exactly fit the rows and log area.
            ClientSize = new Size(560, y + 310);
        }

        private VersionRow BuildRow(string version, int top)
        {
            var row = new VersionRow
            {
                Version = version,
                AddinsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", version),
                HasPayload = HasEmbeddedPayload(version),
            };
            row.RevitDetected = Directory.Exists(row.AddinsDir);
            row.AlreadyInstalled = File.Exists(Path.Combine(row.AddinsDir, "ClashControlConnector.dll"));

            var cb = new CheckBox
            {
                Location = new Point(24, top),
                Size = new Size(520, 22),
                Font = new Font("Segoe UI", 10f),
            };

            var parts = new List<string> { "Revit " + version };
            if (row.AlreadyInstalled)
                parts.Add("(already installed — will overwrite)");
            else if (row.RevitDetected)
                parts.Add("(Revit detected)");
            else
                parts.Add("(Revit not detected on this machine)");

            if (!row.HasPayload)
            {
                parts.Add("— no build bundled for this year");
                cb.Enabled = false;
            }

            cb.Text = string.Join("  ", parts);
            cb.Checked = row.HasPayload && row.RevitDetected;

            Controls.Add(cb);
            row.CheckBox = cb;
            return row;
        }

        // ---------- Button handlers ----------

        private void OnInstallClicked(object sender, EventArgs e)
        {
            var selected = GetSelected();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Please tick at least one Revit version to install.",
                    "Nothing selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            _log.Clear();
            _log.AppendText("Installing ClashControl Connector...\r\n");

            int ok = 0, fail = 0;
            foreach (var row in selected)
            {
                if (InstallForVersion(row)) ok++; else fail++;
                Application.DoEvents();
            }

            _log.AppendText("\r\n------------------------------\r\n");
            _log.AppendText(string.Format("Done. {0} installed, {1} failed.\r\n", ok, fail));
            if (ok > 0)
                _log.AppendText("Open Revit — you'll see a 'ClashControl' tab in the ribbon.\r\n");

            SetBusy(false);
            RefreshRowLabels();
        }

        private void OnUninstallClicked(object sender, EventArgs e)
        {
            var selected = GetSelected();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Please tick at least one Revit version to uninstall.",
                    "Nothing selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            _log.Clear();
            _log.AppendText("Uninstalling ClashControl Connector...\r\n");

            int ok = 0, fail = 0;
            foreach (var row in selected)
            {
                if (UninstallForVersion(row)) ok++; else fail++;
                Application.DoEvents();
            }

            _log.AppendText("\r\n------------------------------\r\n");
            _log.AppendText(string.Format("Done. {0} cleaned, {1} failed.\r\n", ok, fail));

            SetBusy(false);
            RefreshRowLabels();
        }

        private List<VersionRow> GetSelected()
        {
            return _rows.Values
                .Where(r => r.CheckBox.Enabled && r.CheckBox.Checked)
                .ToList();
        }

        private void SetBusy(bool busy)
        {
            _installBtn.Enabled = !busy;
            _uninstallBtn.Enabled = !busy;
            _closeBtn.Enabled = !busy;
        }

        private void RefreshRowLabels()
        {
            foreach (var row in _rows.Values)
            {
                row.AlreadyInstalled = File.Exists(Path.Combine(row.AddinsDir, "ClashControlConnector.dll"));
                row.RevitDetected = Directory.Exists(row.AddinsDir);

                var parts = new List<string> { "Revit " + row.Version };
                if (row.AlreadyInstalled) parts.Add("(installed)");
                else if (row.RevitDetected) parts.Add("(Revit detected)");
                else parts.Add("(Revit not detected on this machine)");
                if (!row.HasPayload) parts.Add("— no build bundled for this year");

                row.CheckBox.Text = string.Join("  ", parts);
            }
        }

        // ---------- Install / uninstall logic ----------

        private bool InstallForVersion(VersionRow row)
        {
            _log.AppendText("\r\n[Revit " + row.Version + "]\r\n");

            if (!row.HasPayload)
            {
                _log.AppendText("  SKIPPED — no bundled build for this year.\r\n");
                return false;
            }

            try
            {
                if (!Directory.Exists(row.AddinsDir))
                {
                    Directory.CreateDirectory(row.AddinsDir);
                    _log.AppendText("  Created addins folder: " + row.AddinsDir + "\r\n");
                }
            }
            catch (Exception ex)
            {
                _log.AppendText("  FAILED to create addins folder: " + ex.Message + "\r\n");
                return false;
            }

            foreach (var fileName in PayloadFiles)
            {
                var resource = GetResourceName(row.Version, fileName);
                if (resource == null)
                {
                    _log.AppendText("  SKIPPED " + fileName + " — not in this build.\r\n");
                    continue;
                }

                var targetPath = Path.Combine(row.AddinsDir, fileName);
                try
                {
                    using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    using (var dst = File.Create(targetPath))
                    {
                        if (src == null)
                        {
                            _log.AppendText("  FAILED to open embedded " + fileName + "\r\n");
                            return false;
                        }
                        src.CopyTo(dst);
                    }
                    _log.AppendText("  [OK] " + fileName + "\r\n");
                }
                catch (Exception ex)
                {
                    _log.AppendText("  FAILED to write " + fileName + " — " + ex.Message + "\r\n");
                    _log.AppendText("       (is Revit " + row.Version + " currently running? Close it and try again.)\r\n");
                    return false;
                }
            }

            return true;
        }

        private bool UninstallForVersion(VersionRow row)
        {
            _log.AppendText("\r\n[Revit " + row.Version + "]\r\n");

            bool anyRemoved = false;
            bool allOk = true;
            foreach (var fileName in PayloadFiles)
            {
                var target = Path.Combine(row.AddinsDir, fileName);
                if (!File.Exists(target)) continue;
                try
                {
                    File.Delete(target);
                    _log.AppendText("  [OK] removed " + fileName + "\r\n");
                    anyRemoved = true;
                }
                catch (Exception ex)
                {
                    allOk = false;
                    _log.AppendText("  FAILED to remove " + fileName + " — " + ex.Message + "\r\n");
                    _log.AppendText("       (is Revit " + row.Version + " currently running? Close it and try again.)\r\n");
                }
            }

            if (!anyRemoved && allOk)
                _log.AppendText("  Not installed — nothing to do.\r\n");

            return allOk;
        }

        // ---------- Embedded resource helpers ----------

        private static bool HasEmbeddedPayload(string version)
        {
            // We need at least the DLL present for a version to be installable.
            return GetResourceName(version, "ClashControlConnector.dll") != null;
        }

        private static string GetResourceName(string version, string fileName)
        {
            // ClashControlInstaller.csproj forces every embedded resource to
            // the predictable logical name "cc.<year>.<filename>".
            var primary = "cc." + version + "." + fileName;

            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            foreach (var name in names)
            {
                if (string.Equals(name, primary, StringComparison.Ordinal))
                    return name;
            }

            // Fallback: fuzzy match in case MSBuild applied its own name
            // mangling for any reason — find a resource with the version
            // year and the right file name.
            foreach (var name in names)
            {
                if (name.IndexOf(version, StringComparison.Ordinal) >= 0
                    && name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return null;
        }

        // ---------- Row state ----------

        private class VersionRow
        {
            public string Version;
            public string AddinsDir;
            public bool HasPayload;
            public bool RevitDetected;
            public bool AlreadyInstalled;
            public CheckBox CheckBox;
        }
    }
}
