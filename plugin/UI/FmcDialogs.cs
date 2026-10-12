using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using revit_mcp_plugin.Fmc;

namespace revit_mcp_plugin.UI
{
    /// <summary>Base for the Formcraft Browser's small windows: tokens, styles, Revit as owner, Esc to cancel.</summary>
    public class FmcWindow : Window
    {
        public FmcWindow(string title, double width)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/RevitMCPPlugin;component/UI/FormcraftStyles.xaml", UriKind.Relative)
            });
            Theme.Attach(this);
            SetResourceReference(BackgroundProperty, "Surface");
            SetResourceReference(ForegroundProperty, "Text");
            SetResourceReference(FontFamilyProperty, "Sans");
            FontSize = 13;
            new WindowInteropHelper(this) { Owner = Process.GetCurrentProcess().MainWindowHandle };
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        protected static TextBlock Text(string s, string style = null, double top = 0)
        {
            var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            if (style != null) t.SetResourceReference(StyleProperty, style);
            return t;
        }

        protected Button MakeButton(string label, bool primary, RoutedEventHandler click)
        {
            var b = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0), MinWidth = 76 };
            if (primary) b.SetResourceReference(StyleProperty, "Primary");
            b.Click += click;
            return b;
        }

        /// <summary>Footer bar with buttons on the right, like the pane's footer.</summary>
        protected Border Footer(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in buttons) row.Children.Add(b);
            var footer = new Border { Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
            footer.SetResourceReference(Border.BackgroundProperty, "SurfaceMuted");
            footer.SetResourceReference(Border.BorderBrushProperty, "Divider");
            return footer;
        }
    }

    /// <summary>The "ask again" step before any real write (Manual 17.2): what will happen, then a clear yes.</summary>
    public sealed class FmcDialog : FmcWindow
    {
        public int Choice { get; private set; } // 0 cancel, 1 primary, 2 secondary
        public bool Checked => _check?.IsChecked == true;
        private readonly CheckBox _check;

        private FmcDialog(string title, string message, IEnumerable<string> details, string primary, string secondary, string checkbox)
            : base(title, 440)
        {
            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(message)) body.Children.Add(Text(message, null, 6));
            var lines = details?.Where(d => !string.IsNullOrEmpty(d)).ToList() ?? new List<string>();
            if (lines.Count > 0)
            {
                var box = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0) };
                box.SetResourceReference(Border.BackgroundProperty, "SurfaceMuted");
                var list = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, LineHeight = 17 };
                list.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
                list.Text = string.Join("\n", lines.Take(14)) + (lines.Count > 14 ? $"\n… and {lines.Count - 14} more" : "");
                box.Child = new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                body.Children.Add(box);
            }
            if (checkbox != null)
            {
                _check = new CheckBox { Content = new TextBlock { Text = checkbox, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 12, 0, 0) };
                body.Children.Add(_check);
            }

            var buttons = new List<Button> { MakeButton("Cancel", false, (s, e) => { Choice = 0; Close(); }) };
            if (secondary != null) buttons.Add(MakeButton(secondary, false, (s, e) => { Choice = 2; Close(); }));
            var main = MakeButton(primary, true, (s, e) => { Choice = 1; Close(); });
            buttons.Add(main);

            var root = new DockPanel();
            var footer = Footer(buttons.ToArray());
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(body);
            Content = root;
            Loaded += (s, e) => main.Focus();
        }

        /// <summary>Returns 0 (cancel), 1 (primary) or 2 (secondary); checked is the checkbox, when one is shown.</summary>
        public static int Ask(string title, string message, IEnumerable<string> details, string primary,
            string secondary, string checkbox, out bool isChecked)
        {
            var d = new FmcDialog(title, message, details, primary, secondary, checkbox);
            d.ShowDialog();
            isChecked = d.Checked;
            return d.Choice;
        }

        public static bool Confirm(string title, string message, IEnumerable<string> details, string primary) =>
            Ask(title, message, details, primary, null, null, out _) == 1;

        public static void Info(string title, string message, IEnumerable<string> details = null) =>
            Ask(title, message, details, "OK", null, null, out _);
    }

    public sealed class SpecInfo
    {
        public string Family { get; set; }
        public string Role { get; set; }
        public string Priority { get; set; }
        public string Category { get; set; }
        public int Connectors { get; set; }
        public int Parameters { get; set; }
    }

    /// <summary>Picks the families the Families section works on: by role, priority and name, at most 25 (one batch).</summary>
    public sealed class FamilyPickerWindow : FmcWindow
    {
        public const int Max = 25;

        public List<string> Names { get; private set; }
        public string Label { get; private set; }

        private readonly List<SpecInfo> _all;
        private readonly List<string> _pilot;
        private readonly HashSet<string> _picked;
        private readonly ComboBox _role = new ComboBox { MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _priority = new ComboBox { MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _ok;

        /// <param name="pilot">The kit's pilot set (FMC_Family_Specs.json pilot_set); empty hides the button.</param>
        public FamilyPickerWindow(List<SpecInfo> all, List<string> pilot, IEnumerable<string> current) : base("Choose families", 520)
        {
            _all = all;
            _pilot = pilot ?? new List<string>();
            _picked = new HashSet<string>(current ?? Enumerable.Empty<string>());

            _role.Items.Add("All roles");
            foreach (var r in all.Select(s => s.Role).Distinct().OrderBy(r => r)) _role.Items.Add(r);
            _priority.Items.Add("All priorities");
            foreach (var p in new[] { "P1", "P2", "P3" }) _priority.Items.Add(p);
            _role.SelectedIndex = 0; _priority.SelectedIndex = 0;
            _role.SelectionChanged += (s, e) => Fill();
            _priority.SelectionChanged += (s, e) => Fill();
            _search.TextChanged += (s, e) => Fill();
            _search.ToolTip = "Filter by name or category";

            var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_role, Dock.Left); DockPanel.SetDock(_priority, Dock.Left);
            filters.Children.Add(_role); filters.Children.Add(_priority); filters.Children.Add(_search);

            var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (_pilot.Count > 0)
                quick.Children.Add(SmallButton("Pilot set", () => { _picked.Clear(); foreach (var n in _pilot) _picked.Add(n); Fill(); }));
            quick.Children.Add(SmallButton("Select shown", () => { foreach (var s in Shown().Take(Max)) _picked.Add(s.Family); Fill(); }));
            quick.Children.Add(SmallButton("Clear", () => { _picked.Clear(); Fill(); }));

            var listBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Height = 340 };
            listBox.SetResourceReference(Border.BorderBrushProperty, "BorderPane");
            listBox.Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) };

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
            body.Children.Add(filters);
            body.Children.Add(quick);
            body.Children.Add(listBox);
            _count.SetResourceReference(StyleProperty, "Label");
            body.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = _count });

            _ok = MakeButton("Use selection", true, (s, e) => Accept());
            var root = new DockPanel();
            var footer = Footer(MakeButton("Cancel", false, (s, e) => Close()), _ok);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(body);
            Content = root;
            Fill();
        }

        public static string PilotLabel(int n) => $"Pilot set · {n} families";

        public static bool PilotLabelFits(List<string> names, List<string> pilot) =>
            pilot.Count > 0 && names.Count == pilot.Count && pilot.All(names.Contains);

        private Button SmallButton(string label, Action click)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) };
            b.SetResourceReference(StyleProperty, "Small");
            b.Click += (s, e) => click();
            return b;
        }

        private IEnumerable<SpecInfo> Shown()
        {
            string role = _role.SelectedIndex > 0 ? (string)_role.SelectedItem : null;
            string prio = _priority.SelectedIndex > 0 ? (string)_priority.SelectedItem : null;
            string q = _search.Text.Trim();
            return _all.Where(s => (role == null || s.Role == role) && (prio == null || s.Priority == prio) &&
                (q.Length == 0 || s.Family.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (s.Category ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void Fill()
        {
            _list.Children.Clear();
            foreach (var s in Shown())
            {
                var name = new TextBlock { Text = s.Family, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
                var meta = new TextBlock { Text = $"{s.Role} · {s.Priority} · {s.Category} · {s.Connectors} connectors · {s.Parameters} parameters" };
                meta.SetResourceReference(StyleProperty, "Meta");
                var cb = new CheckBox { IsChecked = _picked.Contains(s.Family), Content = new StackPanel { Children = { name, meta } }, Margin = new Thickness(2, 3, 2, 3) };
                string fam = s.Family;
                cb.Checked += (o, e) => { _picked.Add(fam); Count(); };
                cb.Unchecked += (o, e) => { _picked.Remove(fam); Count(); };
                _list.Children.Add(cb);
            }
            Count();
        }

        private void Count()
        {
            int n = _picked.Count;
            _count.Text = n > Max ? $"{n} selected: at most {Max} per batch" : $"{n} selected (at most {Max} per batch)";
            _ok.IsEnabled = n > 0 && n <= Max;
        }

        private void Accept()
        {
            // Keep the register's order, so batches read the same way every time.
            Names = _all.Where(s => _picked.Contains(s.Family)).Select(s => s.Family).ToList();
            bool pilot = PilotLabelFits(Names, _pilot);
            var roles = _all.Where(s => _picked.Contains(s.Family)).Select(s => s.Role).Distinct().ToList();
            Label = pilot ? PilotLabel(Names.Count)
                : roles.Count == 1 ? $"Role {roles[0]} · {Names.Count} families"
                : $"{Names.Count} families";
            DialogResult = true;
        }
    }

    /// <summary>New family, project or template from a template file: the fmc_file create_from_template arguments.</summary>
    public sealed class NewFromTemplateWindow : FmcWindow
    {
        public string Kind { get; private set; }
        public string TemplatePath => _template.Text.Trim();
        public string SavePath => _save.Text.Trim();
        public bool CloseAfter => _closeAfter.IsChecked == true;

        private readonly RadioButton _family = new RadioButton { Content = "Family (.rfa from .rft)", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
        private readonly RadioButton _project = new RadioButton { Content = "Project (.rvt from .rte)", Margin = new Thickness(0, 0, 0, 4) };
        private readonly RadioButton _templ = new RadioButton { Content = "Template (.rte from .rte)" };
        private readonly TextBox _template = new TextBox();
        private readonly TextBox _save = new TextBox();
        private readonly CheckBox _closeAfter = new CheckBox { Content = "Close the new file after saving", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        private readonly GuardStatus _guard;

        public NewFromTemplateWindow(GuardStatus guard) : base("New from template", 480)
        {
            _guard = guard;
            foreach (var r in new[] { _family, _project, _templ }) r.SetResourceReference(ForegroundProperty, "Text");
            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
            body.Children.Add(Text("Create", "Label"));
            body.Children.Add(new StackPanel { Margin = new Thickness(0, 4, 0, 10), Children = { _family, _project, _templ } });
            body.Children.Add(Text("Template", "Label"));
            body.Children.Add(PathRow(_template, BrowseTemplate));
            body.Children.Add(Text("Save to", "Label", 10));
            body.Children.Add(PathRow(_save, BrowseSave));
            body.Children.Add(Text("Names follow Manual 4.1 and 4.6: FMC_[Role]_[CategoryToken]_[Description].rfa, " +
                "[Project]-[Originator]-[Volume]-[Level]-[Type]-[Role]-[Number].rvt or FMC_<Role>_Template_v2.2.rte. " +
                "The dry run checks the name and the folder.", "Meta", 8));
            body.Children.Add(_closeAfter);

            var root = new DockPanel();
            var footer = Footer(MakeButton("Cancel", false, (s, e) => Close()), MakeButton("Dry run", true, (s, e) => Accept()));
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(body);
            Content = root;
        }

        private DockPanel PathRow(TextBox box, Action browse)
        {
            var b = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
            b.SetResourceReference(StyleProperty, "Small");
            b.Click += (s, e) => browse();
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            row.Children.Add(box);
            return row;
        }

        private string KindNow => _family.IsChecked == true ? "family" : _project.IsChecked == true ? "project" : "template";

        private void BrowseTemplate()
        {
            bool fam = KindNow == "family";
            string start = fam ? GuardStatus.ExpandEnv(_guard.FamilyTemplateDir)
                : _guard.TemplateRoots.Select(GuardStatus.ExpandEnv).FirstOrDefault(r => !r.Contains("*") && Directory.Exists(r));
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a template",
                Filter = fam ? "Family templates (*.rft)|*.rft" : "Project templates (*.rte)|*.rte",
                InitialDirectory = Directory.Exists(start ?? "") ? start : "",
            };
            if (dlg.ShowDialog(this) == true) _template.Text = dlg.FileName;
        }

        private void BrowseSave()
        {
            string ext = KindNow == "family" ? ".rfa" : KindNow == "project" ? ".rvt" : ".rte";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the new file to",
                Filter = $"Revit file (*{ext})|*{ext}",
                InitialDirectory = _guard.FirstWritableFolder() ?? "",
                OverwritePrompt = false, // the server refuses to overwrite unless asked twice
            };
            if (dlg.ShowDialog(this) == true) _save.Text = dlg.FileName;
        }

        private void Accept()
        {
            if (TemplatePath.Length == 0 || SavePath.Length == 0)
            {
                FmcDialog.Info("New from template", "Choose a template and where to save the new file.");
                return;
            }
            Kind = KindNow;
            DialogResult = true;
        }
    }
}
