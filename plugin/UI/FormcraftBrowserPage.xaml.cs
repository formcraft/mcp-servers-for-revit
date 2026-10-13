using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Fmc;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// The Formcraft Browser pane (FMC_MCP_Canvas_Handoff McpPane / McpStates): status strip, section tabs (Families,
    /// Files, Activity, Guard), section body and footer. It reads Revit state pushed in by FormcraftBrowser; every action
    /// runs the MCP server's own tool code through ServerCli: dry run first, then a clear yes, then the real run.
    /// </summary>
    public partial class FormcraftBrowserPage : UserControl
    {
        private readonly DispatcherTimer _timer;
        private GuardStatus _guard;
        private List<DocState> _docs = new List<DocState>();
        private DocState Active => _docs.FirstOrDefault(d => d.IsActive);
        private readonly ObservableCollection<FamilyRow> _families = new ObservableCollection<FamilyRow>();
        private List<SpecInfo> _specs;
        private List<string> _pilot = new List<string>();
        private bool _usePilotDefault;
        private string _selectionLabel;
        private string _busy;
        private DateTime _logStamp = DateTime.MinValue;
        private int _tick;
        private bool _plannedOnce;
        private bool? _wasRunning;

        public FormcraftBrowserPage()
        {
            InitializeComponent();
            Theme.Attach(this);
            _guard = GuardStatus.Load();
            FamilyList.ItemsSource = _families;

            // A saved selection, or the kit's pilot set (read from FMC_Family_Specs.json when the pane is first shown).
            var (label, names) = BrowserSettings.Selection;
            _usePilotDefault = names == null;
            SetSelection(names ?? new List<string>(), label ?? "Pilot set");

            ShowGuard();
            ShowReadiness();
            ShowDocuments();
            ReadLog(force: true);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();

            // First plan when the pane is first shown, not at Revit start-up.
            IsVisibleChanged += async (s, e) =>
            {
                if (!IsVisible || _plannedOnce || !ServerCli.Ready) return;
                _plannedOnce = true;
                if (_usePilotDefault && await LoadSpecsAsync() && _pilot.Count > 0)
                {
                    _usePilotDefault = false;
                    SetSelection(_pilot, FamilyPickerWindow.PilotLabel(_pilot.Count));
                }
                await PlanAsync(quiet: true);
            };
        }

        // ================================================================ state in from Revit

        public void SetDocuments(List<DocState> docs)
        {
            _docs = docs;
            ShowDocuments();
            UpdateActions();
        }

        public void ShowTab(string tab)
        {
            switch (tab)
            {
                case "files": TabFiles.IsChecked = true; break;
                case "activity": TabActivity.IsChecked = true; break;
                case "guard": TabGuard.IsChecked = true; break;
                default: TabFamilies.IsChecked = true; break;
            }
        }

        /// <summary>Once a second: service state, guard and log file changes, and (every 5 s) the readiness checks.</summary>
        public void Refresh()
        {
            bool running = SocketService.Instance.IsRunning;
            if (_wasRunning != running)
            {
                _wasRunning = running;
                ConnDot.SetResourceReference(Shape.FillProperty, running ? "OkDot" : "OfflineDot");
                ConnText.Text = running ? "Claude service on" : "Claude service off";
                PortText.Text = $"port {SocketService.Instance.Port} · Revit {FormcraftBrowser.RevitVersion}";
                PortText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                StartService.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
                StartService.IsEnabled = true;
                ShowDocuments();
                UpdateActions();
            }
            var last = ActivityLog.Snapshot().FirstOrDefault();
            ConnText.ToolTip = !running ? "Claude can't reach Revit until the service listens on port 8080."
                : last == null ? "Listening on port 8080. No calls from Claude yet."
                : $"Listening on port 8080. Last call: {last.Method} at {last.Time:HH:mm:ss}" + (last.Ok ? "" : $" (failed: {last.Error})");

            string path = GuardStatus.DefaultConfigPath();
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path != _guard.ConfigPath || stamp != _guard.LastWrite) Reload();

            ReadLog(force: false);
            if (++_tick % 5 == 0) ShowReadiness();
        }

        private void Reload()
        {
            _guard = GuardStatus.Load();
            ServerCli.Locate();
            ShowGuard();
            ShowReadiness();
            ShowDocuments();
            ReadLog(force: true);
            UpdateActions();
        }

        // ================================================================ status strip

        private void ShowReadiness() => ProblemList.ItemsSource = Readiness.Check(_guard).Select(p => new ProblemVm(p)).ToList();

        private void ShowDocuments()
        {
            DocState doc = Active;
            string card, text, icon, title, line, where = "";
            if (doc == null)
            {
                card = "SegmentTrack"; text = "TextSecondary"; icon = Icons.Doc;
                title = "No document open"; line = "Open a family, a chain-test file or a project.";
            }
            else
            {
                var (ok, reason) = _guard.Verdict(doc);
                title = doc.Title;
                if (!ok)
                {
                    card = "ErrBg"; text = "ErrText"; icon = Icons.Lock;
                    line = "Live project: Claude reads, never models" + (doc.IsWorkshared ? " · workshared" : "");
                }
                else if (!_guard.LiveModelLock)
                {
                    card = "WarnBg"; text = "WarnText"; icon = Icons.LockOpen;
                    line = "Lock off in fmc_guard.json: model tools open on every file";
                }
                else
                {
                    card = "OkBg"; text = "OkText"; icon = Icons.LockOpen;
                    line = reason + ": model tools open";
                    string root = _guard.LibraryRootOf(doc.Path);
                    where = root == null ? "" : root.TrimEnd('\\', '/').Split('\\', '/').Last();
                }
            }
            DocCard.SetResourceReference(Border.BackgroundProperty, card);
            foreach (var t in new[] { DocTitle, DocLine, DocWhere }) t.SetResourceReference(TextBlock.ForegroundProperty, text);
            DocIcon.SetResourceReference(Shape.StrokeProperty, text);
            DocIcon.Data = Icons.Parse(icon);
            DocTitle.Text = title;
            DocTitle.ToolTip = doc?.Path;
            DocLine.Text = line;
            DocWhere.Text = where;

            ShowFiles();
        }

        private void StartService_Click(object sender, RoutedEventArgs e)
        {
            StartService.IsEnabled = false;
            FormcraftBrowser.RequestToggleService();
        }

        private void Fix_Click(object sender, RoutedEventArgs e)
        {
            var p = (ProblemVm)((FrameworkElement)sender).Tag;
            switch (p.Problem.Fix)
            {
                case Fix.CreateGuard: CreateGuardFromExample(); break;
                case Fix.OpenGuard: FormcraftBrowser.OpenGuardFile(); break;
                case Fix.OpenCommandSettings: OpenSettings_Click(sender, e); break;
                case Fix.LocateServer: LocateServer(); break;
            }
        }

        private void CreateGuardFromExample()
        {
            string target = GuardStatus.DefaultConfigPath();
            if (File.Exists(target)) { Reload(); return; }
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("fmc_guard.example.json"))
            {
                if (s == null) { FmcDialog.Info("Create fmc_guard.json", "The example is missing from this add-in build."); return; }
                if (!FmcDialog.Confirm("Create fmc_guard.json", $"Writes the example folder rules to\n{target}\n\n" +
                        "Then edit the folders to match this PC: the example uses Z:\\00_Standards and Z:\\Projects.",
                        null, "Create and open")) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                using (var f = File.Create(target)) s.CopyTo(f);
            }
            FormcraftBrowser.OpenGuardFile();
            Reload();
        }

        private void LocateServer()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose the MCP server folder (the one with build\\index.js), e.g. C:\\Users\\<you>\\mcp\\revit-fmc",
                ShowNewFolderButton = false,
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!File.Exists(System.IO.Path.Combine(dlg.SelectedPath, "build", "index.js")))
                {
                    FmcDialog.Info("Locate server folder", $"{dlg.SelectedPath} has no build\\index.js.");
                    return;
                }
                BrowserSettings.ServerDir = dlg.SelectedPath;
            }
            Reload();
        }

        // ================================================================ probe

        public async void RunProbe()
        {
            ProbeCard.Visibility = Visibility.Visible;
            if (!SocketService.Instance.IsRunning)
            {
                SetProbe("bad", "Probe: the Claude service is off, so Claude can't reach Revit. Start it, then probe again.");
                return;
            }
            SetProbe("ready", "Probe: asking Revit through Claude's server on port 8080…");
            CliResult r = await ServerCli.RunAsync("probe");
            if (r.Ok)
            {
                string active = r.Body["active"]?.Value<string>("title");
                int open = (r.Body["documents"] as JArray)?.Count ?? 0;
                SetProbe("done", $"Probe OK in {r.Body.Value<int>("ms")} ms: server, port 8080, command set and Revit all answer. " +
                    (active == null ? "No active document." : $"Active: {active}; {open} open."));
            }
            else
            {
                SetProbe("bad", "Probe failed: " + string.Join(" ", r.Reasons()));
            }
        }

        private void SetProbe(string kind, string text)
        {
            ProbeCard.DataContext = new { Kind = kind };
            ProbeText.Text = text;
        }

        private void CloseProbe_Click(object sender, RoutedEventArgs e) => ProbeCard.Visibility = Visibility.Collapsed;

        // ================================================================ tabs and footer

        private string Tab =>
            TabFiles.IsChecked == true ? "files" : TabActivity.IsChecked == true ? "activity" : TabGuard.IsChecked == true ? "guard" : "families";

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (FamiliesPanel == null) return; // during InitializeComponent
            FamiliesPanel.Visibility = Tab == "families" ? Visibility.Visible : Visibility.Collapsed;
            FilesPanel.Visibility = Tab == "files" ? Visibility.Visible : Visibility.Collapsed;
            ActivityPanel.Visibility = Tab == "activity" ? Visibility.Visible : Visibility.Collapsed;
            GuardPanel.Visibility = Tab == "guard" ? Visibility.Visible : Visibility.Collapsed;
            FamilyActions.Visibility = Tab == "families" ? Visibility.Visible : Visibility.Collapsed;
            ActivityActions.Visibility = Tab == "activity" ? Visibility.Visible : Visibility.Collapsed;
            if (Tab == "activity") ReadLog(force: true);
            UpdateActions();
        }

        private void SetBusy(string text)
        {
            _busy = text;
            UpdateActions();
        }

        /// <summary>Enables each action from the same facts the server checks, and says why when one is off.</summary>
        private void UpdateActions()
        {
            if (BuildButton == null) return;
            bool idle = _busy == null;
            bool service = SocketService.Instance.IsRunning;
            bool cli = ServerCli.Ready;
            int picked = _families.Count(f => f.On);
            bool dry = DryRun.IsChecked == true;

            PlanButton.IsEnabled = idle && cli && picked > 0;
            BuildButton.Content = dry ? "Build (dry run)" : $"Build {picked}";
            BuildButton.IsEnabled = idle && cli && picked > 0 && (dry || service);
            BuildButton.ToolTip = !cli ? ServerCli.Problem
                : !dry && !service ? "Start the Claude service: building runs in Revit"
                : dry ? "Report what would be built into _Staging, without building" : $"Build {picked} families into _Staging\\<Role>";

            DocState doc = Active;
            bool chainTest = doc != null && doc.Title.StartsWith("FMC_ChainTest", StringComparison.OrdinalIgnoreCase) && _guard.Verdict(doc).Ok;
            string loadWhy = !cli ? ServerCli.Problem
                : !chainTest ? "Open a chain-test file (FMC_ChainTest_<Role>.rvt from _ChainTests) as the active document"
                : !dry && !service ? "Start the Claude service: the load test runs in Revit" : null;
            LoadTestButton.IsEnabled = idle && picked > 0 && loadWhy == null;
            LoadTestButton.ToolTip = loadWhy ?? (dry ? "Report what would be loaded and placed" : $"Load, place and flex the staged families in {doc?.Title}");

            string fileWhy = !cli ? ServerCli.Problem : !service ? "Start the Claude service: file actions run in Revit" : null;
            NewFromTemplate.IsEnabled = ArchiveFile.IsEnabled = idle && fileWhy == null;
            NewFromTemplate.ToolTip = fileWhy ?? "Create a family, project or template from a template file";
            ArchiveFile.ToolTip = fileWhy ?? "Move a closed file and its backups to _Archive (nothing is deleted)";
            ShowFiles();

            string note;
            if (_busy != null) note = _busy;
            else if (Tab == "families")
                note = picked == 0 ? "Select families" : !chainTest && !dry ? $"{picked} of {_families.Count} selected · open a chain-test file to load test" : $"{picked} of {_families.Count} selected";
            else note = service ? $"Service on · port {SocketService.Instance.Port}" : "Service off";
            FooterNote.Text = note;
        }

        // ================================================================ Families

        private void SetSelection(List<string> names, string label)
        {
            _selectionLabel = label;
            SelectionText.Text = label;
            _families.Clear();
            foreach (string n in names)
            {
                var row = new FamilyRow { Name = n, Meta = "Not checked yet", Status = "—", Kind = "none" };
                row.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(FamilyRow.On)) UpdateActions(); };
                _families.Add(row);
            }
            FamiliesEmpty.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FamiliesEmpty.Text = "No families selected. Use Change… to pick them.";
            FamilyMessages.ItemsSource = null;
            UpdateActions();
        }

        private List<string> Picked() => _families.Where(f => f.On).Select(f => f.Name).ToList();

        private async void ChangeSelection_Click(object sender, RoutedEventArgs e)
        {
            if (!await LoadSpecsAsync()) return;
            var picker = new FamilyPickerWindow(_specs, _pilot, _families.Select(f => f.Name));
            if (picker.ShowDialog() != true) return;
            BrowserSettings.Selection = (picker.Label, picker.Names);
            SetSelection(picker.Names, picker.Label);
            await PlanAsync(quiet: true);
        }

        /// <summary>The families and the pilot set from the kit, through the server (the same specs file Claude reads).</summary>
        private async Task<bool> LoadSpecsAsync()
        {
            if (_specs != null) return true;
            SetBusy("Reading the family specs…");
            CliResult r = await ServerCli.RunAsync("specs");
            SetBusy(null);
            if (!r.Ok)
            {
                Messages(FamilyMessages, ("bad", "Can't read the family specs: " + string.Join(" ", r.Reasons())));
                return false;
            }
            _specs = ((JArray)r.Body["families"]).Select(f => new SpecInfo
            {
                Family = f.Value<string>("family"), Role = f.Value<string>("role"), Priority = f.Value<string>("priority"),
                Category = f.Value<string>("category"), Connectors = f.Value<int>("connectors"), Parameters = f.Value<int>("parameters"),
            }).ToList();
            _pilot = Strings(r.Body["pilot_set"]);
            if (_usePilotDefault && _pilot.Count == 0)
                FamiliesEmpty.Text = "The kit has no pilot set (FMC_Family_Specs.json pilot_set). Use Change… to pick families.";
            return true;
        }

        private void DryRun_Changed(object sender, RoutedEventArgs e) => UpdateActions();

        private async void Plan_Click(object sender, RoutedEventArgs e) => await PlanAsync(quiet: false);

        /// <summary>fmc_family plan: every check before Revit is touched. Quiet runs only refresh the chips.</summary>
        private async Task PlanAsync(bool quiet)
        {
            var names = _families.Select(f => f.Name).ToList();
            if (names.Count == 0 || _busy != null || !ServerCli.Ready) return;
            SetBusy("Planning…");
            CliResult r = await ServerCli.RunAsync("fmc_family", new JObject { ["action"] = "plan", ["families"] = JArray.FromObject(names) });
            SetBusy(null);
            if (!r.Ok && r.Body["families"] == null)
            {
                Messages(FamilyMessages, ("bad", "Plan refused: " + string.Join(" ", r.Reasons())));
                return;
            }
            var briefs = Briefs(r.Body["families"]);
            foreach (var row in _families)
            {
                if (briefs.TryGetValue(row.Name, out JObject b)) ApplyPlan(row, b);
                else { row.Status = "Unknown"; row.Kind = "bad"; row.Meta = "Not in FMC_Family_Specs.json"; row.Tip = "Not in the kit's family specs"; }
            }
            if (quiet) { FamilyMessages.ItemsSource = null; return; }
            var msgs = new List<(string, string)>();
            var problems = _families.Where(f => f.Kind == "bad").ToList();
            if (problems.Count > 0)
                msgs.Add(("bad", $"{problems.Count} can't be built yet:\n" + Bullets(problems.SelectMany(f => f.Problems.Select(p => $"{Short(f.Name)}: {p}")))));
            var notes = _families.SelectMany(f => f.Notes.Select(n => $"{Short(f.Name)}: {n}")).ToList();
            if (notes.Count > 0) msgs.Add(("warn", "Notes for the Family Librarian:\n" + Bullets(notes)));
            int ready = _families.Count(f => f.Kind == "ready" || f.Kind == "warn");
            msgs.Insert(0, (ready > 0 ? "done" : "none", $"Plan: {ready} ready to build, {_families.Count(f => f.Status == "In staging")} already in staging, {problems.Count} with problems."));
            Messages(FamilyMessages, msgs.ToArray());
        }

        private static Dictionary<string, JObject> Briefs(JToken families) =>
            (families as JArray)?.OfType<JObject>().GroupBy(b => b.Value<string>("family")).ToDictionary(g => g.Key, g => g.First())
            ?? new Dictionary<string, JObject>();

        private static List<string> Strings(JToken t) => (t as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>();

        private static bool IsInStaging(string problem) => problem.Contains("exists; set overwrite");

        private void ApplyPlan(FamilyRow row, JObject b)
        {
            row.Problems = Strings(b["problems"]);
            row.Notes = Strings(b["notes"]);
            int connectors = (b["connectors"] as JArray)?.Count ?? 0;
            int parameters = b.Value<int?>("shared_parameters") ?? 0;
            row.Meta = $"{RoleOf(row.Name)} · {Count(connectors, "connector")} · {Count(parameters, "parameter")}";
            var real = row.Problems.Where(p => !IsInStaging(p)).ToList();
            if (real.Count > 0) { row.Status = "Problem"; row.Kind = "bad"; }
            else if (row.Problems.Count > 0) { row.Status = "In staging"; row.Kind = "done"; }
            else if (row.Notes.Count > 0) { row.Status = Count(row.Notes.Count, "note"); row.Kind = "warn"; }
            else { row.Status = "Ready"; row.Kind = "ready"; }
            row.Tip = string.Join("\n", row.Problems.Concat(row.Notes).DefaultIfEmpty("No problems or notes"));
        }

        private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync(DryRun.IsChecked == true, typedYes: false, oneByOne: false);

        /// <summary>The selection a dry run passed for, so a typed "yes" can only confirm what was just shown.</summary>
        private string _buildDryRunFor, _loadDryRunFor;
        private string SelectionKey() => string.Join("|", Picked());

        /// <param name="typedYes">"yes build" in the command box is the user's yes: no dialog, but only after a dry run of this selection.</param>
        /// <param name="oneByOne">One Revit call per family, so a problem shows exactly which family caused it.</param>
        private async Task BuildAsync(bool dry, bool typedYes, bool oneByOne)
        {
            var names = Picked();
            if (names.Count == 0) { Messages(FamilyMessages, ("warn", "No families selected.")); return; }
            var args = new JObject { ["action"] = "build", ["families"] = JArray.FromObject(names), ["dry_run"] = true };
            if (typedYes && _buildDryRunFor != SelectionKey())
            {
                await BuildAsync(true, false, false);
                if (_buildDryRunFor == SelectionKey())
                    AddMessage(FamilyMessages, "ready", "Dry run done. Type \"yes build\" (or \"yes build one by one\") again to build for real.");
                return;
            }

            if (dry)
            {
                SetBusy("Dry run…");
                CliResult d = await ServerCli.RunAsync("fmc_family", args);
                SetBusy(null);
                if (!d.Ok) { Messages(FamilyMessages, ("bad", "Build refused: " + string.Join(" ", d.Reasons()))); return; }
                var would = Briefs(d.Body["would_build"]);
                var skipped = Briefs(d.Body["skipped"]);
                foreach (var row in _families.Where(f => f.On))
                    if (would.TryGetValue(row.Name, out JObject b) || skipped.TryGetValue(row.Name, out b)) ApplyPlan(row, b);
                var msgs = new List<(string, string)> { ("ready", $"Dry run: would build {would.Count} into {_guard.FamilyStagingRoot}\\<Role>." +
                    (skipped.Count > 0 ? $" {skipped.Count} skipped (see their chips)." : "") + " Untick Dry run, then Build to run it.") };
                if (skipped.Count > 0) msgs.Add(("warn", "Skipped:\n" + Bullets(skipped.Values.SelectMany(b => Strings(b["problems"]).Select(p => $"{Short(b.Value<string>("family"))}: {p}")))));
                Messages(FamilyMessages, msgs.ToArray());
                _buildDryRunFor = would.Count > 0 ? SelectionKey() : null;
                return;
            }

            bool overwrite = false;
            if (!typedYes)
            {
            // Real build from the button: show what will happen and ask again (Manual 17.2).
            bool anyStaged = _families.Any(f => f.On && f.Status == "In staging");
            int choice = FmcDialog.Ask($"Build {names.Count} families?",
                $"Creates data-shell families in {_guard.FamilyStagingRoot}\\<Role>. Families with problems are skipped. " +
                "Release stays with the Family Librarian.",
                names, $"Build {names.Count}", null,
                anyStaged ? "Rebuild families already in staging (the old file is replaced; Revit keeps its backups)" : null,
                out overwrite);
            if (choice != 1) return;
            }
            args["dry_run"] = false;
            if (overwrite) { args["overwrite"] = true; args["confirm"] = true; }

            var batches = oneByOne ? names.Select(n => new List<string> { n }).ToList() : new List<List<string>> { names };
            var results = new JArray();
            var refusals = new List<string>();
            int step = 0;
            foreach (var batch in batches)
            {
                step++;
                args["families"] = JArray.FromObject(batch);
                SetBusy(oneByOne ? $"Building {step} of {batches.Count}: {Short(batch[0])}…" : $"Building {names.Count} families in Revit…");
                CliResult r = await ServerCli.RunAsync("fmc_family", args);
                SetBusy(null);
                if (!r.Ok) { refusals.Add((oneByOne ? Short(batch[0]) + ": " : "") + string.Join(" ", r.Reasons())); continue; }
                foreach (var f in (r.Body["result"]?["families"] as JArray ?? new JArray())) results.Add(f);
            }
            _buildDryRunFor = null;
            if (results.Count == 0) { Messages(FamilyMessages, ("bad", "Build refused: " + string.Join("\n", refusals))); return; }
            var warnings = new List<string>();
            foreach (JObject f in results.OfType<JObject>())
            {
                var row = _families.FirstOrDefault(x => x.Name == f.Value<string>("family"));
                if (row == null) continue;
                bool failed = f["error"] != null;
                row.Status = failed ? "Failed" : "Built";
                row.Kind = failed ? "bad" : "done";
                var w = Strings(f["warnings"]);
                warnings.AddRange(w.Select(x => $"{Short(row.Name)}: {x}"));
                row.Tip = failed ? f.Value<string>("error") : $"Saved to {f.Value<string>("saved")}" + (w.Count > 0 ? "\n" + string.Join("\n", w) : "");
            }
            int failedCount = results.OfType<JObject>().Count(f => f["error"] != null), built = results.Count - failedCount;
            var done = new List<(string, string)> { (failedCount > 0 ? "warn" : "done", $"Built {built}" + (failedCount > 0 ? $", {failedCount} failed (hover the chip for why)." : ".") + " Logged in FMC_MCP_FileLog.csv.") };
            // Every warning is shown: they are the hand steps left for the Family Librarian.
            if (warnings.Count > 0) done.Add(("warn", "Hand steps left for the Family Librarian:\n" + Bullets(warnings, 30)));
            if (refusals.Count > 0) done.Add(("bad", "Refused:\n" + Bullets(refusals)));
            Messages(FamilyMessages, done.ToArray());
        }

        private async void LoadTest_Click(object sender, RoutedEventArgs e) => await LoadTestAsync(DryRun.IsChecked == true, typedYes: false);

        private async Task LoadTestAsync(bool dry, bool typedYes)
        {
            var names = Picked();
            if (names.Count == 0) { Messages(FamilyMessages, ("warn", "No families selected.")); return; }
            if (typedYes && _loadDryRunFor != SelectionKey())
            {
                await LoadTestAsync(true, false);
                if (_loadDryRunFor == SelectionKey())
                    AddMessage(FamilyMessages, "ready", "Dry run done. Type \"yes load test\" again to run it in Revit.");
                return;
            }
            var args = new JObject { ["action"] = "load_test", ["families"] = JArray.FromObject(names), ["dry_run"] = true };
            if (!dry)
            {
                if (!typedYes && !FmcDialog.Confirm($"Load test in {Active?.Title}?",
                        "Loads the staged families into the active chain-test project, places one of each type, flexes them, " +
                        "and checks connectors and parameters.", names, "Load test")) return;
                args["dry_run"] = false;
            }
            SetBusy(dry ? "Dry run…" : $"Load testing {names.Count} families in Revit…");
            CliResult r = await ServerCli.RunAsync("fmc_family", args);
            SetBusy(null);
            if (!r.Ok) { Messages(FamilyMessages, ("bad", "Load test refused: " + string.Join(" ", r.Reasons()))); return; }

            var missing = Strings(r.Body["missing"]);
            if (dry)
            {
                var would = Strings(r.Body["would_load_and_place"]);
                _loadDryRunFor = would.Count > 0 ? SelectionKey() : null;
                Messages(FamilyMessages, ("ready", $"Dry run: would load and place {would.Count}." +
                    (missing.Count > 0 ? $" Not in staging yet (build first): {string.Join(", ", missing.Select(Short))}." : "") +
                    " Untick Dry run, then Load test to run it."));
                return;
            }
            var results = r.Body["result"]?["families"] as JArray ?? new JArray();
            foreach (JObject f in results.OfType<JObject>())
            {
                var row = _families.FirstOrDefault(x => x.Name == f.Value<string>("family"));
                if (row == null) continue;
                bool pass = f.Value<bool?>("pass") == true;
                row.Status = pass ? "Load tested" : "Fail";
                row.Kind = pass ? "done" : "bad";
                row.Tip = pass ? "Loaded, placed and flexed; connectors and parameters as expected" : f["issues"]?.ToString(Newtonsoft.Json.Formatting.None) ?? f.ToString();
            }
            int tested = r.Body["result"]?.Value<int?>("tested") ?? results.Count, passed = r.Body["result"]?.Value<int?>("passed") ?? 0;
            var msgs = new List<(string, string)> { (passed == tested ? "done" : "warn", $"Load test: {passed} of {tested} passed." +
                (missing.Count > 0 ? $" Not in staging: {string.Join(", ", missing.Select(Short))}." : "")) };
            string note = r.Body["result"]?.Value<string>("note");
            if (!string.IsNullOrEmpty(note)) msgs.Add(("none", note));
            Messages(FamilyMessages, msgs.ToArray());
        }

        // ================================================================ command box

        private const string CommandHelp =
            "Commands:\n" +
            "• plan: check the selected families against the kit\n" +
            "• dry run: what a build would do, without building\n" +
            "• yes build: build after a dry run (yes build one by one: one Revit call per family)\n" +
            "• load test, then yes load test: in the active FMC_ChainTest file\n" +
            "• pilot: select the kit's pilot set · probe: test the connection · start service\n" +
            "• ask <question>: ask Claude about the open model (test build, read-only, uses the firm's API key) · ask reset";

        private void CommandBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            RunCommand();
        }

        private void CommandBox_TextChanged(object sender, TextChangedEventArgs e) =>
            CommandHint.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RunCommand_Click(object sender, RoutedEventArgs e) => RunCommand();

        /// <summary>
        /// Fixed commands, matched exactly (no AI): each one calls the same method as its button. Typing "yes ..." is the
        /// user's confirmation, so it replaces the dialog, and it only acts on a selection whose dry run just passed.
        /// </summary>
        private async void RunCommand()
        {
            string raw = CommandBox.Text.Trim();
            if (raw.Length == 0) return;
            // "ask …", "claude …" or "? …" goes to Claude; everything else is a fixed command, matched exactly.
            var ask = Regex.Match(raw, @"^(?:(?:ask|claude)(?:\s+(?<q>.*))?|\?\s*(?<q>.*))$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (ask.Success)
            {
                CommandBox.Text = "";
                TabFamilies.IsChecked = true;
                await AskAsync(ask.Groups["q"].Value.Trim());
                return;
            }
            string c = Regex.Replace(Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9 ]", " "), @"\bplease\b", " ");
            c = Regex.Replace(c, @"\s+", " ").Trim();
            CommandBox.Text = "";
            TabFamilies.IsChecked = true;
            if (_busy != null) { Messages(FamilyMessages, ("warn", $"Busy ({_busy}). Try again when it finishes.")); return; }

            bool real = c.StartsWith("yes ");
            if (real && !SocketService.Instance.IsRunning)
            {
                Messages(FamilyMessages, ("warn", "The Claude service is off, so Revit can't be reached. Type \"start service\" (or use the Formcraft tab), then try again."));
                return;
            }
            switch (c)
            {
                case "help": case "": Messages(FamilyMessages, ("none", CommandHelp)); break;
                case "plan": await PlanAsync(quiet: false); break;
                case "dry run": case "build": case "build dry run": case "dry run build":
                    await BuildAsync(dry: true, typedYes: false, oneByOne: false); break;
                case "yes build": case "yes build all":
                    await BuildAsync(dry: false, typedYes: true, oneByOne: false); break;
                case "yes build one by one": case "yes build one at a time":
                    await BuildAsync(dry: false, typedYes: true, oneByOne: true); break;
                case "load test": case "dry run load test": case "load test dry run":
                    await LoadTestAsync(dry: true, typedYes: false); break;
                case "yes load test":
                    await LoadTestAsync(dry: false, typedYes: true); break;
                case "probe": RunProbe(); break;
                case "pilot": case "select pilot": case "pilot set":
                    if (await LoadSpecsAsync() && _pilot.Count > 0)
                    {
                        string label = FamilyPickerWindow.PilotLabel(_pilot.Count);
                        BrowserSettings.Selection = (label, _pilot);
                        SetSelection(_pilot, label);
                        await PlanAsync(quiet: true);
                    }
                    break;
                case "start service":
                    if (SocketService.Instance.IsRunning) Messages(FamilyMessages, ("done", "The Claude service is already on."));
                    else FormcraftBrowser.RequestToggleService();
                    break;
                default:
                    Messages(FamilyMessages, ("warn", $"Unknown command \"{raw}\". " + CommandHelp)); break;
            }
        }

        // ================================================================ Ask Claude (test build, read-only)

        /// <summary>Previous questions and answers this session, so a follow-up ("and on level 2?") has context.</summary>
        private readonly List<(string Role, string Text)> _askHistory = new List<(string Role, string Text)>();

        /// <summary>
        /// "ask &lt;question&gt;": the server's ask command (fmc/ask.ts) calls the Claude API with the firm's own API key and lets
        /// Claude use read-only Revit tools only. Billed per token on the key's Claude Console account, not the Claude app
        /// plan. Nothing here can change the model: changes still go through the buttons and their dry run.
        /// </summary>
        private async Task AskAsync(string question)
        {
            if (_busy != null) { Messages(FamilyMessages, ("warn", $"Busy ({_busy}). Try again when it finishes.")); return; }
            if (question.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _askHistory.Clear();
                Messages(FamilyMessages, ("done", "Ask Claude: conversation cleared."));
                return;
            }
            if (question.Length == 0)
            {
                Messages(FamilyMessages, ("none", "Type a question after ask, e.g. \"ask how many sheets and views are in this model?\" " +
                    "Claude can read the open model but not change it (test build)."));
                return;
            }
            if (!SocketService.Instance.IsRunning)
                Messages(FamilyMessages, ("warn", "The Claude service is off, so Claude can't read the model. It will answer from the question only. Type \"start service\" to connect."));

            var history = new JArray(_askHistory.Select(h => new JObject { ["role"] = h.Role, ["text"] = h.Text }));
            SetBusy("Asking Claude…");
            CliResult r = await ServerCli.RunAsync("ask", new JObject { ["question"] = question, ["history"] = history });
            SetBusy(null);
            if (!r.Ok)
            {
                Messages(FamilyMessages, ("bad", "Ask Claude: " + string.Join(" ", r.Reasons())));
                return;
            }
            string answer = r.Body.Value<string>("answer") ?? "";
            _askHistory.Add(("user", question));
            _askHistory.Add(("assistant", answer));
            if (_askHistory.Count > 12) _askHistory.RemoveRange(0, _askHistory.Count - 12);

            var tools = (r.Body["tools_used"] as JArray)?.Select(t => t.ToString()).Distinct().ToList() ?? new List<string>();
            int inTok = r.Body["usage"]?.Value<int?>("input_tokens") ?? 0, outTok = r.Body["usage"]?.Value<int?>("output_tokens") ?? 0;
            string meta = $"Read: {(tools.Count == 0 ? "nothing in Revit" : string.Join(", ", tools))} · {inTok + outTok:N0} tokens · " +
                          $"{r.Body.Value<string>("model")} · today's budget left {r.Body.Value<int?>("budget_left") ?? 0:N0}";
            Messages(FamilyMessages, ("none", "You: " + question), ("done", answer), ("none", meta));
        }

        // ================================================================ Files

        private void ShowFiles()
        {
            if (DocList == null) return;
            bool service = SocketService.Instance.IsRunning;
            bool idle = _busy == null;
            DocList.ItemsSource = _docs.Select(d => new DocRow(d, _guard, service, idle)).ToList();
            DocsEmpty.Visibility = _docs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var row = (DocRow)((FrameworkElement)sender).Tag;
            string ext = row.Doc.IsFamily ? ".rfa" : row.Doc.Path.EndsWith(".rte", StringComparison.OrdinalIgnoreCase) ? ".rte" : ".rvt";
            string dir = !string.IsNullOrEmpty(row.Doc.Path) && _guard.WriteRoots.Any(r => GuardStatus.IsInside(row.Doc.Path, r))
                ? System.IO.Path.GetDirectoryName(row.Doc.Path) : _guard.FirstWritableFolder();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save {row.Title} as",
                Filter = $"Revit file (*{ext})|*{ext}",
                FileName = System.IO.Path.GetFileNameWithoutExtension(row.Title) + ext,
                InitialDirectory = dir ?? "",
                OverwritePrompt = false, // the server refuses to overwrite unless asked twice
            };
            if (dlg.ShowDialog() != true) return;
            await RunFileAction(new JObject { ["action"] = "save_as", ["document"] = row.Doc.Reference, ["save_path"] = dlg.FileName },
                $"Save {row.Title} as {System.IO.Path.GetFileName(dlg.FileName)}?", "Save");
        }

        private async void Close_Click(object sender, RoutedEventArgs e)
        {
            var row = (DocRow)((FrameworkElement)sender).Tag;
            bool save = false;
            if (row.Doc.IsModified)
            {
                int c = FmcDialog.Ask($"Close {row.Title}?", "It has unsaved changes.", null,
                    "Close without saving", row.Doc.IsWorkshared || string.IsNullOrEmpty(row.Doc.Path) ? null : "Save and close", null, out _);
                if (c == 0) return;
                save = c == 2;
            }
            await RunFileAction(new JObject { ["action"] = "close", ["document"] = row.Doc.Reference, ["save"] = save }, null, null);
        }

        private async void NewFromTemplate_Click(object sender, RoutedEventArgs e)
        {
            var w = new NewFromTemplateWindow(_guard);
            if (w.ShowDialog() != true) return;
            await RunFileAction(new JObject
            {
                ["action"] = "create_from_template", ["kind"] = w.Kind, ["template_path"] = w.TemplatePath,
                ["save_path"] = w.SavePath, ["close_after"] = w.CloseAfter,
            }, $"Create {System.IO.Path.GetFileName(w.SavePath)}?", "Create");
        }

        private async void Archive_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Archive a closed file (moves it and its backups to _Archive)",
                Filter = "Revit files (*.rvt;*.rfa;*.rte)|*.rvt;*.rfa;*.rte",
                InitialDirectory = _guard.FirstWritableFolder() ?? "",
            };
            if (dlg.ShowDialog() != true) return;
            await RunFileAction(new JObject { ["action"] = "archive", ["path"] = dlg.FileName },
                $"Archive {System.IO.Path.GetFileName(dlg.FileName)}?", "Archive");
        }

        /// <summary>
        /// fmc_file, the way Claude must use it: dry run, show the result and ask (confirmTitle null: no question, used
        /// for closing a file the user already chose to close), then the real run. An existing target asks once more.
        /// </summary>
        private async Task RunFileAction(JObject args, string confirmTitle, string confirmLabel)
        {
            string action = args.Value<string>("action");
            args["dry_run"] = true;
            SetBusy("Dry run…");
            CliResult d = await ServerCli.RunAsync("fmc_file", args);
            SetBusy(null);

            if (!d.Ok && d.Reasons().Any(IsInStaging) && d.Reasons().All(x => IsInStaging(x) || x.Contains("overwrite needs confirm")))
            {
                if (!FmcDialog.Confirm("Replace the existing file?", args.Value<string>("save_path"),
                        new[] { "The file already exists. Replacing it needs a second yes." }, "Replace")) return;
                args["overwrite"] = true; args["confirm"] = true;
                SetBusy("Dry run…");
                d = await ServerCli.RunAsync("fmc_file", args);
                SetBusy(null);
            }
            if (!d.Ok) { Messages(FileMessages, ("bad", "Refused: " + string.Join(" ", d.Reasons()))); return; }

            if (confirmTitle != null)
            {
                var details = new List<string>();
                if (d.Body["would_do"] is JObject would)
                    details.AddRange(would.Properties().Where(p => p.Value.Type != JTokenType.Null && p.Value.ToString().Length > 0)
                        .Select(p => $"{p.Name}: {p.Value}"));
                if (d.Body["would_move"] is JArray moves)
                    details.AddRange(moves.OfType<JArray>().Select(m => $"{System.IO.Path.GetFileName(m[0].ToString())} → {m[1]}"));
                if (!FmcDialog.Confirm(confirmTitle, "The dry run passed the folder rules and name checks. Run it for real?", details, confirmLabel)) return;
            }
            args["dry_run"] = false;
            if (action == "archive") args["confirm"] = true;

            SetBusy("Working in Revit…");
            CliResult r = await ServerCli.RunAsync("fmc_file", args);
            SetBusy(null);
            Messages(FileMessages, r.Ok
                ? ("done", $"{FileLog.Describe(action, false)}: done. Logged in FMC_MCP_FileLog.csv.")
                : ("bad", $"{FileLog.Describe(action, false)} failed: " + string.Join(" ", r.Reasons())));
        }

        // ================================================================ Activity

        private void ReadLog(bool force)
        {
            string file = FileLog.PathFor(_guard);
            DateTime stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (!force && stamp == _logStamp) return;
            _logStamp = stamp;
            List<LogRow> rows;
            try { rows = FileLog.ReadLast(file, 60); }
            catch (Exception) { rows = new List<LogRow>(); }
            LogList.ItemsSource = rows.Select(r => new LogVm(r)).ToList();
            LogEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e) => FormcraftBrowser.OpenReports(_guard);

        // ================================================================ Guard

        private void ShowGuard()
        {
            var g = _guard;
            string Exists(string p) => File.Exists(GuardStatus.ExpandEnv(p)) ? p : p + "\n(not found)";
            string Lines(List<string> l, string none) => l.Count == 0 ? none : string.Join("\n", l);
            var rows = new List<GuardVm>
            {
                new GuardVm("Config", g.Found ? g.ConfigPath : g.ConfigPath + "\n(" + g.Problem + ")"),
                new GuardVm("Write folders", Lines(g.WriteRoots, "None: every write is refused")),
                new GuardVm("Library folders", Lines(g.LibraryRoots, "None: model tools run only on families and new files")),
                new GuardVm("Template folders", Lines(g.TemplateRoots, "None")),
                new GuardVm("Never written", string.Join(" · ", g.BlockedSegments) + "\n04_Families\\<Role>"),
                new GuardVm("Live-model lock", g.LiveModelLock ? "On" : "Off (not recommended: Manual Part 18)"),
                new GuardVm("Family staging", g.FamilyStagingRoot),
                new GuardVm("Family specs", Exists(g.SpecsPath)),
                new GuardVm("Shared parameters", Exists(g.SharedParameterFile)),
                new GuardVm("Reports", GuardStatus.ExpandEnv(g.ReportsDir)),
                new GuardVm("Tool profile", ServerCli.ToolProfile == "full" ? "Full (every upstream tool)" : "FMC (10 upstream tools hidden)"),
                new GuardVm("MCP server", ServerCli.ServerDir == null ? "Not found" : $"{ServerCli.ServerDir}\n(from {ServerCli.FoundVia})"),
                new GuardVm("Node.js", ServerCli.NodePath ?? "Not found"),
            };
            GuardList.ItemsSource = rows;
        }

        private void OpenGuard_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(GuardStatus.DefaultConfigPath())) FormcraftBrowser.OpenGuardFile();
            else CreateGuardFromExample();
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            Reload();
            _specs = null;
            _ = PlanAsync(quiet: true);
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow();
            _ = new System.Windows.Interop.WindowInteropHelper(window) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        // ================================================================ helpers

        private static void Messages(ItemsControl target, params (string Kind, string Text)[] items) =>
            target.ItemsSource = items.Select(i => new { Kind = i.Kind, Text = i.Text }).ToList();

        private static void AddMessage(ItemsControl target, string kind, string text)
        {
            var items = (target.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
            items.Add(new { Kind = kind, Text = text });
            target.ItemsSource = items;
        }

        private static string Bullets(IEnumerable<string> lines, int max = 12)
        {
            var l = lines.ToList();
            return string.Join("\n", l.Take(max).Select(x => "• " + x)) + (l.Count > max ? $"\n… and {l.Count - max} more" : "");
        }

        private static string Short(string family) => family.StartsWith("FMC_") ? family.Substring(4) : family;
        private static string RoleOf(string family) => family.Split('_').Skip(1).FirstOrDefault() ?? "";
        private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    }

    // ==================================================================== row models

    public sealed class FamilyRow : INotifyPropertyChanged
    {
        private bool _on = true;
        private string _status, _kind, _meta, _tip;
        public string Name { get; set; }
        public List<string> Problems { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
        public bool On { get => _on; set { _on = value; Changed(nameof(On)); } }
        public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
        public string Kind { get => _kind; set { _kind = value; Changed(nameof(Kind)); } }
        public string Meta { get => _meta; set { _meta = value; Changed(nameof(Meta)); } }
        public string Tip { get => _tip; set { _tip = value; Changed(nameof(Tip)); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public sealed class DocRow
    {
        public DocState Doc { get; }
        public string Title => Doc.Title;
        public string Path => Doc.Path;
        public string Badge { get; }
        public string Kind { get; }
        public bool CanSave { get; }
        public bool CanClose { get; }
        public string SaveTip { get; }
        public string CloseTip { get; }

        public DocRow(DocState d, GuardStatus guard, bool service, bool idle)
        {
            Doc = d;
            var (ok, reason) = guard.Verdict(d);
            var parts = new List<string>();
            if (d.IsFamily)
            {
                string root = guard.LibraryRootOf(d.Path);
                parts.Add("Family");
                if (root != null) parts.Add(root.TrimEnd('\\', '/').Split('\\', '/').Last() + "\\" + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(d.Path)));
                else if (string.IsNullOrEmpty(d.Path)) parts.Add("not saved");
                Kind = "none";
            }
            else if (string.IsNullOrEmpty(d.Path)) { parts.Add("New file · not saved"); Kind = "none"; }
            else if (ok && guard.LiveModelLock) { parts.Add("Library file"); Kind = "done"; }
            else if (ok) { parts.Add("Project · lock off"); Kind = "none"; }
            else { parts.Add("Live project"); if (d.IsWorkshared) parts.Add("workshared"); parts.Add("read-only for Claude"); Kind = "bad"; }
            if (d.IsActive) parts.Add("active");
            if (d.IsModified) parts.Add("unsaved changes");
            Badge = string.Join(" · ", parts);

            string off = !ServerCli.Ready ? ServerCli.Problem : !service ? "Start the Claude service first" : !idle ? "Busy" : null;
            CanSave = off == null && !d.IsWorkshared;
            SaveTip = d.IsWorkshared ? "Workshared models are never saved by a tool: synchronise with central in Revit" : off ?? "Save a copy to a WIP or staging folder (dry run first)";
            CanClose = off == null && !d.IsActive;
            CloseTip = d.IsActive ? "Revit can't close the active document: switch to another view first" : off ?? "Close this document";
        }
    }

    public sealed class LogVm
    {
        public string Time { get; }
        public string Action { get; }
        public string Target { get; }
        public string Result { get; }
        public string Kind { get; }
        public string Tip { get; }

        public LogVm(LogRow r)
        {
            Time = r.Time == default ? "" : r.Time.Date == DateTime.Today ? r.Time.ToString("HH:mm") : r.Time.ToString("dd MMM");
            Action = FileLog.Describe(r.Action, r.DryRun);
            Target = r.Target;
            switch (r.Result)
            {
                case "done": Result = "Done"; Kind = "done"; break;
                case "refused": Result = "Refused"; Kind = "bad"; break;
                case "error": Result = "Error"; Kind = "bad"; break;
                default: Result = r.Result; Kind = "ready"; break;
            }
            string detail = r.Detail ?? "";
            Tip = $"{r.Time:yyyy-MM-dd HH:mm:ss}  {Action}\n{r.Target}" + (detail.Length > 0 ? "\n" + (detail.Length > 600 ? detail.Substring(0, 600) + "…" : detail) : "");
        }
    }

    public sealed class GuardVm
    {
        public string Label { get; }
        public string Value { get; }
        public GuardVm(string label, string value) { Label = label; Value = value; }
    }

    public sealed class ProblemVm
    {
        public ReadinessProblem Problem { get; }
        public string Title => Problem.Title;
        public string Text => Problem.Text;
        public string Kind => Problem.Blocking ? "warn" : "none";
        public string FixLabel => Problem.FixLabel;
        public Visibility FixVisibility => Problem.Fix == Fix.None ? Visibility.Collapsed : Visibility.Visible;
        public ProblemVm(ReadinessProblem p) { Problem = p; }
    }
}
