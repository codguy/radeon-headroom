using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tuner
{
    /// <summary>
    /// One value over time: the line, where it is now, its lowest/average/highest in the window shown, and the
    /// ceiling that is applied to it as a dashed line. A flat line means stable; a line pressed against its
    /// ceiling shows what is holding the GPU back.
    /// </summary>
    class Graph : Control
    {
        public string Title = "", Unit = "", SecondName = "";
        int[] first = new int[0], second;
        int ceiling = -1, windowSeconds = 300;
        readonly Font heading = Theme.Display(10.5f), number = Theme.Display(13f), small = Theme.Body(8f), tiny = Theme.Body(7.5f);

        public Graph() { SetStyle(Theme.Painted, true); }

        /// <summary>One value per second, oldest first. second may be null; a negative ceiling hides the dashed line.</summary>
        public void SetData(int[] primary, int[] secondary, int ceilingValue, int window)
        {
            first = primary ?? new int[0]; second = secondary; ceiling = ceilingValue; windowSeconds = Math.Max(2, window);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Window : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10f))
            using (SolidBrush b = new SolidBrush(Theme.Panel))
            using (Pen pen = new Pen(Theme.Groove))
            { g.FillPath(b, p); g.DrawPath(pen, p); }

            TextFormatFlags plain = TextFormatFlags.NoPadding;
            int pad = 14;
            SeriesStats s = SeriesStats.Of(first), s2 = SeriesStats.Of(second);

            // header: name on the left, the value right now on the right, the spread underneath
            TextRenderer.DrawText(g, Title, heading, new Point(pad, 9), Theme.Ink, plain);
            string now = s.Any ? s.Last + " " + Unit : "-";
            Size ns = TextRenderer.MeasureText(g, now, number, Size.Empty, plain);
            TextRenderer.DrawText(g, now, number, new Point(Width - pad - ns.Width, 7), Theme.Ink, plain);
            string spread = !s.Any ? "waiting for the first reading" : "min " + s.Min + "  ·  avg " + s.Avg + "  ·  max " + s.Max;
            if (s.Any && s2.Any) spread += "      " + SecondName + " " + s2.Last + ", max " + s2.Max;
            TextRenderer.DrawText(g, spread, small, new Point(pad, 31), Theme.Muted, plain);

            Rectangle plot = new Rectangle(pad, 52, Width - 2 * pad, Height - 52 - 22);
            if (plot.Height < 20 || plot.Width < 40) return;
            int lo, hi;
            SeriesStats.Scale(first, second, ceiling, out lo, out hi);

            // three faint lines with the scale written on the top and bottom ones
            Color faint = Theme.Blend(Theme.Groove, Theme.Panel, 0.45);
            using (Pen grid = new Pen(faint))
                for (int i = 0; i <= 2; i++) { int y = plot.Top + plot.Height * i / 2; g.DrawLine(grid, plot.Left, y, plot.Right, y); }
            Color scaleText = Theme.Blend(Theme.Muted, Theme.Panel, 0.3);
            bool limitAtTop = ceiling >= 0 && YOf(ceiling, lo, hi, plot) < plot.Top + 16;   // the limit's own label names the top then
            if (!limitAtTop) TextRenderer.DrawText(g, hi.ToString(), tiny, new Point(plot.Left + 2, plot.Top + 1), scaleText, plain);
            TextRenderer.DrawText(g, lo.ToString(), tiny, new Point(plot.Left + 2, plot.Bottom - 13), scaleText, plain);

            if (ceiling >= 0)
            {
                float y = YOf(ceiling, lo, hi, plot);
                using (Pen dash = new Pen(Theme.Accent, 1.2f)) { dash.DashStyle = DashStyle.Dash; g.DrawLine(dash, plot.Left, y, plot.Right, y); }
                string text = "limit " + ceiling;
                Size ts = TextRenderer.MeasureText(g, text, tiny, Size.Empty, plain);
                TextRenderer.DrawText(g, text, tiny, new Point(plot.Right - ts.Width - 2, (int)(y < plot.Top + 16 ? y + 2 : y - 14)), Theme.Accent, plain);
            }

            DrawLine(g, second, lo, hi, plot, Theme.Muted, 1.2f);
            DrawLine(g, first, lo, hi, plot, Theme.Teal, 1.8f);

            g.SmoothingMode = SmoothingMode.None;
            string span = windowSeconds >= 120 ? windowSeconds / 60 + " min ago" : windowSeconds + " s ago";
            TextRenderer.DrawText(g, span, tiny, new Point(plot.Left, plot.Bottom + 4), scaleText, plain);
            Size nowSize = TextRenderer.MeasureText(g, "now", tiny, Size.Empty, plain);
            TextRenderer.DrawText(g, "now", tiny, new Point(plot.Right - nowSize.Width, plot.Bottom + 4), scaleText, plain);
        }

        static float YOf(int value, int lo, int hi, Rectangle plot) { return plot.Bottom - (float)(value - lo) / Math.Max(1, hi - lo) * plot.Height; }

        /// <summary>The newest reading sits on the right edge; one second is one step to the left.</summary>
        void DrawLine(Graphics g, int[] values, int lo, int hi, Rectangle plot, Color color, float width)
        {
            if (values == null || values.Length < 2) return;
            int n = Math.Min(values.Length, windowSeconds);
            PointF[] points = new PointF[n];
            float step = plot.Width / (float)(windowSeconds - 1);
            for (int i = 0; i < n; i++)
                points[i] = new PointF(plot.Right - (n - 1 - i) * step, YOf(values[values.Length - n + i], lo, hi, plot));
            using (Pen pen = new Pen(color, width)) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, points); }
        }
    }
}
