using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace RevitMCPCommandSet.Services.Fmc
{
    /// <summary>
    /// Formcraft family builder (FMC-BIM-STD-002 4-5; Family Development Process stages 4-5) and load test (stage 6 helper).
    /// The MCP server resolves and checks every plan (template, save path, dictionary) before it arrives here.
    /// Anything the Revit API cannot do, or that fails, is reported per family, never skipped silently (Manual 17.2).
    /// </summary>
    public class FmcFamilyEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public string Action { get; set; } = "";
        public JObject Parameters { get; set; } = new JObject();
        public JObject Result { get; private set; } = new JObject();
        public string Error { get; private set; } = "";

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "FMC family builder";

        public void Execute(UIApplication app)
        {
            Result = new JObject();
            Error = "";
            try
            {
                if (Action == "build") Result = Build(app);
                else if (Action == "load_test") Result = LoadTest(app);
                else Error = $"Unknown FMC family action '{Action}'";
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { _resetEvent.Set(); }
        }

        private static double Mm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters);
        private static double Volts(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Volts);

        private static T? ParseEnum<T>(string name) where T : struct, Enum =>
            Enum.TryParse<T>(name, true, out var v) ? v : (T?)null;

        private static Parameter? Bip(Element e, string name)
        {
            var bip = ParseEnum<BuiltInParameter>(name);
            return bip.HasValue ? e.get_Parameter(bip.Value) : null;
        }

        // ------------------------------------------------------------------ build
        private JObject Build(UIApplication app)
        {
            var rvt = app.Application;
            string spFile = Parameters["shared_parameter_file"]?.Value<string>() ?? "";
            bool overwrite = Parameters["overwrite"]?.Value<bool>() ?? false;
            int backups = Math.Max(1, Parameters["max_backups"]?.Value<int>() ?? 3);
            var plans = Parameters["families"] as JArray ?? new JArray();
            if (!File.Exists(spFile)) throw new System.IO.FileNotFoundException($"Shared parameter file not found: {spFile}");

            string previous = rvt.SharedParametersFilename;
            var results = new JArray();
            int built = 0;
            try
            {
                rvt.SharedParametersFilename = spFile;
                DefinitionFile df = rvt.OpenSharedParameterFile()
                    ?? throw new InvalidOperationException($"Revit could not open the shared parameter file {spFile}");
                foreach (JObject plan in plans)
                {
                    var r = BuildOne(rvt, df, plan, overwrite, backups);
                    if (r["saved"] != null) built++;
                    results.Add(r);
                }
            }
            finally
            {
                rvt.SharedParametersFilename = previous ?? "";
            }
            return new JObject { ["built"] = built, ["failed"] = plans.Count - built, ["families"] = results };
        }

        private static ExternalDefinition? FindDefinition(DefinitionFile df, string name, string guid)
        {
            foreach (DefinitionGroup g in df.Groups)
            {
                if (g.Definitions.get_Item(name) is ExternalDefinition d &&
                    string.Equals(d.GUID.ToString(), guid, StringComparison.OrdinalIgnoreCase)) return d;
            }
            return null;
        }

        private JObject BuildOne(Autodesk.Revit.ApplicationServices.Application rvt, DefinitionFile df, JObject plan, bool overwrite, int backups)
        {
            string name = plan["family"]?.Value<string>() ?? "";
            string template = plan["template_path"]?.Value<string>() ?? "";
            string target = plan["save_path"]?.Value<string>() ?? "";
            var warnings = new JArray();
            var result = new JObject { ["family"] = name };
            Document? fam = null;
            try
            {
                fam = rvt.NewFamilyDocument(template) ?? throw new InvalidOperationException($"Revit could not open template {template}");
                var fm = fam.FamilyManager;
                int sharedAdded = 0, connectorsMade = 0;
                var types = (plan["types"] as JArray)?.Select(x => x.Value<string>() ?? "").Where(x => x != "").ToList() ?? new List<string>();
                var connectors = (plan["connectors"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();

                var revitErrors = new List<string>();
                using (var t = new Transaction(fam, "FMC build family"))
                {
                    // Without a handler a Revit warning opens a modal dialog and the build waits for a click until the
                    // command times out. Warnings are recorded for the Family Librarian; errors roll this family back.
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new RecordFailures(warnings, revitErrors));
                    opts.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(opts);
                    t.Start();
                    // 1 types
                    var existing = new HashSet<string>(fm.Types.Cast<FamilyType>().Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (string tn in types.Count > 0 ? types : new List<string> { "Standard" })
                    {
                        if (existing.Contains(tn)) continue;
                        try { fm.NewType(tn); existing.Add(tn); }
                        catch (Exception ex) { warnings.Add($"Type '{tn}' not created: {ex.Message}"); }
                    }

                    // 2 shared parameters from the dictionary only
                    foreach (JObject p in (plan["shared_parameters"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        string pn = p["name"]?.Value<string>() ?? "";
                        if (fm.get_Parameter(pn) != null) continue;
                        var def = FindDefinition(df, pn, p["guid"]?.Value<string>() ?? "");
                        if (def == null) { warnings.Add($"{pn}: not found in the shared parameter file with the expected GUID"); continue; }
                        try { fm.AddParameter(def, GroupTypeId.Data, p["instance"]?.Value<bool>() ?? false); sharedAdded++; }
                        catch (Exception ex) { warnings.Add($"{pn} not added: {ex.Message}"); }
                    }

                    // 3 category: stand-in templates (Generic Model, Generic Tag) take the register's category
                    string setCategory = plan["set_category"]?.Value<string>() ?? "";
                    if (setCategory != "")
                    {
                        var bic = ParseEnum<BuiltInCategory>(setCategory);
                        Category? cat = bic.HasValue ? Category.GetCategory(fam, bic.Value) : null;
                        if (cat == null) warnings.Add($"Category {setCategory} not available in Revit {rvt.VersionNumber}: set it in Family Category and Parameters");
                        else if (fam.OwnerFamily.FamilyCategory?.Id != cat.Id)
                        {
                            try { fam.OwnerFamily.FamilyCategory = cat; result["category"] = cat.Name; }
                            catch (Exception ex) { warnings.Add($"Category {cat.Name} not set: {ex.Message}"); }
                        }
                    }

                    // 4 Part Type for electrical equipment (STD-002 5.2)
                    string partType = plan["part_type"]?.Value<string>() ?? "";
                    if (partType != "")
                    {
                        var pt = ParseEnum<PartType>(partType.Replace(" ", "").Replace("Panelboard", "PanelBoard").Replace("Switchboard", "SwitchBoard"));
                        var ptParam = fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                        if (pt.HasValue && ptParam != null && !ptParam.IsReadOnly) ptParam.Set((int)pt.Value);
                        else warnings.Add($"Part Type '{partType}' not set: set it in Family Category and Parameters");
                    }

                    // 5 placeholder box and connectors
                    if (connectors.Count > 0)
                        connectorsMade = AddConnectors(fam, fm, connectors, types, warnings);

                    if (t.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit rolled the family back" +
                            (revitErrors.Count > 0 ? ": " + string.Join("; ", revitErrors) : ""));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                fam.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = overwrite, MaximumBackups = backups });
                result["saved"] = target;
                result["types"] = fm.Types.Size;
                result["shared_parameters_added"] = sharedAdded;
                result["connectors_created"] = connectorsMade;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }
            finally
            {
                try { fam?.Close(false); } catch { /* already closed */ }
            }
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        /// <summary>A 400 x 300 x 300 mm box on the reference level; electrical connectors on top, pipes and ducts on the sides.</summary>
        private static int AddConnectors(Document fam, FamilyManager fm, List<JObject> connectors, List<string> types, JArray warnings)
        {
            double w = Mm(400), d = Mm(300), h = Mm(300);
            var pts = new[] { new XYZ(-w / 2, -d / 2, 0), new XYZ(w / 2, -d / 2, 0), new XYZ(w / 2, d / 2, 0), new XYZ(-w / 2, d / 2, 0) };
            var loop = new CurveArray();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            var profile = new CurveArrArray();
            profile.Append(loop);
            var plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            Extrusion box = fam.FamilyCreate.NewExtrusion(true, profile, plane, h);
            fam.Regenerate();

            var faces = new List<PlanarFace>();
            foreach (GeometryObject go in box.get_Geometry(new Options { ComputeReferences = true }))
                if (go is Solid s)
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.Reference != null) faces.Add(pf);
            PlanarFace? top = faces.FirstOrDefault(f => f.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ));
            var sides = faces.Where(f => Math.Abs(f.FaceNormal.Z) < 0.01).ToList();
            if (top == null || sides.Count == 0) { warnings.Add("Placeholder box faces not found: add connectors by hand"); return 0; }

            FamilyParameter? voltage = null, poles = null, pf_ = null, apparent = null;
            int made = 0, side = 0, loads = 0;
            bool sharedFace = false;
            foreach (var c in connectors)
            {
                string domain = c["domain"]?.Value<string>() ?? "";
                string system = c["system"]?.Value<string>() ?? "";
                string note = c["note"]?.Value<string>() ?? "";
                try
                {
                    if (domain == "Electrical" || domain == "ELV")
                    {
                        if (made > 0 && domain == "Electrical") sharedFace = true;
                        ElectricalSystemType est = domain == "ELV"
                            ? ParseEnum<ElectricalSystemType>(system.Replace(" ", "")) ?? ElectricalSystemType.UndefinedSystemType
                            : (c["poles"]?.Type == JTokenType.Integer && c["poles"]!.Value<int>() == 3
                                ? ElectricalSystemType.PowerBalanced : ElectricalSystemType.PowerUnBalanced);
                        var ce = ConnectorElement.CreateElectricalConnector(fam, est, top.Reference);
                        made++;
                        if (domain == "ELV") continue;

                        voltage ??= fm.get_Parameter("Voltage") ?? fm.AddParameter("Voltage", GroupTypeId.ElectricalLoads, SpecTypeId.ElectricalPotential, false);
                        poles ??= fm.get_Parameter("Number of Poles") ?? fm.AddParameter("Number of Poles", GroupTypeId.ElectricalLoads, SpecTypeId.Int.NumberOfPoles, false);
                        Associate(fm, ce, "RBS_ELEC_VOLTAGE", voltage, warnings);
                        Associate(fm, ce, "RBS_ELEC_NUMBER_OF_POLES", poles, warnings);
                        SetPerType(fm, types, voltage, poles, c, warnings);

                        if ((c["role"]?.Value<string>() ?? "") == "load")
                        {
                            loads++;
                            pf_ ??= fm.get_Parameter("Power Factor") ?? fm.AddParameter("Power Factor", GroupTypeId.ElectricalLoads, SpecTypeId.Number, false);
                            string an = loads == 1 ? "Apparent Load" : $"Apparent Load {loads}";
                            var ap = fm.get_Parameter(an) ?? fm.AddParameter(an, GroupTypeId.ElectricalLoads, SpecTypeId.ApparentPower, false);
                            if (loads == 1) apparent = ap;
                            Associate(fm, ce, "RBS_ELEC_POWER_FACTOR", pf_, warnings);
                            Associate(fm, ce, "RBS_ELEC_APPARENT_LOAD", ap, warnings);
                            foreach (FamilyType ft in fm.Types) { fm.CurrentType = ft; fm.Set(pf_, 0.9); }
                            string powerParam = c["power_param"]?.Value<string>() ?? "";
                            if (powerParam != "" && fm.get_Parameter(powerParam) != null) SetApparentFormula(fm, ap, powerParam, warnings);
                            else warnings.Add($"{an}: no rated-power parameter ({(powerParam == "" ? "none in the spec" : powerParam + " missing")}); enter the load per type");
                            string lc = c["load_class"]?.Value<string>() ?? "";
                            if (lc != "") warnings.Add($"Set Load Classification '{lc}' on the connector by hand (not settable by the API in a family)");
                        }
                    }
                    else if (domain == "Piping" || domain == "Duct")
                    {
                        PlanarFace face = sides[side % sides.Count];
                        if (side >= sides.Count) sharedFace = true;
                        side++;
                        ConnectorElement ce;
                        if (domain == "Piping")
                        {
                            var pst = ParseEnum<PipeSystemType>(PipeName(system)) ?? ParseEnum<PipeSystemType>("OtherPipe") ?? default(PipeSystemType);
                            ce = ConnectorElement.CreatePipeConnector(fam, pst, face.Reference);
                            var size = c["size_mm"];
                            if (size != null && size.Type == JTokenType.Integer)
                                Bip(ce, "CONNECTOR_RADIUS")?.Set(Mm(size.Value<int>() / 2.0));
                            SetFlow(ce, "RBS_PIPE_FLOW_DIRECTION_PARAM", c["direction"]?.Value<string>(), warnings);
                        }
                        else
                        {
                            var dst = ParseEnum<DuctSystemType>(system.Replace(" ", "")) ?? ParseEnum<DuctSystemType>("OtherAir") ?? default(DuctSystemType);
                            ce = ConnectorElement.CreateDuctConnector(fam, dst, ConnectorProfileType.Round, face.Reference);
                            SetFlow(ce, "RBS_DUCT_FLOW_DIRECTION_PARAM", c["direction"]?.Value<string>(), warnings);
                        }
                        made++;
                        string sysType = c["system_type"]?.Value<string>() ?? "";
                        if (sysType != "") warnings.Add($"{system} connector{(note != "" ? " (" + note + ")" : "")}: project system type {sysType} is assigned in the project, not the family");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{domain} {system} connector not created: {ex.Message}");
                }
            }
            if (sharedFace) warnings.Add("Some connectors share a face of the placeholder box: move them to their real positions when the geometry is modelled");
            return made;
        }

        private static string PipeName(string classification) => classification switch
        {
            "Domestic Cold Water" => "DomesticColdWater",
            "Domestic Hot Water" => "DomesticHotWater",
            "Fire Protection Wet" => "FireProtectWet",
            "Other" => "OtherPipe",
            _ => classification.Replace(" ", ""),
        };

        private static void Associate(FamilyManager fm, ConnectorElement ce, string bip, FamilyParameter fp, JArray warnings)
        {
            var p = Bip(ce, bip);
            if (p == null) { warnings.Add($"Connector parameter {bip} not found"); return; }
            try { fm.AssociateElementParameterToFamilyParameter(p, fp); }
            catch (Exception ex) { warnings.Add($"{fp.Definition.Name} not linked to {bip}: {ex.Message}"); }
        }

        private static void SetFlow(ConnectorElement ce, string bip, string? direction, JArray warnings)
        {
            if (string.IsNullOrEmpty(direction)) return;
            var flow = ParseEnum<FlowDirectionType>(direction!);
            var p = Bip(ce, bip);
            if (flow.HasValue && p != null && !p.IsReadOnly) p.Set((int)flow.Value);
            else warnings.Add($"Flow direction {direction} not set on a connector");
        }

        /// <summary>Voltage exactly 240 V or 415 V (never a rounding formula, STD-002 5.3); per type where the spec says so.</summary>
        private static void SetPerType(FamilyManager fm, List<string> types, FamilyParameter voltage, FamilyParameter poles, JObject c, JArray warnings)
        {
            var perType = c["per_type"] as JObject;
            foreach (FamilyType ft in fm.Types)
            {
                int v = 0, n = 0;
                if (perType != null)
                {
                    foreach (var kv in perType)
                        if (ft.Name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value is JArray a && a.Count == 2)
                        { v = a[0].Value<int>(); n = a[1].Value<int>(); }
                    if (v == 0) { warnings.Add($"Type {ft.Name}: no voltage in the spec's per-type list"); continue; }
                }
                else if (c["voltage"]?.Type == JTokenType.Integer)
                {
                    v = c["voltage"]!.Value<int>();
                    n = c["poles"]?.Value<int>() ?? 1;
                }
                else continue;
                fm.CurrentType = ft;
                fm.Set(voltage, Volts(v));
                fm.Set(poles, n);
            }
        }

        private static void SetApparentFormula(FamilyManager fm, FamilyParameter apparent, string powerParam, JArray warnings)
        {
            // FMC_Power_W is a NUMBER in the dictionary; Revit may refuse to divide it into an apparent-power value.
            string[] tries = { $"{powerParam} / Power Factor", $"{powerParam} * 1 VA / Power Factor" };
            foreach (string f in tries)
            {
                try { fm.SetFormula(apparent, f); return; }
                catch { /* try the next form */ }
            }
            warnings.Add($"{apparent.Definition.Name}: Revit refused the formula from {powerParam} (unit mismatch). Enter the load per type; " +
                         "a dictionary change request for a Power-type rated-power parameter would fix it");
        }

        // ------------------------------------------------------------------ load test
        private class OverwriteLoad : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }

        /// <summary>
        /// Build failures: each warning's text goes into the family's result (every warning is shown, Manual rule) and
        /// is cleared so no dialog waits for a click; any error rolls this family back and the batch carries on.
        /// </summary>
        private class RecordFailures : IFailuresPreprocessor
        {
            private readonly JArray _warnings;
            private readonly List<string> _errors;
            public RecordFailures(JArray warnings, List<string> errors) { _warnings = warnings; _errors = errors; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                bool error = false;
                foreach (var f in a.GetFailureMessages())
                {
                    string text = f.GetDescriptionText();
                    if (f.GetSeverity() == FailureSeverity.Warning)
                    {
                        _warnings.Add("Revit warning: " + text);
                        a.DeleteWarning(f);
                    }
                    else
                    {
                        _errors.Add(text);
                        error = true;
                    }
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        private class SwallowWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        private JObject LoadTest(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active document");
            if (doc.IsFamilyDocument) throw new InvalidOperationException("Open the chain-test project (FMC_ChainTest_<Role>.rvt), not a family");
            Level? level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            var results = new JArray();
            int passed = 0, i = 0;
            foreach (JObject test in Parameters["families"] as JArray ?? new JArray())
            {
                var r = new JObject { ["family"] = test["family"] };
                var issues = new JArray();
                using (var t = new Transaction(doc, $"FMC load test {test["family"]}"))
                {
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SwallowWarnings());
                    t.SetFailureHandlingOptions(opts);
                    t.Start();
                    try
                    {
                        string path = test["path"]?.Value<string>() ?? "";
                        if (!doc.LoadFamily(path, new OverwriteLoad(), out Family family) || family == null)
                            family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                .FirstOrDefault(f => f.Name == Path.GetFileNameWithoutExtension(path))
                                ?? throw new InvalidOperationException($"Could not load {path}");
                        var symbols = family.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).ToList();
                        r["types_loaded"] = symbols.Count;
                        foreach (var s in symbols) if (!s.IsActive) s.Activate();
                        doc.Regenerate();

                        FamilyInstance? inst = null;
                        var at = new XYZ(Mm(3000) * i++, 0, 0);
                        var placement = family.FamilyPlacementType;
                        if (symbols.Count == 0) issues.Add("No types in the family");
                        else if (placement == FamilyPlacementType.OneLevelBased && level != null)
                            inst = doc.Create.NewFamilyInstance(at, symbols[0], level, StructuralType.NonStructural);
                        else if (placement == FamilyPlacementType.WorkPlaneBased || placement == FamilyPlacementType.OneLevelBased)
                            inst = doc.Create.NewFamilyInstance(at, symbols[0], StructuralType.NonStructural);
                        else
                            r["placement"] = $"{placement}: needs a host; place it by hand in the chain-test project";

                        if (inst != null)
                        {
                            doc.Regenerate();
                            var flexFailed = new JArray();
                            foreach (var s in symbols.Skip(1))
                            {
                                try { inst.ChangeTypeId(s.Id); doc.Regenerate(); }
                                catch (Exception ex) { flexFailed.Add($"{s.Name}: {ex.Message}"); }
                            }
                            if (symbols.Count > 1) inst.ChangeTypeId(symbols[0].Id);
                            r["placed"] = true;
                            r["types_flexed"] = symbols.Count - flexFailed.Count;
                            if (flexFailed.Count > 0) issues.Add(new JObject { ["flex_failed"] = flexFailed });

                            var counts = new Dictionary<string, int>();
                            var cm = inst.MEPModel?.ConnectorManager;
                            if (cm != null)
                                foreach (Connector c in cm.Connectors)
                                {
                                    string key = c.Domain == Domain.DomainElectrical ? "Electrical" : c.Domain == Domain.DomainPiping ? "Piping"
                                        : c.Domain == Domain.DomainHvac ? "Duct" : c.Domain.ToString();
                                    counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                                }
                            r["connectors"] = JObject.FromObject(counts);
                            foreach (var kv in (test["expected_connectors"] as JObject) ?? new JObject())
                            {
                                int want = kv.Value?.Value<int>() ?? 0;
                                int got = counts.TryGetValue(kv.Key, out var g) ? g : 0;
                                if (got != want) issues.Add($"{kv.Key} connectors: expected {want}, found {got}");
                            }
                            foreach (var p in (test["expected_parameters"] as JArray) ?? new JArray())
                            {
                                string pn = p.Value<string>() ?? "";
                                if (inst.LookupParameter(pn) == null && symbols[0].LookupParameter(pn) == null) issues.Add($"Parameter {pn} missing");
                            }
                        }
                        t.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                        issues.Add($"Error: {ex.Message}");
                    }
                }
                r["pass"] = issues.Count == 0;
                if (issues.Count == 0) passed++;
                else r["issues"] = issues;
                results.Add(r);
            }
            return new JObject { ["document"] = doc.Title, ["tested"] = results.Count, ["passed"] = passed, ["families"] = results,
                ["note"] = "Load test only: connecting systems (chain test pass criteria) is checked by the Family Librarian, witnessed by the Discipline Lead" };
        }
    }
}
