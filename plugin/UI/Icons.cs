using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// 24 px line icons from the handoff (Ribbon.dc.html, McpStates.dc.html), round caps. The pane draws them as Paths;
    /// the ribbon gets bitmaps rendered from the same geometry, so a state change can swap a filled and an outline image.
    /// </summary>
    public static class Icons
    {
        public const string Browser = "M3 4h18v16H3z M9 4v16";
        public const string Power = "M12 3v8 M7.5 6.5a7 7 0 1 0 9 0";
        public const string Lock = "M6 11h12v9H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3";
        public const string LockOpen = "M6 11h12v9H6z M8.5 11V8a3.5 3.5 0 0 1 6.8 -1.2";
        public const string Doc = "M8 3h11v15H8z M5 6v15h11";
        public const string Probe = "M3 12h4l2 -5l3 10l2 -5h7";

        public static Geometry Parse(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        /// <summary>Large ribbon button: 32 DIP circle; filled accent with a white glyph, or white with an accent glyph.</summary>
        public static BitmapSource RibbonLarge(string data, bool filled)
        {
            Color accent = Theme.ColorOf("Accent");
            return Render(32, dc =>
            {
                var accentBrush = new SolidColorBrush(accent);
                dc.DrawEllipse(filled ? accentBrush : Brushes.White, new Pen(accentBrush, 2), new Point(16, 16), 14.5, 14.5);
                dc.PushTransform(new MatrixTransform(0.75, 0, 0, 0.75, 7, 7));
                dc.DrawGeometry(null, RoundPen(filled ? Brushes.White : accentBrush, 2.2), Parse(data));
                dc.Pop();
            });
        }

        /// <summary>Small stacked ribbon button: 16 DIP accent glyph.</summary>
        public static BitmapSource RibbonSmall(string data)
        {
            var accentBrush = new SolidColorBrush(Theme.ColorOf("Accent"));
            return Render(16, dc =>
            {
                dc.PushTransform(new ScaleTransform(16.0 / 24, 16.0 / 24));
                dc.DrawGeometry(null, RoundPen(accentBrush, 2.2), Parse(data));
                dc.Pop();
            });
        }

        private static Pen RoundPen(Brush b, double width) =>
            new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        /// <summary>Renders at 2x (192 dpi) so the icon stays sharp on high-DPI screens; its size in DIP is unchanged.</summary>
        private static BitmapSource Render(int size, Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                draw(dc); // drawn in DIP; the 192 dpi target maps each DIP to 2 pixels
            var bmp = new RenderTargetBitmap(size * 2, size * 2, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
