using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tuner
{
    /// <summary>
    /// Settings: where each slider (and the calibration search) starts and ends, what the monitor shows,
    /// and how the window looks. Nothing here touches the GPU; ranges only decide what can be chosen later.
    /// </summary>
    class SettingsDialog : Form
    {
        /// <summary>The preferences as edited (valid after OK).</summary>
        public Prefs Result;
        /// <summary>The ranges as edited, or null when this GPU has no table yet.</summary>
        public Ranges NewRanges;
        /// <summary>True when the edited ranges equal the defaults, so nothing needs to be stored.</summary>
        public bool RangesAreDefault;

        readonly Limits hard, defaults;
        NumericUpDown clockFrom, clockTo, powerFrom, voltFrom, tempFrom;
        CheckBox monitor, speed, voltage, power, temp;
        ComboBox history, theme;
        string accent;
        readonly Panel[] swatches = new Panel[Accents.Names.Length];

        /// <param name="hard">Widest ranges allowed for this GPU, or null if it has no table yet.</param>
        public SettingsDialog(Prefs prefs, Limits hard, Limits defaults, Limits current)
        {
            this.hard = hard; this.defaults = defaults;
            accent = prefs.Accent;
            Text = "Settings"; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            Font = Theme.Body(9f); BackColor = Theme.Window; ForeColor = Theme.Ink;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            HandleCreated += delegate { Theme.StyleTitleBar(Handle); };

            int y = 16;
            Heading("Ranges", ref y);
            if (hard == null) Note("This GPU has no table yet. Import its vBIOS first; ranges can be set after that.", ref y);
            else
            {
                Settings f = hard.Factory;
                Note("Where each slider starts and ends. Calibration searches inside these ranges, so its presets stay comparable from run to run.", ref y);
                clockFrom = Row("Top speed", "MHz", hard.ClockMin, f.MaxClockMHz - Limits.ClockStep, current.ClockMin, Limits.ClockStep, out clockTo, f.MaxClockMHz, hard.ClockMax, current.ClockMax, null, ref y);
                NumericUpDown none;
                powerFrom = Row("Power limit", "W", hard.PowerMin, f.PowerLimitW - 1, current.PowerMin, 1, out none, 0, 0, 0, f.PowerLimitW + " W", ref y);
                voltFrom = Row("Voltage ceiling", "mV", hard.VoltMin, f.MaxVoltageMv - Limits.VoltStep, current.VoltMin, Limits.VoltStep, out none, 0, 0, 0, f.MaxVoltageMv + " mV", ref y);
                tempFrom = Row("Hotspot limit", "°C", hard.TempMin, f.TempLimitC - 1, current.TempMin, 1, out none, 0, 0, 0, f.TempLimitC + " °C", ref y);
                Note("Top speed may end up to 20% above factory (" + hard.ClockMax + " MHz). Power, voltage and hotspot always end at factory: they can only be lowered.", ref y);
                PillButton reset = new PillButton(); reset.Text = "Use default ranges"; reset.SetBounds(20, y, 170, 30);
                reset.Click += delegate
                {
                    clockFrom.Value = defaults.ClockMin; clockTo.Value = defaults.ClockMax; powerFrom.Value = defaults.PowerMin;
                    voltFrom.Value = defaults.VoltMin; tempFrom.Value = defaults.TempMin;
                };
                Controls.Add(reset);
                y += 44;
            }

            Heading("Monitor", ref y);
            monitor = Check("Show graphs beside the controls", prefs.Monitor, 20, y, 300); y += 26;
            speed = Check("Speed", prefs.GraphSpeed, 20, y, 90);
            voltage = Check("Voltage", prefs.GraphVoltage, 112, y, 90);
            power = Check("Power", prefs.GraphPower, 204, y, 90);
            temp = Check("Hotspot and GPU temperature", prefs.GraphTemp, 290, y, 200); y += 30;
            Caption("Time shown", 20, y + 3, 110);
            history = Combo(new string[] { "1 minute", "5 minutes", "15 minutes" }, Array.IndexOf(Prefs.HistoryOptions, prefs.HistorySeconds), 134, y, 150); y += 42;

            Heading("Appearance", ref y);
            Caption("Theme", 20, y + 3, 110);
            theme = Combo(new string[] { "Follow Windows", "Light", "Dark" }, prefs.Theme == "light" ? 1 : prefs.Theme == "dark" ? 2 : 0, 134, y, 150); y += 36;
            Caption("Accent colour", 20, y + 6, 110);
            ToolTip tip = new ToolTip();
            for (int i = 0; i < swatches.Length; i++)
            {
                string name = Accents.Names[i];
                Panel sw = new Panel(); sw.SetBounds(134 + i * 38, y, 30, 30); sw.Cursor = Cursors.Hand;
                sw.Paint += delegate (object s, PaintEventArgs e)
                {
                    int rgb = Accents.Rgb(name, Theme.Dark);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255))) e.Graphics.FillEllipse(b, 4, 4, 22, 22);
                    if (name == accent) using (Pen p = new Pen(Theme.Ink, 2f)) e.Graphics.DrawEllipse(p, 1, 1, 27, 27);
                };
                sw.Click += delegate { accent = name; foreach (Panel p in swatches) p.Invalidate(); };
                tip.SetToolTip(sw, name);
                Controls.Add(sw); swatches[i] = sw;
            }
            y += 48;

            PillButton save = new PillButton(); save.Text = "Save"; save.Primary = true; save.SetBounds(316, y, 92, 34);
            PillButton cancel = new PillButton(); cancel.Text = "Cancel"; cancel.SetBounds(416, y, 92, 34);
            save.Click += delegate { Collect(prefs); DialogResult = DialogResult.OK; };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(save); Controls.Add(cancel);
            ClientSize = new Size(528, y + 50);
        }

        void Collect(Prefs old)
        {
            Prefs p = new Prefs();
            p.TestSeconds = old.TestSeconds;
            p.Monitor = monitor.Checked; p.GraphSpeed = speed.Checked; p.GraphVoltage = voltage.Checked; p.GraphPower = power.Checked; p.GraphTemp = temp.Checked;
            p.HistorySeconds = Prefs.HistoryOptions[Math.Max(0, history.SelectedIndex)];
            p.Theme = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system";
            p.Accent = accent;
            Result = p;
            if (hard == null) return;
            NewRanges = new Ranges((int)clockFrom.Value, (int)clockTo.Value, (int)powerFrom.Value, (int)voltFrom.Value, (int)tempFrom.Value);
            Ranges d = defaults.ToRanges();
            RangesAreDefault = NewRanges.ClockMin == d.ClockMin && NewRanges.ClockMax == d.ClockMax && NewRanges.PowerMin == d.PowerMin
                            && NewRanges.VoltMin == d.VoltMin && NewRanges.TempMin == d.TempMin;
        }

        // ---- small builders ----

        void Heading(string text, ref int y)
        {
            Label l = new Label(); l.Text = text; l.Font = Theme.Display(11.5f); l.ForeColor = Theme.Ink; l.SetBounds(18, y, 480, 24);
            Controls.Add(l); y += 28;
        }

        void Note(string text, ref int y)
        {
            Label l = new Label(); l.Text = text; l.ForeColor = Theme.Muted; l.SetBounds(20, y, 490, 34);
            Controls.Add(l); y += 38;
        }

        void Caption(string text, int x, int y, int w)
        {
            Label l = new Label(); l.Text = text; l.ForeColor = Theme.Ink; l.SetBounds(x, y, w, 20);
            Controls.Add(l);
        }

        NumericUpDown Number(int min, int max, int value, int step, int x, int y)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = Math.Max(min, max); n.Increment = step; n.Value = Math.Max(min, Math.Min(Math.Max(min, max), value));
            n.BackColor = Theme.Panel; n.ForeColor = Theme.Ink; n.BorderStyle = BorderStyle.FixedSingle; n.TextAlign = HorizontalAlignment.Right;
            n.SetBounds(x, y, 78, 24);
            Controls.Add(n);
            return n;
        }

        /// <summary>One range row: name, "from" box, then either a "to" box or the fixed factory end.</summary>
        NumericUpDown Row(string name, string unit, int fromMin, int fromMax, int fromValue, int step,
                          out NumericUpDown to, int toMin, int toMax, int toValue, string fixedEnd, ref int y)
        {
            Caption(name, 20, y + 3, 110);
            Caption("from", 134, y + 3, 34);
            NumericUpDown from = Number(fromMin, fromMax, fromValue, step, 170, y);
            Caption("to", 262, y + 3, 20);
            to = null;
            if (fixedEnd == null)
            {
                to = Number(toMin, toMax, toValue, step, 286, y);
                Caption(unit + "    (allowed " + fromMin + "-" + toMax + ")", 372, y + 3, 150);
            }
            else
            {
                Caption(fixedEnd, 288, y + 3, 80);                                                  // the end is the factory value and cannot be moved
                Caption("factory   (lowest " + fromMin + ")", 372, y + 3, 150);
            }
            y += 30;
            return from;
        }

        CheckBox Check(string text, bool on, int x, int y, int w)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.Checked = on; c.ForeColor = Theme.Ink; c.SetBounds(x, y, w, 22);
            Controls.Add(c);
            return c;
        }

        ComboBox Combo(string[] items, int selected, int x, int y, int w)
        {
            ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Panel; c.ForeColor = Theme.Ink;
            c.Items.AddRange(items); c.SelectedIndex = Math.Max(0, selected); c.SetBounds(x, y, w, 24);
            Controls.Add(c);
            return c;
        }
    }
}
