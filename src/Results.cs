using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tuner
{
    /// <summary>
    /// The measuring panel. It shows one of three things: a hint, a running test or calibration (title, bar, clock),
    /// or the last result as four tiles with their change since the comparison test.
    /// </summary>
    class ResultsCard : Card
    {
        LoadResult now, before;
        string versus = "", message = "", footer = "", title = "", right = "";
        double progress = -1;
        readonly Font label = Theme.Body(8f), number = Theme.Display(13f), heading = Theme.Display(11f);

        public void ShowMessage(string text) { now = null; progress = -1; message = text; Invalidate(); }

        /// <summary>With a "before", each tile says how it moved "vs {versus}"; without one, {versus} is a plain caption.</summary>
        public void ShowResult(LoadResult result, LoadResult compareWith, string versusText, string footerText)
        {
            now = result; before = compareWith; versus = versusText; footer = footerText; progress = -1;
            Invalidate();
        }

        /// <summary>fraction is 0..1.</summary>
        public void ShowProgress(string titleText, double fraction, string rightText, string footerText)
        {
            title = titleText; progress = Math.Max(0, Math.Min(1, fraction)); right = rightText; footer = footerText;
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            float s = Height / 92f;
            int pad = (int)(18 * s);
            TextFormatFlags plain = TextFormatFlags.NoPadding;

            if (progress >= 0)
            {
                TextRenderer.DrawText(g, title, heading, new Point(pad, (int)(14 * s)), Theme.Ink, plain);
                Size rs = TextRenderer.MeasureText(g, right, heading, Size.Empty, plain);
                TextRenderer.DrawText(g, right, heading, new Point(Width - pad - rs.Width, (int)(14 * s)), Theme.Muted, plain);
                RectangleF bar = new RectangleF(pad, 44 * s, Width - 2 * pad, 8 * s);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath groove = Theme.Round(bar, bar.Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(Theme.Groove)) g.FillPath(b, groove);
                    g.SetClip(groove);
                    using (SolidBrush b = new SolidBrush(Theme.Teal)) g.FillRectangle(b, bar.X, bar.Y, (float)(bar.Width * progress), bar.Height);
                    g.ResetClip();
                }
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, footer, label, new Rectangle(pad, (int)(62 * s), Width - 2 * pad, (int)(18 * s)), Theme.Muted, plain | TextFormatFlags.EndEllipsis);
                return;
            }

            if (now == null)
            {
                TextRenderer.DrawText(g, message, Font, new Rectangle(pad, 0, Width - 2 * pad, Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.WordBreak);
                return;
            }

            // Hotspot (the hottest point of the chip) is what the limit acts on; GPU temperature is the cooler overall reading.
            string[] names = { "Speed under load", "Voltage", "Power", "Hotspot peak", "GPU temp. peak" };
            string[] values = { now.AvgClock + " MHz", now.PeakVoltage + " mV", now.AvgPower + " W", now.PeakHotspot + " °C", now.PeakEdge + " °C" };
            string[] deltas = before == null ? null : new string[] {
                LoadResult.Delta(now.AvgClock, before.AvgClock, "MHz"), LoadResult.Delta(now.PeakVoltage, before.PeakVoltage, "mV"),
                LoadResult.Delta(now.AvgPower, before.AvgPower, "W"), LoadResult.Delta(now.PeakHotspot, before.PeakHotspot, "°C"),
                LoadResult.Delta(now.PeakEdge, before.PeakEdge, "°C") };
            int w = (Width - 2 * pad) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                int x = pad + i * w;
                if (i > 0) using (Pen p = new Pen(Theme.Groove)) g.DrawLine(p, x - (int)(10 * s), (int)(12 * s), x - (int)(10 * s), (int)(64 * s));
                TextRenderer.DrawText(g, names[i], label, new Point(x, (int)(9 * s)), Theme.Muted, plain);
                TextRenderer.DrawText(g, values[i], number, new Point(x, (int)(24 * s)), Theme.Ink, plain);
                string third = deltas == null ? versus : deltas[i];   // what it is compared with is named in the line underneath
                TextRenderer.DrawText(g, third, label, new Point(x, (int)(49 * s)), deltas == null || deltas[i] == "no change" ? Theme.Muted : Theme.Teal, plain);
            }
            using (Pen p = new Pen(Theme.Groove)) g.DrawLine(p, pad, (int)(68 * s), Width - pad, (int)(68 * s));
            TextRenderer.DrawText(g, footer, label, new Rectangle(pad, (int)(72 * s), Width - 2 * pad, (int)(16 * s)), Theme.Muted, plain | TextFormatFlags.EndEllipsis);
        }
    }

    enum Tone { Info, Good, Warn, Bad }

    /// <summary>One message with its tone.</summary>
    class Note
    {
        public readonly Tone Tone; public readonly string Text;
        public Note(Tone tone, string text) { Tone = tone; Text = text; }
        public static Note Info(string t) { return new Note(Tone.Info, t); }
        public static Note Good(string t) { return new Note(Tone.Good, t); }
        public static Note Warn(string t) { return new Note(Tone.Warn, t); }
        public static Note Bad(string t) { return new Note(Tone.Bad, t); }
    }

    /// <summary>The status line at the bottom: a coloured dot that says what kind of message it is, then the message.</summary>
    class StatusLine : Control
    {
        Tone tone;

        public StatusLine() { SetStyle(Theme.Painted, true); Font = Theme.Body(9f); }

        public void Say(Note note) { tone = note.Tone; Text = note.Text; Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Window : Parent.BackColor);
            float s = Height / 40f;
            Color dot = tone == Tone.Good ? Theme.Teal : tone == Tone.Warn ? Theme.Amber : tone == Tone.Bad ? Theme.Accent : Theme.Muted;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, 2 * s, 5 * s, 8 * s, 8 * s);
            g.SmoothingMode = SmoothingMode.None;
            Rectangle r = new Rectangle((int)(18 * s), 0, Width - (int)(18 * s), Height);
            TextRenderer.DrawText(g, Text, Font, r, Theme.Ink, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}
