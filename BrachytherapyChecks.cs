// ============================================================================
// BrachytherapyChecks.cs
// Varian Eclipse ESAPI v15.6 — Brachytherapy Plan Check Script
//
// This is a standalone ESAPI script. Build it with Visual Studio targeting
// .NET Framework 4.6.1 and run it on a machine where Eclipse 15.6 is installed.
//
// Checks performed:
//   1. Plan identification and metadata
//   2. Prescription (dose, fractions, dose/fraction)
//   3. Technical (treatment unit, catheters, dwell times, dose calculation)
//   4. DVH-based target checks  (HR-CTV D90, V100, D100)
//   5. DVH-based OAR checks     (Bladder/Rectum/Sigmoid D2cc per GEC-ESTRO)
//   6. Plan approval status
//
// NOTE: D2cc values reported are physical dose from the brachytherapy plan
// only. Add the external beam EQD2 contribution separately to compare against
// total cumulative EQD2 GEC-ESTRO limits.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

// Mark as read-only so Eclipse does not prompt for write access.
[assembly: ESAPIScript(IsWriteable = false)]

namespace BrachytherapyChecks
{
    // =========================================================================
    // Entry point
    // =========================================================================
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                using (Application app = Application.CreateApplication())
                {
                    Execute(app);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("\nFATAL ERROR:\n" + ex);
                Console.WriteLine("\nPress Enter to exit.");
                Console.ReadLine();
            }
        }

        // =====================================================================
        // Top-level execution
        // =====================================================================
        static void Execute(Application app)
        {
            Console.Write("Enter Patient ID: ");
            string patientId = (Console.ReadLine() ?? "").Trim();

            if (string.IsNullOrEmpty(patientId))
            {
                Console.WriteLine("No patient ID entered. Exiting.");
                return;
            }

            Patient patient = app.OpenPatientById(patientId);
            if (patient == null)
            {
                Console.WriteLine($"Patient '{patientId}' not found.");
                return;
            }

            Console.WriteLine($"\nOpened: {patient.Name} (ID: {patient.Id})");

            // Collect all brachytherapy plans across all courses.
            var brachyPlans = new List<(Course Course, BrachyPlanSetup Plan)>();
            foreach (Course course in patient.Courses)
            {
                foreach (PlanSetup ps in course.PlanSetups)
                {
                    if (ps is BrachyPlanSetup bp)
                        brachyPlans.Add((course, bp));
                }
            }

            if (!brachyPlans.Any())
            {
                Console.WriteLine("No brachytherapy plans found for this patient.");
                app.ClosePatient();
                return;
            }

            // Let the user pick one plan or check all.
            Console.WriteLine($"\nFound {brachyPlans.Count} brachytherapy plan(s):");
            for (int i = 0; i < brachyPlans.Count; i++)
            {
                var (c, p) = brachyPlans[i];
                Console.WriteLine($"  [{i + 1}] Course: {c.Id,-20} Plan: {p.Id,-20} Status: {p.ApprovalStatus}");
            }

            Console.Write("\nSelect plan number (or 0 to check all): ");
            int.TryParse(Console.ReadLine(), out int selection);

            var toCheck = (selection >= 1 && selection <= brachyPlans.Count)
                ? new[] { brachyPlans[selection - 1] }
                : brachyPlans.ToArray();

            // Build report.
            var report = new StringBuilder();
            report.AppendLine(Separator('='));
            report.AppendLine("BRACHYTHERAPY PLAN CHECK REPORT");
            report.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Patient   : {patient.Name}");
            report.AppendLine($"ID        : {patient.Id}");
            report.AppendLine(Separator('='));

            foreach (var (course, plan) in toCheck)
                CheckBrachyPlan(course, plan, report);

            string reportText = report.ToString();
            Console.WriteLine("\n" + reportText);

            string outFile = $"BrachyCheck_{patient.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            File.WriteAllText(outFile, reportText);
            Console.WriteLine($"Report saved to: {Path.GetFullPath(outFile)}");

            app.ClosePatient();
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }

        // =====================================================================
        // Per-plan checks
        // =====================================================================
        static void CheckBrachyPlan(Course course, BrachyPlanSetup plan, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine($"PLAN: {plan.Id}   COURSE: {course.Id}");
            report.AppendLine(Separator('-'));

            var allChecks = new List<CheckResult>();

            // ------------------------------------------------------------------
            // 1. PLAN IDENTIFICATION
            // ------------------------------------------------------------------
            report.AppendLine("\n[1] PLAN IDENTIFICATION");
            report.AppendLine($"    Plan ID             : {plan.Id}");
            report.AppendLine($"    Plan Name           : {plan.Name}");
            report.AppendLine($"    Course ID           : {course.Id}");
            report.AppendLine($"    Approval Status     : {plan.ApprovalStatus}");
            report.AppendLine($"    Creation Date       : {plan.CreationDateTime?.ToString("yyyy-MM-dd") ?? "N/A"}");
            report.AppendLine($"    Brachy Technique    : {plan.BrachyTreatmentType}");

            // ------------------------------------------------------------------
            // 2. PRESCRIPTION
            // ------------------------------------------------------------------
            report.AppendLine("\n[2] PRESCRIPTION CHECKS");

            DoseValue rxDose      = plan.TotalDose;
            DoseValue dosePerFx   = plan.DosePerFraction;
            int?      numFx       = plan.NumberOfFractions;

            report.AppendLine($"    Total Dose          : {rxDose}");
            report.AppendLine($"    Dose / Fraction     : {dosePerFx}");
            report.AppendLine($"    Fractions           : {numFx?.ToString() ?? "Not set"}");

            var rxChecks = new List<CheckResult>
            {
                new CheckResult("Prescription dose > 0",
                    rxDose.Dose > 0,
                    rxDose.Dose > 0 ? rxDose.ToString() : "Prescription dose not set"),

                new CheckResult("Number of fractions set",
                    numFx.HasValue && numFx.Value > 0,
                    numFx.HasValue && numFx.Value > 0 ? $"{numFx.Value}" : "Not set"),

                new CheckResult("Dose per fraction > 0",
                    dosePerFx.Dose > 0,
                    dosePerFx.Dose > 0 ? dosePerFx.ToString() : "Not set"),
            };

            AppendChecks(report, rxChecks, "    ");
            allChecks.AddRange(rxChecks);

            // ------------------------------------------------------------------
            // 3. TECHNICAL CHECKS
            // ------------------------------------------------------------------
            report.AppendLine("\n[3] TECHNICAL CHECKS");

            var techChecks = new List<CheckResult>();

            // Treatment unit
            BrachyTreatmentUnit tu = plan.BrachyTreatmentUnit;
            bool hasTU = tu != null;
            techChecks.Add(new CheckResult("Treatment unit assigned", hasTU,
                hasTU ? tu.Id : "No treatment unit"));

            if (hasTU)
            {
                report.AppendLine($"    Treatment Unit      : {tu.Id}");
                report.AppendLine($"    Source Type         : {tu.SourceType}");
            }

            // Catheters / dwell points
            var catheters = plan.Catheters?.ToList() ?? new List<Catheter>();
            techChecks.Add(new CheckResult("At least one catheter defined",
                catheters.Count > 0, $"{catheters.Count} catheter(s)"));

            report.AppendLine($"    Catheter Count      : {catheters.Count}");

            double maxDwellTime = 0;
            int    totalDwellPoints = 0;
            bool   allDwellPositive = true;

            foreach (Catheter cath in catheters)
            {
                foreach (DwellPoint dp in cath.DwellPoints)
                {
                    totalDwellPoints++;
                    if (dp.IsEnabled)
                    {
                        if (dp.DwellTime <= 0)
                            allDwellPositive = false;
                        if (dp.DwellTime > maxDwellTime)
                            maxDwellTime = dp.DwellTime;
                    }
                }
            }

            techChecks.Add(new CheckResult("All active dwell times > 0",
                allDwellPositive,
                allDwellPositive
                    ? $"Max dwell time: {maxDwellTime:F1} s"
                    : "One or more active dwell times are ≤ 0"));

            report.AppendLine($"    Total Dwell Points  : {totalDwellPoints}");
            report.AppendLine($"    Max Dwell Time      : {maxDwellTime:F1} s");

            // Dose calculation
            bool isDoseValid = plan.IsDoseValid;
            techChecks.Add(new CheckResult("Dose calculation is valid", isDoseValid,
                isDoseValid ? "Dose is calculated" : "Dose has NOT been calculated"));

            AppendChecks(report, techChecks, "    ");
            allChecks.AddRange(techChecks);

            // ------------------------------------------------------------------
            // 4. DVH-BASED DOSE CHECKS
            // ------------------------------------------------------------------
            report.AppendLine("\n[4] DVH DOSE CHECKS");

            var dvhChecks = new List<CheckResult>();

            if (!isDoseValid)
            {
                report.AppendLine("    [SKIP] Dose not calculated — DVH checks skipped.");
            }
            else if (plan.StructureSet == null)
            {
                report.AppendLine("    [SKIP] No structure set attached to plan — DVH checks skipped.");
            }
            else
            {
                StructureSet ss = plan.StructureSet;

                // --- HR-CTV ---
                Structure hrCtv = FindStructure(ss, HrCtvNames);
                if (hrCtv != null)
                {
                    report.AppendLine($"\n    HR-CTV  [{hrCtv.Id}]  Volume: {hrCtv.Volume:F2} cc");

                    DVHData dvhRel = plan.GetDVHCumulativeData(
                        hrCtv,
                        DoseValuePresentation.Absolute,
                        VolumePresentation.Relative,
                        0.1);

                    if (dvhRel != null)
                    {
                        double d90  = GetDoseAtRelativeVolume(dvhRel, 90.0);
                        double d100 = GetDoseAtRelativeVolume(dvhRel, 100.0);
                        double v100 = GetVolumeAtDose(dvhRel, rxDose.Dose);

                        report.AppendLine($"    D90   = {d90:F2} {rxDose.Unit}  (Goal: ≥ {rxDose.Dose:F2})");
                        report.AppendLine($"    V100  = {v100:F1}%           (Goal: ≥ 90%)");
                        report.AppendLine($"    D100  = {d100:F2} {rxDose.Unit}");

                        dvhChecks.Add(new CheckResult(
                            $"HR-CTV D90 ≥ Rx ({rxDose.Dose:F2} {rxDose.Unit})",
                            d90 >= rxDose.Dose,
                            $"D90 = {d90:F2} {rxDose.Unit}"));

                        dvhChecks.Add(new CheckResult(
                            "HR-CTV V100 ≥ 90%",
                            v100 >= 90.0,
                            $"V100 = {v100:F1}%"));
                    }
                    else
                    {
                        report.AppendLine("    WARNING: Could not retrieve DVH for HR-CTV.");
                    }
                }
                else
                {
                    report.AppendLine($"    WARNING: HR-CTV not found. Searched: {string.Join(", ", HrCtvNames)}");
                }

                // --- OARs ---
                report.AppendLine("\n    OAR D2cc checks (physical dose — add EBRT EQD2 for total cumulative dose):");
                report.AppendLine($"    {"Structure",-15} {"D2cc (Gy)",-14} {"Limit (Gy EQD2)",-18} Result");
                report.AppendLine($"    {new string('-', 60)}");

                AddOarD2ccCheck(plan, ss, BladderNames,  "Bladder",  90.0, dvhChecks, report);
                AddOarD2ccCheck(plan, ss, RectumNames,   "Rectum",   75.0, dvhChecks, report);
                AddOarD2ccCheck(plan, ss, SigmoidNames,  "Sigmoid",  75.0, dvhChecks, report);
                AddOarD2ccReport(plan, ss, UrethraNames, "Urethra",        dvhChecks, report);
            }

            allChecks.AddRange(dvhChecks);

            // ------------------------------------------------------------------
            // 5. PLAN APPROVAL
            // ------------------------------------------------------------------
            report.AppendLine("\n[5] PLAN APPROVAL");

            bool isApproved = plan.ApprovalStatus == PlanSetupApprovalStatus.Approved
                           || plan.ApprovalStatus == PlanSetupApprovalStatus.TreatmentApproved;

            var approvalChecks = new List<CheckResult>
            {
                new CheckResult("Plan is approved for treatment",
                    isApproved,
                    plan.ApprovalStatus.ToString())
            };

            AppendChecks(report, approvalChecks, "    ");
            allChecks.AddRange(approvalChecks);

            // ------------------------------------------------------------------
            // SUMMARY
            // ------------------------------------------------------------------
            int passed = allChecks.Count(c => c.Pass);
            int failed = allChecks.Count(c => !c.Pass);

            report.AppendLine();
            report.AppendLine(Separator('-'));
            report.AppendLine($"SUMMARY: {passed} PASSED  |  {failed} FAILED  |  {allChecks.Count} TOTAL");
            if (failed > 0)
            {
                report.AppendLine("FAILED CHECKS:");
                foreach (var fc in allChecks.Where(c => !c.Pass))
                    report.AppendLine("  " + fc.Format());
            }
            report.AppendLine(Separator('='));
        }

        // =====================================================================
        // OAR helpers
        // =====================================================================

        /// <summary>Check OAR D2cc against a dose limit and record pass/fail.</summary>
        static void AddOarD2ccCheck(
            BrachyPlanSetup plan,
            StructureSet ss,
            string[] names,
            string label,
            double limitGy,
            List<CheckResult> checks,
            StringBuilder report)
        {
            Structure s = FindStructure(ss, names);
            if (s == null)
            {
                report.AppendLine($"    {label,-15} {"Not found",-14} {"≤ " + limitGy,-18}");
                return;
            }

            DVHData dvh = plan.GetDVHCumulativeData(
                s,
                DoseValuePresentation.Absolute,
                VolumePresentation.AbsoluteCm3,
                0.01);

            if (dvh == null)
            {
                report.AppendLine($"    {label,-15} {"DVH unavail.",-14} {"≤ " + limitGy,-18}");
                return;
            }

            double d2cc = GetDoseAtAbsoluteVolume(dvh, 2.0);
            bool   pass = d2cc <= limitGy;
            string result = pass ? "PASS" : "FAIL";

            report.AppendLine($"    {label,-15} {d2cc,8:F2} Gy    {"≤ " + limitGy,-18} [{result}]");
            checks.Add(new CheckResult($"{label} D2cc ≤ {limitGy} Gy", pass,
                $"D2cc = {d2cc:F2} Gy  [{s.Id}]"));
        }

        /// <summary>Report OAR D2cc and D0.1cc without a pass/fail limit (report-only).</summary>
        static void AddOarD2ccReport(
            BrachyPlanSetup plan,
            StructureSet ss,
            string[] names,
            string label,
            List<CheckResult> checks,
            StringBuilder report)
        {
            Structure s = FindStructure(ss, names);
            if (s == null)
            {
                report.AppendLine($"    {label,-15} {"Not found",-14} {"(report only)",-18}");
                return;
            }

            DVHData dvh = plan.GetDVHCumulativeData(
                s,
                DoseValuePresentation.Absolute,
                VolumePresentation.AbsoluteCm3,
                0.01);

            if (dvh == null)
            {
                report.AppendLine($"    {label,-15} {"DVH unavail.",-14} {"(report only)",-18}");
                return;
            }

            double d2cc  = GetDoseAtAbsoluteVolume(dvh, 2.0);
            double d01cc = GetDoseAtAbsoluteVolume(dvh, 0.1);

            report.AppendLine(
                $"    {label,-15} {d2cc,8:F2} Gy    {"(report only)",-18}  D0.1cc = {d01cc:F2} Gy  [{s.Id}]");
        }

        // =====================================================================
        // DVH interpolation helpers
        // =====================================================================

        /// <summary>
        /// Returns the dose (in DVH dose units) at which the cumulative relative
        /// volume equals <paramref name="volumePercent"/>%.
        /// The DVH CurveData must have VolumePresentation.Relative.
        /// </summary>
        static double GetDoseAtRelativeVolume(DVHData dvh, double volumePercent)
        {
            var curve = dvh.CurveData;
            if (curve == null || curve.Length == 0) return 0;

            // Cumulative DVH: dose increases, volume decreases.
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double v1 = curve[i].Volume;
                double v2 = curve[i + 1].Volume;

                if (v1 >= volumePercent && v2 <= volumePercent)
                {
                    double d1 = curve[i].DoseValue.Dose;
                    double d2 = curve[i + 1].DoseValue.Dose;
                    if (Math.Abs(v2 - v1) < 1e-10) return (d1 + d2) / 2.0;
                    return d1 + (d2 - d1) * (volumePercent - v1) / (v2 - v1);
                }
            }

            // If the volume never drops to volumePercent, return 0 (underdosed).
            return curve[0].Volume < volumePercent ? 0.0 : curve.Last().DoseValue.Dose;
        }

        /// <summary>
        /// Returns the dose at which the cumulative absolute volume equals
        /// <paramref name="volumeCc"/> cc.
        /// The DVH CurveData must have VolumePresentation.AbsoluteCm3.
        /// </summary>
        static double GetDoseAtAbsoluteVolume(DVHData dvh, double volumeCc)
        {
            var curve = dvh.CurveData;
            if (curve == null || curve.Length == 0) return 0;

            for (int i = 0; i < curve.Length - 1; i++)
            {
                double v1 = curve[i].Volume;
                double v2 = curve[i + 1].Volume;

                if (v1 >= volumeCc && v2 <= volumeCc)
                {
                    double d1 = curve[i].DoseValue.Dose;
                    double d2 = curve[i + 1].DoseValue.Dose;
                    if (Math.Abs(v2 - v1) < 1e-10) return (d1 + d2) / 2.0;
                    return d1 + (d2 - d1) * (volumeCc - v1) / (v2 - v1);
                }
            }

            return curve[0].Volume < volumeCc ? 0.0 : curve.Last().DoseValue.Dose;
        }

        /// <summary>
        /// Returns the percentage of the structure volume that receives at least
        /// <paramref name="doseValue"/> (in the DVH dose units).
        /// The DVH CurveData must have VolumePresentation.Relative.
        /// </summary>
        static double GetVolumeAtDose(DVHData dvh, double doseValue)
        {
            var curve = dvh.CurveData;
            if (curve == null || curve.Length == 0) return 0;

            // Scan for the dose crossing.
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double d1 = curve[i].DoseValue.Dose;
                double d2 = curve[i + 1].DoseValue.Dose;

                if (d1 <= doseValue && d2 >= doseValue)
                {
                    double v1 = curve[i].Volume;
                    double v2 = curve[i + 1].Volume;
                    if (Math.Abs(d2 - d1) < 1e-10) return (v1 + v2) / 2.0;
                    return v1 + (v2 - v1) * (doseValue - d1) / (d2 - d1);
                }
            }

            return doseValue <= curve[0].DoseValue.Dose ? 100.0 : 0.0;
        }

        // =====================================================================
        // Structure search helper
        // =====================================================================

        /// <summary>
        /// Returns the first non-empty structure whose Id matches any pattern in
        /// <paramref name="namePatterns"/> (case-insensitive, substring match).
        /// Exact matches are preferred over substring matches.
        /// </summary>
        static Structure FindStructure(StructureSet ss, string[] namePatterns)
        {
            // Exact match first.
            foreach (string p in namePatterns)
            {
                Structure s = ss.Structures.FirstOrDefault(x =>
                    string.Equals(x.Id, p, StringComparison.OrdinalIgnoreCase) && !x.IsEmpty);
                if (s != null) return s;
            }
            // Substring match.
            foreach (string p in namePatterns)
            {
                Structure s = ss.Structures.FirstOrDefault(x =>
                    x.Id.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 && !x.IsEmpty);
                if (s != null) return s;
            }
            return null;
        }

        // =====================================================================
        // Report utilities
        // =====================================================================

        static void AppendChecks(StringBuilder sb, IEnumerable<CheckResult> checks, string indent)
        {
            foreach (CheckResult c in checks)
                sb.AppendLine(indent + c.Format());
        }

        static string Separator(char ch) => new string(ch, 80);

        // =====================================================================
        // Structure name lookup tables
        // =====================================================================
        static readonly string[] HrCtvNames   = { "HR-CTV", "HRCTV", "HR_CTV", "CTV_HR", "CTV-HR", "ctv_hr", "hrctv" };
        static readonly string[] BladderNames  = { "Bladder", "BLADDER", "bladder", "Blad" };
        static readonly string[] RectumNames   = { "Rectum",  "RECTUM",  "rectum",  "Rect" };
        static readonly string[] SigmoidNames  = { "Sigmoid", "SIGMOID", "sigmoid" };
        static readonly string[] UrethraNames  = { "Urethra", "URETHRA", "urethra" };
    }

    // =========================================================================
    // CheckResult — single pass/fail item
    // =========================================================================
    class CheckResult
    {
        public string Description { get; }
        public bool   Pass        { get; }
        public string Details     { get; }

        public CheckResult(string description, bool pass, string details = "")
        {
            Description = description;
            Pass        = pass;
            Details     = details;
        }

        public string Format()
        {
            string tag    = Pass ? "[PASS]" : "[FAIL]";
            string detail = string.IsNullOrEmpty(Details) ? "" : $"  — {Details}";
            return $"{tag} {Description}{detail}";
        }
    }
}
