using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tuner
{
    /// <summary>
    /// Two palettes with one identity: a copper stop (heat pipes are what limit a laptop GPU) and teal for live
    /// readings. Light is brushed aluminium, dark is graphite; the window follows the Windows app theme.
    /// </summary>
    static class Theme
    {
        public static bool Dark;
        public static Color Window, Panel, Ink, Muted, Groove, Accent, AccentSoft, Teal, Amber, OnAccent;

        static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static void Use(bool dark, string accentName)
        {
            Dark = dark;
            if (dark) { Window = Hex(0x17191D); Panel = Hex(0x21252B); Ink = Hex(0xECEEF1); Muted = Hex(0x98A1AC); Groove = Hex(0x3A414B); Teal = Hex(0x45C4CE); Amber = Hex(0xE7AC3F); }
            else { Window = Hex(0xE9ECF0); Panel = Hex(0xFAFBFC); Ink = Hex(0x1B2129); Muted = Hex(0x66707C); Groove = Hex(0xD3D8DF); Teal = Hex(0x0B7A84); Amber = Hex(0xC98510); }
            Accent = Hex(Accents.Rgb(accentName, dark));
            AccentSoft = Blend(Accent, Panel, dark ? 0.62 : 0.70);   // the filled part of a rail
            double luminance = (0.299 * Accent.R + 0.587 * Accent.G + 0.114 * Accent.B) / 255;
            OnAccent = luminance > 0.58 ? Hex(0x16181B) : Color.White;   // text on an accent-coloured button
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch (Exception) { return false; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        /// <summary>Asks Windows for a dark title bar to match the dark palette (ignored on old Windows).</summary>
        public static void StyleTitleBar(IntPtr hwnd)
        {
            try { int on = Dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref on, 4); }
            catch (Exception) { }
        }

        /// <summary>Bahnschrift (Windows' DIN face, the lettering of instrument panels) for names and numbers.</summary>
        public static Font Display(float pt)
        {
            Font f = new Font("Bahnschrift SemiBold", pt);
            if (f.Name == "Bahnschrift SemiBold") return f;
            f.Dispose();
            return new Font("Segoe UI Semibold", pt);
        }
        public static Font Body(float pt) { return new Font("Segoe UI", pt); }

        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public const ControlStyles Painted = ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
    }

    /// <summary>A rounded panel that groups related controls.</summary>
    class Card : Panel
    {
        public Card() { SetStyle(Theme.Painted, true); BackColor = Theme.Panel; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Window : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10f * Width / 640f))
            using (SolidBrush b = new SolidBrush(Theme.Panel))
            using (Pen pen = new Pen(Theme.Groove))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
            g.SmoothingMode = SmoothingMode.None;
            PaintContent(g);
            base.OnPaint(e);   // lets owners add their own lines through the Paint event
        }
        protected virtual void PaintContent(Graphics g) { }
    }

    /// <summary>A rounded button. Primary = the one filled action; Selected = an outlined "this one is active" state.</summary>
    class PillButton : Control
    {
        public bool Primary;
        bool selected, hover, down;

        public PillButton()
        {
            SetStyle(Theme.Painted | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true; Cursor = Cursors.Hand; Font = Theme.Display(10f);
            AccessibleRole = AccessibleRole.PushButton;
        }

        public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Window : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, border, text;
            if (!Enabled) { fill = Primary ? Theme.Groove : Theme.Panel; border = Theme.Groove; text = Theme.Muted; }
            else if (Primary) { fill = hover ? Theme.Blend(Theme.Accent, Theme.Ink, 0.12) : Theme.Accent; border = fill; text = Theme.OnAccent; }
            else
            {
                fill = hover ? Theme.Blend(Theme.Panel, Theme.Groove, 0.45) : Theme.Panel;
                border = selected ? Theme.Accent : Theme.Groove;
                text = selected ? Theme.Accent : Theme.Ink;
            }
            if (down && Enabled) fill = Theme.Blend(fill, Color.Black, 0.10);

            float inset = selected && !Primary ? 1f : 0.5f;
            using (GraphicsPath p = Theme.Round(new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), Height * 0.26f))
            using (SolidBrush b = new SolidBrush(fill))
            using (Pen pen = new Pen(border, selected && !Primary ? 2f : 1f))
            { g.FillPath(b, p); g.DrawPath(pen, p); }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Focused && Enabled && ShowFocusCues)
                using (GraphicsPath p = Theme.Round(new RectangleF(3.5f, 3.5f, Width - 7, Height - 7), Height * 0.2f))
                using (Pen pen = new Pen(Color.FromArgb(150, Primary ? Theme.OnAccent : Theme.Ink)))
                { pen.DashStyle = DashStyle.Dot; g.DrawPath(pen, p); }
        }
    }

    /// <summary>A big number with a small unit, right-aligned.</summary>
    class ValueLabel : Control
    {
        public string Number = "", Unit = "";
        readonly Font unitFont = Theme.Display(10f);

        public ValueLabel() { SetStyle(Theme.Painted, true); Font = Theme.Display(17f); }
        public void Set(string number, string unit) { if (number == Number && unit == Unit) return; Number = number; Unit = unit; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            Size us = TextRenderer.MeasureText(g, Unit, unitFont, Size.Empty, TextFormatFlags.NoPadding);
            Size ns = TextRenderer.MeasureText(g, Number, Font, Size.Empty, TextFormatFlags.NoPadding);
            int baseY = (Height - ns.Height) / 2;
            TextRenderer.DrawText(g, Unit, unitFont, new Point(Width - us.Width, baseY + ns.Height - us.Height - (int)(ns.Height * 0.08f)), Theme.Muted, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Number, Font, new Point(Width - us.Width - ns.Width - (int)(4 * Height / 30f), baseY), Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// One limit on one scale: drag the copper stop to set the ceiling; the teal marker shows the live reading
    /// on the same scale, so you can see how much headroom the GPU has under the ceiling you set.
    /// </summary>
    class Rail : Control
    {
        public int Min, Max = 100, Step = 1, Factory, CautionAbove = int.MaxValue;
        int value, live = -1, applied = -1;
        string liveCaption = "";
        public event EventHandler ValueChanged;

        /// <summary>
        /// The value the GPU is really running on. While the stop sits somewhere else, a ghost stays at this value
        /// so it is obvious that the change has not been applied yet. A negative value hides it.
        /// </summary>
        public void SetApplied(int v) { if (v != applied) { applied = v; Invalidate(); } }

        public Rail()
        {
            SetStyle(Theme.Painted | ControlStyles.Selectable, true);
            TabStop = true; Font = Theme.Body(7.5f); Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(Min, Math.Min(Max, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void Configure(int min, int max, int step, int factory, int cautionAbove)
        {
            Min = min; Max = Math.Max(min + 1, max); Step = Math.Max(1, step); Factory = factory; CautionAbove = cautionAbove;
            value = Math.Max(Min, Math.Min(Max, value));
            Invalidate();
        }

        /// <summary>Position and caption of the live marker; a negative value hides it.</summary>
        public void SetLive(int v, string caption)
        {
            if (v == live && caption == liveCaption) return;
            live = v; liveCaption = caption;
            Invalidate();
        }

        float S { get { return Height / 52f; } }
        int Pad { get { return (int)(16 * S); } }
        int XOf(int v)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            return Pad + (int)Math.Round((Width - 2 * Pad) * (double)(v - Min) / (Max - Min));
        }
        int ValueAt(int x)
        {
            double t = (x - Pad) / (double)(Width - 2 * Pad);
            int raw = Min + (int)Math.Round(t * (Max - Min));
            return Min + (int)Math.Round((raw - Min) / (double)Step) * Step;
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Value = ValueAt(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) Value = ValueAt(e.X); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Value = value - Step;
            else if (e.KeyCode == Keys.Right) Value = value + Step;
            else if (e.KeyCode == Keys.Home) Value = Factory;
            else if (e.KeyCode == Keys.Escape && applied >= 0) Value = applied;
        }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Value = value + Math.Sign(e.Delta) * Step; }
        /// <summary>Double-click puts the stop back on the applied value.</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (applied >= 0) Value = applied; }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            float s = S;
            int gy = (int)(19 * s), gh = Math.Max(4, (int)(8 * s));
            int x0 = XOf(Min), x1 = XOf(Max), xv = XOf(value), xf = XOf(Factory);
            bool hasCaution = CautionAbove < Max;
            int xc = hasCaution ? XOf(CautionAbove) : x1;
            TextFormatFlags plain = TextFormatFlags.NoPadding;

            // groove with rounded ends; the allowed range up to the stop is filled, the overclock zone is hatched
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath groove = Theme.Round(new RectangleF(x0 - gh / 2f, gy, x1 - x0 + gh, gh), gh / 2f))
            {
                using (SolidBrush b = new SolidBrush(Theme.Groove)) g.FillPath(b, groove);
                g.SetClip(groove);
                if (hasCaution)
                    using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Theme.Amber, Theme.Groove)) g.FillRectangle(hb, xc, gy, x1 - xc + gh, gh);
                using (SolidBrush b = new SolidBrush(Enabled ? Theme.AccentSoft : Theme.Groove)) g.FillRectangle(b, x0 - gh, gy, Math.Min(xv, xc) - x0 + gh, gh);
                if (xv > xc)
                    using (SolidBrush b = new SolidBrush(Theme.Amber)) g.FillRectangle(b, xc, gy, xv - xc, gh);
                g.ResetClip();
            }

            // scale: the two ends and the factory mark (an end label gives way when "factory" sits on it)
            using (Pen p = new Pen(Theme.Muted, Math.Max(1f, s))) g.DrawLine(p, xf, gy - (int)(6 * s), xf, gy - 1);
            bool ghost = applied >= 0 && applied != value;
            int xa = XOf(applied);
            string factoryText = ghost && applied == Factory ? "factory, applied" : "factory";
            Size fs = TextRenderer.MeasureText(g, factoryText, Font, Size.Empty, plain);
            Rectangle fr = new Rectangle(Math.Max(0, Math.Min(Width - fs.Width, xf - fs.Width / 2)), 0, fs.Width, fs.Height);
            Rectangle clear = Rectangle.Inflate(fr, (int)(6 * s), 0);
            Rectangle ar = Rectangle.Empty;   // the "applied" label, unless it would sit on top of "factory"
            if (ghost && applied != Factory)
            {
                Size az = TextRenderer.MeasureText(g, "applied", Font, Size.Empty, plain);
                ar = new Rectangle(Math.Max(0, Math.Min(Width - az.Width, xa - az.Width / 2)), 0, az.Width, az.Height);
                if (ar.IntersectsWith(clear)) ar = Rectangle.Empty;
            }
            Rectangle clearApplied = ar.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(ar, (int)(6 * s), 0);
            string lo = Min.ToString(), hi = Max.ToString();
            Size ls = TextRenderer.MeasureText(g, lo, Font, Size.Empty, plain), hs = TextRenderer.MeasureText(g, hi, Font, Size.Empty, plain);
            Rectangle lr = new Rectangle(x0 - gh / 2, 0, ls.Width, ls.Height), hr = new Rectangle(x1 + gh / 2 - hs.Width, 0, hs.Width, hs.Height);
            Color faint = Theme.Blend(Theme.Muted, Theme.Panel, 0.35);
            if (!lr.IntersectsWith(clear) && !lr.IntersectsWith(clearApplied)) TextRenderer.DrawText(g, lo, Font, lr.Location, faint, plain);
            if (!hr.IntersectsWith(clear) && !hr.IntersectsWith(clearApplied)) TextRenderer.DrawText(g, hi, Font, hr.Location, faint, plain);
            TextRenderer.DrawText(g, factoryText, Font, fr.Location, Theme.Muted, plain);
            if (!ar.IsEmpty) TextRenderer.DrawText(g, "applied", Font, ar.Location, Theme.Ink, plain);

            // the ghost: where the GPU really is until the change is applied
            if (ghost)
            {
                RectangleF gr = new RectangleF(xa - 6 * s, gy - 8 * s, 12 * s, gh + 16 * s);
                using (GraphicsPath gp = Theme.Round(gr, 3.5f * s))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(75, Theme.Ink)))
                using (Pen p = new Pen(Color.FromArgb(220, Theme.Ink), 1f))
                { p.DashStyle = DashStyle.Dash; g.FillPath(b, gp); g.DrawPath(p, gp); }
            }

            // live marker
            if (live >= 0)
            {
                int xl = XOf(live), ty = gy + gh + (int)(3 * s), th = (int)(7 * s), tw = (int)(5 * s);
                using (SolidBrush b = new SolidBrush(Theme.Teal))
                    g.FillPolygon(b, new Point[] { new Point(xl, ty), new Point(xl - tw, ty + th), new Point(xl + tw, ty + th) });
                Size cs = TextRenderer.MeasureText(g, liveCaption, Font, Size.Empty, plain);
                int lx = xl + tw + (int)(4 * s);
                if (lx + cs.Width > Width) lx = xl - tw - (int)(4 * s) - cs.Width;
                // keep the caption clear of the stop, which is drawn over this row
                int stopLeft = xv - (int)(9 * s), stopRight = xv + (int)(9 * s);
                if (lx < stopRight && lx + cs.Width > stopLeft)
                {
                    lx = stopRight + (int)(2 * s);
                    if (lx + cs.Width > Width) lx = stopLeft - (int)(2 * s) - cs.Width;
                }
                TextRenderer.DrawText(g, liveCaption, Font, new Point(lx, ty - 1), Theme.Teal, plain);
            }

            // the stop: a rounded block with a soft shadow and a grip line
            RectangleF stop = new RectangleF(xv - 6 * s, gy - 8 * s, 12 * s, gh + 16 * s);
            Color stopColor = !Enabled ? Theme.Muted : xv > xc ? Theme.Amber : Theme.Accent;
            using (GraphicsPath shadow = Theme.Round(new RectangleF(stop.X, stop.Y + 1.5f * s, stop.Width, stop.Height), 3.5f * s))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.Dark ? 110 : 45, 0, 0, 0))) g.FillPath(b, shadow);
            using (GraphicsPath body = Theme.Round(stop, 3.5f * s))
            using (SolidBrush b = new SolidBrush(stopColor)) g.FillPath(b, body);
            using (Pen p = new Pen(Color.FromArgb(150, Theme.OnAccent), Math.Max(1f, s))) g.DrawLine(p, xv, stop.Top + 6 * s, xv, stop.Bottom - 6 * s);
            if (Focused && ShowFocusCues)
                using (GraphicsPath ring = Theme.Round(RectangleF.Inflate(stop, 2.5f, 2.5f), 5f * s))
                using (Pen p = new Pen(Theme.Ink, 1f)) g.DrawPath(p, ring);
        }
    }

}
