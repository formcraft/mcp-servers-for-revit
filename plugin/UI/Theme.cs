using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Formcraft Browser design tokens (FMC_MCP_Canvas_Handoff design/tokens.json) as WPF brushes. Light values are the
    /// handoff's; dark values are the same roles for Revit's dark theme. Every Formcraft window calls Apply() on its own
    /// Resources and uses DynamicResource, so a theme change repaints without rebuilding anything.
    /// </summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }
        public static event Action Changed;

        private static readonly Dictionary<string, (string Light, string Dark)> Tokens = new Dictionary<string, (string, string)>
        {
            ["Canvas"] = ("#F6F7F9", "#1E2228"),
            ["Surface"] = ("#FFFFFF", "#262B33"),
            ["SurfaceMuted"] = ("#F8F9FA", "#2B3139"),
            ["TitleBar"] = ("#F1F3F5", "#2B3139"),
            ["SegmentTrack"] = ("#EEF0F3", "#333A44"),
            ["SegmentOn"] = ("#FFFFFF", "#46505D"),
            ["SelectedRow"] = ("#F1F5FE", "#22324A"),
            ["Text"] = ("#1A1D21", "#E8EBEF"),
            ["TextBody"] = ("#3B424D", "#CDD2D9"),
            ["TextSecondary"] = ("#4A515C", "#B7BDC6"),
            ["TextMeta"] = ("#5B6270", "#A2A9B4"),
            ["TextDisabled"] = ("#8A919C", "#7F8792"),   // HANDOFF_NEXT: disabled button text
            ["IconMuted"] = ("#8A919C", "#7F8792"),
            ["Border"] = ("#C9CED6", "#4A525E"),
            ["BorderPane"] = ("#D5D9DF", "#404752"),
            ["Divider"] = ("#E3E6EA", "#3A414B"),
            ["DividerLight"] = ("#EEF0F3", "#323942"),
            ["Accent"] = ("#1F6FEB", "#4C8DF6"),
            ["AccentHover"] = ("#1556BD", "#6AA0F8"),
            ["OnAccent"] = ("#FFFFFF", "#FFFFFF"),
            ["ButtonBg"] = ("#FFFFFF", "#2E353F"),
            ["ButtonHover"] = ("#F1F3F5", "#38404B"),
            ["DisabledBg"] = ("#F1F3F5", "#2A3038"),
            ["DisabledBorder"] = ("#DDE1E6", "#3A414B"),
            ["OkDot"] = ("#1E8E3E", "#3FB463"),
            ["OkBg"] = ("#E6F4EA", "#1E3A28"),
            ["OkText"] = ("#1E6B34", "#8FD6A4"),
            ["InfoBg"] = ("#E8F0FE", "#1F2F4D"),
            ["InfoText"] = ("#1556BD", "#9CC0FA"),
            ["WarnBg"] = ("#FFF4E0", "#3D2F17"),
            ["WarnText"] = ("#6B4100", "#F2C27A"),
            ["WarnLine"] = ("#C77400", "#E0A040"),
            ["ErrBg"] = ("#FDECEA", "#46201E"),
            ["ErrText"] = ("#A50E0E", "#F4A39B"),
            ["OfflineDot"] = ("#8A919C", "#7F8792"),
        };

        private static FontFamily _sans, _mono;

        /// <summary>IBM Plex Sans / Mono embedded in the add-in, falling back to Segoe UI / Consolas.</summary>
        public static FontFamily Sans => _sans ?? (_sans = Embedded("IBM Plex Sans", "Segoe UI"));
        public static FontFamily Mono => _mono ?? (_mono = Embedded("IBM Plex Mono", "Consolas"));

        private static FontFamily Embedded(string name, string fallback)
        {
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // registers pack:// inside Revit
                return new FontFamily(new Uri("pack://application:,,,/RevitMCPPlugin;component/UI/Fonts/"), $"./#{name}, {fallback}");
            }
            catch (Exception)
            {
                return new FontFamily(fallback);
            }
        }

        public static void Apply(ResourceDictionary rd)
        {
            foreach (var kv in Tokens)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? kv.Value.Dark : kv.Value.Light));
                brush.Freeze();
                rd[kv.Key] = brush;
            }
            rd["Sans"] = Sans;
            rd["Mono"] = Mono;
        }

        public static Color ColorOf(string key) =>
            (Color)ColorConverter.ConvertFromString(IsDark ? Tokens[key].Dark : Tokens[key].Light);

        public static void SetDark(bool dark)
        {
            if (dark == IsDark) return;
            IsDark = dark;
            Changed?.Invoke();
        }

        /// <summary>Applies the tokens now and again on every theme change, for as long as the element lives.</summary>
        public static void Attach(FrameworkElement element)
        {
            Apply(element.Resources);
            Action reapply = () => Apply(element.Resources);
            Changed += reapply;
            if (element is Window w) w.Closed += (s, e) => Changed -= reapply;
        }
    }
}
