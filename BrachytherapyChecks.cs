// ============================================================================
// BrachytherapyChecks.cs
// Varian Eclipse ESAPI v15.6 — GYN HDR Catheter & Dwell QA Check
//
// Eclipse plugin script — build as a Library DLL and load from Eclipse
// via Script Wizard (Scripting → Run → Browse to DLL).
//
// On first run, copy GynHdrTolerances.xml (shipped alongside the DLL) to the
// same directory.  Edit that file to change tolerance values without rebuilding.
// If the file is absent, built-in defaults are used.
//
// Checks performed:
//   • Treatment unit identity
//   • Source type (informational)
//   • Dose calculation state
//   • Catheter count (min / max)
//   • Catheters with no active dwell points
//   • Total active dwell points
//   • Max single dwell time (warn / fail thresholds)
//   • Min active dwell time (fail threshold)
//   • Total treatment time (warn / fail thresholds)
//   • Max active source length per catheter (warn / fail thresholds)
//   • Dwell-point spacing regularity
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using VMS.TPS.Common.Model.API;

[assembly: ESAPIScript(IsWriteable = false)]

namespace VMS.TPS
{
    // =========================================================================
    // Script entry point — called by Eclipse when the DLL is loaded
    // =========================================================================
    public class Script
    {
        public void Execute(ScriptContext context)
        {
            BrachyPlanSetup plan = context.PlanSetup as BrachyPlanSetup;

            if (plan == null)
            {
                MessageBox.Show(
                    "No brachytherapy plan is currently open in Eclipse.\n\n" +
                    "Please open a brachy plan and re-run this script.",
                    "GYN HDR QA — No Plan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Tolerances tol    = Tolerances.Load();
            var        checks = QaChecks.Run(plan, tol);
            var        window = new ResultsWindow(context.Patient, plan, checks, tol);
            window.ShowDialog();
        }
    }

    // =========================================================================
    // Tolerances — loaded from GynHdrTolerances.xml; falls back to defaults
    // =========================================================================
    class Tolerances
    {
        // Treatment unit
        public string ExpectedTreatmentUnit      { get; private set; } = "";

        // Catheter count
        public int    CatheterCountMin            { get; private set; } = 1;
        public int    CatheterCountMax            { get; private set; } = 30;

        // Single dwell time limits (seconds)
        public double MaxDwellTimeWarnSec         { get; private set; } = 120.0;
        public double MaxDwellTimeFailSec         { get; private set; } = 200.0;

        // Minimum dwell time for an active dwell point (seconds)
        public double MinActiveDwellTimeSec       { get; private set; } = 0.1;

        // Total treatment time limits (seconds)
        public double MaxTreatmentTimeWarnSec     { get; private set; } = 1200.0;
        public double MaxTreatmentTimeFailSec     { get; private set; } = 1800.0;

        // Active source length per catheter (cm)
        public double MaxActiveLengthWarnCm       { get; private set; } = 20.0;
        public double MaxActiveLengthFailCm       { get; private set; } = 25.0;

        // Dwell-point spacing (mm) — expected value and allowed deviation
        public double DwellSpacingExpectedMm      { get; private set; } = 5.0;
        public double DwellSpacingToleranceMm     { get; private set; } = 1.0;

        // Path of the loaded config file (empty = defaults used)
        public string LoadedFrom                  { get; private set; } = "(built-in defaults)";

        Tolerances() { }

        public static Tolerances Load()
        {
            var t = new Tolerances();

            string dir = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? "";
            string path = Path.Combine(dir, "GynHdrTolerances.xml");

            if (!File.Exists(path))
                return t;

            try
            {
                XElement root = XDocument.Load(path).Root;
                if (root == null) return t;

                t.ExpectedTreatmentUnit   = Attr(root, "TreatmentUnit",    "expected",    "");
                t.CatheterCountMin        = IntAttr(root, "CatheterCount", "min",          1);
                t.CatheterCountMax        = IntAttr(root, "CatheterCount", "max",         30);
                t.MaxDwellTimeWarnSec     = DblAttr(root, "MaxDwellTime",  "warnSec",    120.0);
                t.MaxDwellTimeFailSec     = DblAttr(root, "MaxDwellTime",  "failSec",    200.0);
                t.MinActiveDwellTimeSec   = DblAttr(root, "MinActiveDwellTime", "failSec", 0.1);
                t.MaxTreatmentTimeWarnSec = DblAttr(root, "MaxTreatmentTime", "warnSec", 1200.0);
                t.MaxTreatmentTimeFailSec = DblAttr(root, "MaxTreatmentTime", "failSec", 1800.0);
                t.MaxActiveLengthWarnCm   = DblAttr(root, "MaxActiveLength", "warnCm",    20.0);
                t.MaxActiveLengthFailCm   = DblAttr(root, "MaxActiveLength", "failCm",    25.0);
                t.DwellSpacingExpectedMm  = DblAttr(root, "DwellSpacing",  "expectedMm",   5.0);
                t.DwellSpacingToleranceMm = DblAttr(root, "DwellSpacing",  "toleranceMm",  1.0);
                t.LoadedFrom = path;
            }
            catch { /* return defaults on any parse error */ }

            return t;
        }

        static string Attr(XElement root, string element, string attr, string def)
            => (string)(root.Element(element)?.Attribute(attr)) ?? def;

        static int IntAttr(XElement root, string element, string attr, int def)
            => int.TryParse(Attr(root, element, attr, ""), out int v) ? v : def;

        static double DblAttr(XElement root, string element, string attr, double def)
            => double.TryParse(
                Attr(root, element, attr, ""),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double v) ? v : def;
    }

    // =========================================================================
    // Check result model
    // =========================================================================
    enum CheckStatus { Pass, Warn, Fail, Info }

    class CheckItem
    {
        public string      Check     { get; }
        public string      Value     { get; }
        public string      Tolerance { get; }
        public CheckStatus Status    { get; }

        public CheckItem(string check, string value, string tolerance, CheckStatus status)
        {
            Check     = check;
            Value     = value;
            Tolerance = tolerance;
            Status    = status;
        }
    }

    // =========================================================================
    // QA logic
    // =========================================================================
    static class QaChecks
    {
        public static List<CheckItem> Run(BrachyPlanSetup plan, Tolerances tol)
        {
            var items = new List<CheckItem>();

            // ── 1. Treatment unit ──────────────────────────────────────────────
            BrachyTreatmentUnit tu = plan.BrachyTreatmentUnit;
            if (tu == null)
            {
                items.Add(Item("Treatment unit", "Not assigned", "Assigned", CheckStatus.Fail));
            }
            else
            {
                bool tuOk = string.IsNullOrEmpty(tol.ExpectedTreatmentUnit)
                    || string.Equals(tu.Id, tol.ExpectedTreatmentUnit,
                                     StringComparison.OrdinalIgnoreCase);

                string tolStr = string.IsNullOrEmpty(tol.ExpectedTreatmentUnit)
                    ? "(any)" : tol.ExpectedTreatmentUnit;

                items.Add(Item("Treatment unit", tu.Id, tolStr,
                    tuOk ? CheckStatus.Pass : CheckStatus.Fail));

                items.Add(Item("Source type", tu.SourceType.ToString(), "—", CheckStatus.Info));
            }

            // ── 2. Dose calculated ────────────────────────────────────────────
            items.Add(Item("Dose calculated",
                plan.IsDoseValid ? "Yes" : "No",
                "Yes",
                plan.IsDoseValid ? CheckStatus.Pass : CheckStatus.Fail));

            // ── 3. Catheter count ─────────────────────────────────────────────
            var catheters = plan.Catheters?.ToList() ?? new List<Catheter>();
            int nCath     = catheters.Count;

            CheckStatus cathStatus =
                nCath < tol.CatheterCountMin ? CheckStatus.Fail :
                nCath > tol.CatheterCountMax ? CheckStatus.Warn :
                CheckStatus.Pass;

            items.Add(Item("Catheter count",
                nCath.ToString(),
                $"{tol.CatheterCountMin}–{tol.CatheterCountMax}",
                cathStatus));

            // ── 4. Per-catheter dwell analysis ────────────────────────────────
            double maxSingleDwell = 0.0;
            double minActiveDwell = double.MaxValue;
            double totalTxTimeSec = 0.0;
            int    totalActive    = 0;
            int    emptyCaths     = 0;
            double maxActiveLenCm = 0.0;
            int    spacingFaults  = 0;

            foreach (Catheter cath in catheters)
            {
                var all    = cath.DwellPoints?.ToList() ?? new List<DwellPoint>();
                var active = all.Where(d => d.IsEnabled).ToList();

                if (active.Count == 0) { emptyCaths++; continue; }

                foreach (DwellPoint dp in active)
                {
                    totalActive++;
                    totalTxTimeSec += dp.DwellTime;
                    if (dp.DwellTime > maxSingleDwell) maxSingleDwell = dp.DwellTime;
                    if (dp.DwellTime < minActiveDwell) minActiveDwell = dp.DwellTime;
                }

                // Active source length: straight-line distance first → last active point (mm → cm)
                if (active.Count >= 2)
                {
                    double lenCm = VecDistance(active.First().Position,
                                              active.Last().Position) / 10.0;
                    if (lenCm > maxActiveLenCm) maxActiveLenCm = lenCm;
                }

                // Dwell spacing regularity
                for (int i = 0; i < active.Count - 1; i++)
                {
                    double spacing = VecDistance(active[i].Position, active[i + 1].Position);
                    if (Math.Abs(spacing - tol.DwellSpacingExpectedMm) > tol.DwellSpacingToleranceMm)
                        spacingFaults++;
                }
            }

            // Empty catheters
            int activeCaths = nCath - emptyCaths;
            items.Add(Item("Catheters with active dwells",
                $"{activeCaths} / {nCath}",
                "All catheters active",
                emptyCaths == 0 ? CheckStatus.Pass : CheckStatus.Warn));

            // Total active dwell points
            items.Add(Item("Total active dwell points",
                totalActive.ToString(),
                "> 0",
                totalActive > 0 ? CheckStatus.Pass : CheckStatus.Fail));

            if (totalActive > 0)
            {
                // Max single dwell time
                CheckStatus maxDwellSt =
                    maxSingleDwell >= tol.MaxDwellTimeFailSec ? CheckStatus.Fail :
                    maxSingleDwell >= tol.MaxDwellTimeWarnSec ? CheckStatus.Warn :
                    CheckStatus.Pass;
                items.Add(Item("Max single dwell time",
                    $"{maxSingleDwell:F1} s",
                    $"Warn ≥ {tol.MaxDwellTimeWarnSec:F0} s  /  Fail ≥ {tol.MaxDwellTimeFailSec:F0} s",
                    maxDwellSt));

                // Min active dwell time
                double minDwell = minActiveDwell == double.MaxValue ? 0.0 : minActiveDwell;
                items.Add(Item("Min active dwell time",
                    $"{minDwell:F2} s",
                    $"≥ {tol.MinActiveDwellTimeSec:F2} s",
                    minDwell >= tol.MinActiveDwellTimeSec ? CheckStatus.Pass : CheckStatus.Fail));

                // Total treatment time
                CheckStatus txSt =
                    totalTxTimeSec >= tol.MaxTreatmentTimeFailSec ? CheckStatus.Fail :
                    totalTxTimeSec >= tol.MaxTreatmentTimeWarnSec ? CheckStatus.Warn :
                    CheckStatus.Pass;
                items.Add(Item("Total treatment time",
                    $"{totalTxTimeSec:F1} s  ({totalTxTimeSec / 60.0:F1} min)",
                    $"Warn ≥ {tol.MaxTreatmentTimeWarnSec / 60.0:F0} min  /  " +
                    $"Fail ≥ {tol.MaxTreatmentTimeFailSec / 60.0:F0} min",
                    txSt));

                // Max active source length
                if (maxActiveLenCm > 0.0)
                {
                    CheckStatus lenSt =
                        maxActiveLenCm >= tol.MaxActiveLengthFailCm ? CheckStatus.Fail :
                        maxActiveLenCm >= tol.MaxActiveLengthWarnCm ? CheckStatus.Warn :
                        CheckStatus.Pass;
                    items.Add(Item("Max active source length",
                        $"{maxActiveLenCm:F1} cm",
                        $"Warn ≥ {tol.MaxActiveLengthWarnCm:F0} cm  /  " +
                        $"Fail ≥ {tol.MaxActiveLengthFailCm:F0} cm",
                        lenSt));
                }

                // Dwell spacing regularity
                items.Add(Item("Dwell spacing regularity",
                    spacingFaults == 0 ? "All within tolerance" : $"{spacingFaults} irregular gap(s)",
                    $"{tol.DwellSpacingExpectedMm:F0} ± {tol.DwellSpacingToleranceMm:F0} mm",
                    spacingFaults == 0 ? CheckStatus.Pass : CheckStatus.Warn));
            }

            return items;
        }

        static CheckItem Item(string check, string value, string tolerance, CheckStatus status)
            => new CheckItem(check, value, tolerance, status);

        static double VecDistance(VMS.TPS.Common.Model.Types.VVector a,
                                  VMS.TPS.Common.Model.Types.VVector b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // =========================================================================
    // WPF results window — built entirely in code (no XAML dependency)
    // =========================================================================
    class ResultsWindow : Window
    {
        public ResultsWindow(Patient patient, BrachyPlanSetup plan,
                             List<CheckItem> checks, Tolerances tol)
        {
            Title  = "GYN HDR — Catheter & Dwell QA";
            Width  = 860;
            Height = 560;
            MinWidth  = 700;
            MinHeight = 400;
            ResizeMode            = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(242, 244, 247));

            var root = new DockPanel();
            Content = root;

            // ── Header ───────────────────────────────────────────────────────
            var headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 50, 83)),
                Padding    = new Thickness(16, 11, 16, 11)
            };
            DockPanel.SetDock(headerBorder, Dock.Top);

            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text       = "GYN HDR — Catheter & Dwell QA Check",
                Foreground = Brushes.White,
                FontSize   = 15,
                FontWeight = FontWeights.SemiBold
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = $"Patient: {patient.Name}   |   ID: {patient.Id}   |   " +
                       $"Plan: {plan.Id}   |   Course: {plan.Course?.Id ?? "—"}   |   " +
                       $"{DateTime.Now:yyyy-MM-dd  HH:mm}",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 195, 220)),
                FontSize   = 10.5,
                Margin     = new Thickness(0, 4, 0, 0)
            });
            headerStack.Children.Add(new TextBlock
            {
                Text       = $"Tolerances: {tol.LoadedFrom}",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 155, 185)),
                FontSize   = 9.5,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            headerBorder.Child = headerStack;
            root.Children.Add(headerBorder);

            // ── Footer ────────────────────────────────────────────────────────
            int nPass = checks.Count(c => c.Status == CheckStatus.Pass);
            int nWarn = checks.Count(c => c.Status == CheckStatus.Warn);
            int nFail = checks.Count(c => c.Status == CheckStatus.Fail);

            var footerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(225, 228, 233)),
                Padding    = new Thickness(16, 8, 16, 8)
            };
            DockPanel.SetDock(footerBorder, Dock.Bottom);

            var footerPanel = new DockPanel { LastChildFill = false };

            var btnClose = new Button
            {
                Content = "Close",
                Width   = 80,
                Height  = 26,
                Margin  = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            btnClose.Click += (s, e) => Close();
            DockPanel.SetDock(btnClose, Dock.Right);

            var summary = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            summary.Children.Add(SummaryBadge(nPass, Color.FromRgb(55, 140, 55),  "PASS"));
            summary.Children.Add(SummaryBadge(nWarn, Color.FromRgb(200, 120,  0),  "WARN"));
            summary.Children.Add(SummaryBadge(nFail, Color.FromRgb(190,  30, 30),  "FAIL"));

            footerPanel.Children.Add(btnClose);
            footerPanel.Children.Add(summary);
            footerBorder.Child = footerPanel;
            root.Children.Add(footerBorder);

            // ── DataGrid ──────────────────────────────────────────────────────
            var grid = new DataGrid
            {
                Margin                = new Thickness(12, 10, 12, 6),
                AutoGenerateColumns   = false,
                IsReadOnly            = true,
                RowHeight             = 30,
                FontSize              = 12,
                GridLinesVisibility   = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility     = DataGridHeadersVisibility.Column,
                SelectionMode         = DataGridSelectionMode.Single,
                CanUserReorderColumns = false,
                CanUserResizeRows     = false,
                Background            = Brushes.White,
                BorderBrush           = new SolidColorBrush(Color.FromRgb(200, 204, 210)),
                BorderThickness       = new Thickness(1)
            };

            grid.Columns.Add(TextCol("Check",     "Check",     3));
            grid.Columns.Add(TextCol("Value",     "Value",     2));
            grid.Columns.Add(TextCol("Tolerance", "Tolerance", 3));
            grid.Columns.Add(StatusBadgeColumn());

            grid.ItemsSource = checks;
            root.Children.Add(grid);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static DataGridTextColumn TextCol(string header, string binding, double star)
            => new DataGridTextColumn
            {
                Header  = header,
                Binding = new Binding(binding),
                Width   = new DataGridLength(star, DataGridLengthUnitType.Star),
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters = { new Setter(TextBlock.VerticalAlignmentProperty,
                                           VerticalAlignment.Center) }
                }
            };

        static DataGridTemplateColumn StatusBadgeColumn()
        {
            var dt     = new DataTemplate(typeof(CheckItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.MarginProperty,       new Thickness(6, 4, 6, 4));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Status") { Converter = new StatusToBrushConverter() });

            var txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            txt.SetValue(TextBlock.VerticalAlignmentProperty,   VerticalAlignment.Center);
            txt.SetValue(TextBlock.FontWeightProperty,          FontWeights.Bold);
            txt.SetValue(TextBlock.ForegroundProperty,          Brushes.White);
            txt.SetValue(TextBlock.FontSizeProperty,            10.5);
            txt.SetValue(TextBlock.PaddingProperty,             new Thickness(8, 2, 8, 2));
            txt.SetBinding(TextBlock.TextProperty,
                new Binding("Status") { Converter = new StatusToLabelConverter() });

            border.AppendChild(txt);
            dt.VisualTree = border;

            return new DataGridTemplateColumn
            {
                Header       = "Status",
                Width        = new DataGridLength(80),
                CellTemplate = dt
            };
        }

        static Border SummaryBadge(int count, Color color, string label)
        {
            var b = new Border
            {
                Background      = new SolidColorBrush(color),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 6, 0),
                Padding         = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Child = new TextBlock
            {
                Text       = $"{count}  {label}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize   = 11
            };
            return b;
        }
    }

    // =========================================================================
    // Value converters
    // =========================================================================
    class StatusToBrushConverter : IValueConverter
    {
        static readonly SolidColorBrush BrushPass = new SolidColorBrush(Color.FromRgb(55, 140, 55));
        static readonly SolidColorBrush BrushWarn = new SolidColorBrush(Color.FromRgb(200, 120,  0));
        static readonly SolidColorBrush BrushFail = new SolidColorBrush(Color.FromRgb(190,  30, 30));
        static readonly SolidColorBrush BrushInfo = new SolidColorBrush(Color.FromRgb( 55, 115, 185));

        public object Convert(object value, Type targetType, object parameter,
                              System.Globalization.CultureInfo culture)
        {
            if (value is CheckStatus s)
                switch (s)
                {
                    case CheckStatus.Pass: return BrushPass;
                    case CheckStatus.Warn: return BrushWarn;
                    case CheckStatus.Fail: return BrushFail;
                    case CheckStatus.Info: return BrushInfo;
                }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter,
                                  System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    class StatusToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              System.Globalization.CultureInfo culture)
        {
            if (value is CheckStatus s)
                switch (s)
                {
                    case CheckStatus.Pass: return "PASS";
                    case CheckStatus.Warn: return "WARN";
                    case CheckStatus.Fail: return "FAIL";
                    case CheckStatus.Info: return "INFO";
                }
            return "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter,
                                  System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
