using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tuner
{
    class MainForm : Form
    {
        enum Mode { Idle, Applying, Testing, Calibrating }

        static readonly string[] Titles = { "Top speed", "Power limit", "Voltage ceiling", "Hotspot limit" };
        const int TuneWidth = 680, MonitorWidth = 420;
        static readonly string[] Units = { "MHz", "W", "mV", "°C" };
        const int RowHeight = 100;
        const string RailTip = "Drag the stop, or use the arrow keys or mouse wheel.\r\nDouble-click (or Esc) puts it back on the applied value. Home jumps to factory.";

        readonly Gpu gpu;
        readonly string appDir = Path.GetDirectoryName(Application.ExecutablePath);
        readonly Rail[] rails = new Rail[4];
        readonly ValueLabel[] values = new ValueLabel[4];
        readonly Label[] hints = new Label[4];
        readonly PillButton[] presetButtons = new PillButton[4];
        readonly System.Windows.Forms.Timer sensorTimer = new System.Windows.Forms.Timer(), progressTimer = new System.Windows.Forms.Timer();
        readonly ToolTip tips = new ToolTip();
        readonly Stopwatch workClock = new Stopwatch(), stepClock = new Stopwatch();
        PillButton applyButton, undoButton, testButton, lengthButton, moreButton;
        Label gpuLabel, currentLabel, calibrationLink, summaryLabel;
        ResultsCard results;
        StatusLine status;

        byte[] reference;                // this GPU's starting table; null until one exists
        bool referenceFromVbios;
        Limits limits;
        Preset[] presets = new Preset[0];
        CalibrationResult calibration;   // what a calibration measured on this GPU; null until one was run
        Sensors sensors;
        Settings active;                 // what is applied to the GPU right now (the ghost markers)
        Mode mode = Mode.Idle;
        bool needsReboot;
        volatile bool stopRequested;
        int testSeconds = Durations.Default;
        Prefs prefs = new Prefs();
        History history;                 // one reading per second, for the graphs
        readonly List<Graph> graphs = new List<Graph>();
        Graph speedGraph, voltageGraph, powerGraph, tempGraph;

        /// <summary>Set when Settings were saved: the window is rebuilt so new colours and layout take effect.</summary>
        public bool RestartRequested;

        // the last test, and what it is compared with
        LoadResult lastTest, lastBefore;
        string lastVersus = "", lastFooter = "";
        // what the progress display says while something is running
        string progressTitle = "", progressFooter = "", calWhat = "";
        int calStep, calTotal;

        string TablesDir { get { return Path.Combine(appDir, "tables"); } }
        string ProfileDir { get { return Path.Combine(appDir, "profiles"); } }
        string BackupDir { get { return Path.Combine(appDir, "backups"); } }
        string PrefsPath { get { return Path.Combine(appDir, "prefs.txt"); } }

        public MainForm(Gpu gpu, string startupNote)
        {
            this.gpu = gpu;
            try { prefs = Prefs.Parse(File.ReadAllText(PrefsPath)); } catch (IOException) { }
            testSeconds = prefs.TestSeconds;
            history = new History(Prefs.HistoryOptions[Prefs.HistoryOptions.Length - 1]);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = App.Name;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Window; ForeColor = Theme.Ink; Font = Theme.Body(9f);
            ClientSize = new Size(prefs.Monitor ? TuneWidth + MonitorWidth : TuneWidth, 800);

            // header: what this is on the left, which GPU and its state on the right
            MakeLabel(this, App.Name, Theme.Display(21f), Theme.Ink, 18, 14, 300, 38, ContentAlignment.MiddleLeft);
            MakeLabel(this, App.Tagline, Theme.Body(9.5f), Theme.Muted, 22, 52, 300, 20, ContentAlignment.MiddleLeft);
            gpuLabel = MakeLabel(this, "", Theme.Display(11f), Theme.Ink, 300, 14, 360, 22, ContentAlignment.MiddleRight);
            currentLabel = MakeLabel(this, "", Theme.Body(9.5f), Theme.Accent, 300, 36, 360, 20, ContentAlignment.MiddleRight);
            calibrationLink = MakeLabel(this, "", new Font("Segoe UI", 8.75f, FontStyle.Underline), Theme.Teal, 300, 56, 360, 18, ContentAlignment.MiddleRight);
            calibrationLink.Cursor = Cursors.Hand;
            calibrationLink.Click += delegate { CalibrateClicked(); };
            tips.SetToolTip(calibrationLink, "Measures this GPU step by step and sets its limits and presets from the results.");

            for (int i = 0; i < 4; i++)
            {
                int index = i;
                presetButtons[i] = MakeButton(this, "", 20 + i * 122, 90, 114, 36, false, delegate { if (index < presets.Length) SetSliders(presets[index].Settings); });
            }
            moreButton = MakeButton(this, "More ▾", 546, 90, 114, 36, false, MoreClicked);
            summaryLabel = MakeLabel(this, "", Theme.Body(9f), Theme.Muted, 22, 132, 638, 20, ContentAlignment.MiddleLeft);
            summaryLabel.AutoEllipsis = true;

            // the four ceilings
            Card card = new Card();
            card.SetBounds(20, 160, 640, 12 + 4 * RowHeight);
            card.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Groove))
                    for (int r = 1; r < 4; r++) e.Graphics.DrawLine(pen, 20, 6 + r * RowHeight, card.Width - 20, 6 + r * RowHeight);
            };
            Controls.Add(card);
            for (int i = 0; i < 4; i++)
            {
                int y = 8 + i * RowHeight;
                MakeLabel(card, Titles[i], Theme.Display(11.5f), Theme.Ink, 20, y + 6, 240, 24, ContentAlignment.MiddleLeft);
                values[i] = new ValueLabel(); values[i].SetBounds(430, y + 2, 190, 32); card.Controls.Add(values[i]);
                hints[i] = MakeLabel(card, "", Theme.Body(8.75f), Theme.Muted, 21, y + 29, 400, 17, ContentAlignment.MiddleLeft);
                Rail rail = new Rail();
                rail.SetBounds(6, y + 44, 628, 52);
                rail.AccessibleName = Titles[i];
                rail.ValueChanged += delegate { RefreshTexts(); };
                tips.SetToolTip(rail, RailTip);
                card.Controls.Add(rail);
                rails[i] = rail;
            }

            // measuring
            results = new ResultsCard();
            results.SetBounds(20, card.Bottom + 12, 640, 92);
            results.Font = Theme.Body(9f);
            Controls.Add(results);

            // actions: changing on the left, measuring on the right
            int by = results.Bottom + 14;
            applyButton = MakeButton(this, "Apply to GPU", 20, by, 160, 40, true, ApplyClicked);
            undoButton = MakeButton(this, "Undo changes", 188, by, 130, 40, false, delegate { if (active != null) SetSliders(limits.Clamp(active)); });
            testButton = MakeButton(this, "", 346, by, 200, 40, false, TestClicked);
            lengthButton = MakeButton(this, "Length ▾", 554, by, 106, 40, false, LengthClicked);
            tips.SetToolTip(applyButton, "Writes these settings and restarts the GPU so it uses them (a few seconds).");
            tips.SetToolTip(undoButton, "Puts the stops back on the applied values.");
            tips.SetToolTip(testButton, "Loads the GPU and measures what it does. Always tests the applied settings.");
            tips.SetToolTip(lengthButton, "How long a test runs. Longer tests show the temperature the GPU settles at.");
            status = new StatusLine();
            status.SetBounds(22, by + 52, 638, 44);
            Controls.Add(status);
            if (prefs.Monitor) BuildMonitor();
            ResumeLayout(false);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (Height > area.Height) { AutoScroll = true; Height = area.Height; }

            LoadGpuState();
            if (startupNote != null) status.Say(Note.Warn(startupNote));
            OpenSensors();
            sensorTimer.Interval = 1000;
            sensorTimer.Tick += delegate { ReadSensors(); };
            sensorTimer.Start();
            ReadSensors();
            progressTimer.Interval = 250;
            progressTimer.Tick += delegate { UpdateProgress(); };
            FormClosed += delegate { sensorTimer.Stop(); progressTimer.Stop(); stopRequested = true; CloseSensors(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleTitleBar(Handle); }

        /// <summary>The right-hand column: one graph per value chosen in Settings, sharing the height evenly.</summary>
        void BuildMonitor()
        {
            int x = TuneWidth, w = MonitorWidth - 20;
            MakeLabel(this, "Monitor", Theme.Display(11.5f), Theme.Ink, x, 18, 200, 24, ContentAlignment.MiddleLeft);
            string span = prefs.HistorySeconds >= 120 ? "last " + prefs.HistorySeconds / 60 + " minutes" : "last minute";
            MakeLabel(this, span + ", one reading a second", Theme.Body(8.75f), Theme.Muted, x + 150, 20, w - 150, 20, ContentAlignment.MiddleRight);

            if (prefs.GraphSpeed) { speedGraph = new Graph(); speedGraph.Title = "Speed"; speedGraph.Unit = "MHz"; graphs.Add(speedGraph); }
            if (prefs.GraphVoltage) { voltageGraph = new Graph(); voltageGraph.Title = "Voltage"; voltageGraph.Unit = "mV"; graphs.Add(voltageGraph); }
            if (prefs.GraphPower) { powerGraph = new Graph(); powerGraph.Title = "Power"; powerGraph.Unit = "W"; graphs.Add(powerGraph); }
            if (prefs.GraphTemp) { tempGraph = new Graph(); tempGraph.Title = "Hotspot"; tempGraph.Unit = "°C"; tempGraph.SecondName = "GPU temperature"; graphs.Add(tempGraph); }
            if (graphs.Count == 0) { MakeLabel(this, "No graphs are switched on. Choose them under More ▾ > Settings.", Theme.Body(9f), Theme.Muted, x, 60, w, 40, ContentAlignment.TopLeft); return; }

            int top = 52, gap = 8, each = Math.Min(260, (786 - top - gap * (graphs.Count - 1)) / graphs.Count);
            for (int i = 0; i < graphs.Count; i++)
            {
                graphs[i].SetBounds(x, top + i * (each + gap), w, each);
                Controls.Add(graphs[i]);
            }
            tips.SetToolTip(graphs[graphs.Count - 1], "The dashed line is the limit applied to the GPU. A line pressed against it is what holds the GPU back.");
        }

        void UpdateGraphs()
        {
            if (graphs.Count == 0) return;
            int n = prefs.HistorySeconds;
            // each dashed line is the ceiling that is applied right now; none while this GPU has no table
            if (speedGraph != null) speedGraph.SetData(history.Series(delegate (Reading r) { return r.ClockMHz; }, n), null, active == null ? -1 : active.MaxClockMHz, n);
            if (voltageGraph != null) voltageGraph.SetData(history.Series(delegate (Reading r) { return r.VoltageMv; }, n), null, active == null ? -1 : active.MaxVoltageMv, n);
            if (powerGraph != null) powerGraph.SetData(history.Series(delegate (Reading r) { return r.PowerW; }, n), null, active == null ? -1 : active.PowerLimitW, n);
            if (tempGraph != null) tempGraph.SetData(history.Series(delegate (Reading r) { return r.HotspotC; }, n), history.Series(delegate (Reading r) { return r.EdgeC; }, n), active == null ? -1 : active.TempLimitC, n);
        }

        void SavePrefs() { try { File.WriteAllText(PrefsPath, prefs.Format()); } catch (IOException) { } }

        static Label MakeLabel(Control parent, string text, Font font, Color color, int x, int y, int w, int h, ContentAlignment align)
        {
            Label l = new Label();
            l.AutoSize = false; l.Text = text; l.Font = font; l.ForeColor = color; l.BackColor = parent is Card ? Theme.Panel : Theme.Window; l.TextAlign = align;
            l.SetBounds(x, y, w, h);
            parent.Controls.Add(l);
            return l;
        }

        static PillButton MakeButton(Control parent, string text, int x, int y, int w, int h, bool primary, EventHandler onClick)
        {
            PillButton b = new PillButton();
            b.Text = text; b.Primary = primary;
            b.SetBounds(x, y, w, h);
            b.Click += onClick;
            parent.Controls.Add(b);
            return b;
        }

        // ---- state ----

        /// <summary>(Re)loads this GPU's reference table, calibration, limits, presets and applied settings, and sets up the sliders.</summary>
        void LoadGpuState()
        {
            gpuLabel.Text = gpu.Name + (gpu.IsTested ? "" : "  ·  untested model");
            reference = gpu.LoadReference(TablesDir, out referenceFromVbios);
            if (reference == null)
            {
                limits = null; presets = new Preset[0]; active = null; calibration = null;
                foreach (PillButton b in presetButtons) { b.Text = "-"; b.Enabled = false; }
                foreach (Rail r in rails) { r.Enabled = false; r.SetApplied(-1); }
                for (int i = 0; i < 4; i++) { values[i].Set("-", Units[i]); hints[i].Text = ""; }
                currentLabel.Text = "No table yet"; calibrationLink.Text = ""; summaryLabel.Text = "";
                applyButton.Enabled = undoButton.Enabled = testButton.Enabled = lengthButton.Enabled = false;
                testButton.Text = Durations.ButtonText(testSeconds);
                ShowIdleResults();
                status.Say(Note.Warn(App.Name + " needs this GPU's own power table to start from. Save the GPU's vBIOS with GPU-Z, then choose More ▾ > Import vBIOS file."));
                return;
            }

            calibration = Calibrator.Load(gpu, TablesDir);
            limits = gpu.LimitsFor(reference, TablesDir);   // the ranges chosen in Settings, or the defaults; calibration only sets presets
            presets = Presets.For(limits, calibration);
            Settings f = limits.Factory;
            rails[0].Configure(limits.ClockMin, limits.ClockMax, Limits.ClockStep, f.MaxClockMHz, f.MaxClockMHz);   // above factory = overclock zone
            rails[1].Configure(limits.PowerMin, limits.PowerMax, 1, f.PowerLimitW, int.MaxValue);
            rails[2].Configure(limits.VoltMin, limits.VoltMax, Limits.VoltStep, f.MaxVoltageMv, int.MaxValue);
            rails[3].Configure(limits.TempMin, limits.TempMax, 1, f.TempLimitC, int.MaxValue);
            for (int i = 0; i < presetButtons.Length; i++)
            {
                presetButtons[i].Text = presets[i].Name;
                tips.SetToolTip(presetButtons[i], presets[i].Summary + "\r\n" + presets[i].Settings.Pretty());
            }

            string problem = ReloadActive();
            Note hint = Note.Info("Drag a stop or pick a preset, then press \"Apply to GPU\". Teal markers show what the GPU is doing now.");
            if (calibration == null) hint = Note.Info("The presets are rules of thumb until this GPU is measured. Use \"Not calibrated\" at the top right to measure it.");
            else if (!referenceFromVbios) hint = Note.Info("Limits come from the table found on this PC, which may include earlier edits. Import the vBIOS (More ▾) for true factory limits.");
            SetMode(Mode.Idle, problem != null ? Note.Warn(problem) : hint);
            SetSliders(limits.Clamp(active));
            ShowIdleResults();
        }

        Settings Pending() { return new Settings(rails[0].Value, rails[1].Value, rails[2].Value, rails[3].Value); }

        void SetSliders(Settings s)
        {
            rails[0].Value = s.MaxClockMHz; rails[1].Value = s.PowerLimitW; rails[2].Value = s.MaxVoltageMv; rails[3].Value = s.TempLimitC;
            RefreshTexts();
        }

        /// <summary>Re-reads what is applied on this PC. Returns a problem to show, or null.</summary>
        string ReloadActive()
        {
            try { active = gpu.Current(reference); return null; }
            catch (TableFormatException)
            {
                active = limits.Factory.Clone();
                return "The power table set on this PC is not one " + App.Name + " can read. Applying anything here replaces it.";
            }
        }

        void RefreshTexts()
        {
            if (limits == null || active == null) return;
            Settings p = Pending();
            int[] v = { p.MaxClockMHz, p.PowerLimitW, p.MaxVoltageMv, p.TempLimitC };
            int[] a = { active.MaxClockMHz, active.PowerLimitW, active.MaxVoltageMv, active.TempLimitC };
            string[] advice = { Advice.Clock(v[0], limits), Advice.Power(v[1], limits), Advice.Voltage(v[2], limits), Advice.Temp(v[3], limits) };
            for (int i = 0; i < 4; i++)
            {
                values[i].Set(v[i].ToString(), Units[i]);
                hints[i].Text = advice[i];
                rails[i].SetApplied(a[i]);   // the ghost stays where the GPU really is
            }

            // one line under the presets: what is waiting to be applied, otherwise what the current choice means
            Preset match = Presets.Find(presets, p);
            string waiting = Changes.Summary(active, p);
            summaryLabel.ForeColor = waiting.Length > 0 ? Theme.Amber : Theme.Muted;
            summaryLabel.Text = waiting.Length > 0 ? waiting : match != null ? match.Name + " — " + match.Summary : "Custom settings.";
            tips.SetToolTip(summaryLabel, waiting.Length > 0 ? string.Join("\r\n", Changes.Describe(active, p)) : "");
            for (int i = 0; i < presetButtons.Length; i++) presetButtons[i].Selected = match == presets[i];

            Preset running = Presets.Find(presets, active);
            currentLabel.Text = needsReboot ? "Saved — starts after a reboot" : "Applied: " + (running != null ? running.Name : "custom settings");
            calibrationLink.Text = calibration == null ? "Not calibrated — measure this GPU" : "Calibrated " + calibration.Date;

            bool idle = mode == Mode.Idle, changed = waiting.Length > 0;
            applyButton.Enabled = idle && (changed || needsReboot);
            undoButton.Enabled = idle && changed;
            lengthButton.Enabled = idle;
            testButton.Enabled = idle || ((mode == Mode.Testing || mode == Mode.Calibrating) && !stopRequested);   // doubles as the stop button
            testButton.Text = mode == Mode.Testing ? "Stop test" : mode == Mode.Calibrating ? "Stop calibration" : Durations.ButtonText(testSeconds);
        }

        void SetMode(Mode m, Note note)
        {
            mode = m;
            bool idle = m == Mode.Idle;
            foreach (Rail r in rails) r.Enabled = idle;
            foreach (PillButton b in presetButtons) b.Enabled = idle;
            foreach (ValueLabel v in values) v.Enabled = idle;
            moreButton.Enabled = idle;
            calibrationLink.Enabled = idle;
            Cursor = idle ? Cursors.Default : Cursors.AppStarting;
            if (note != null) status.Say(note);
            RefreshTexts();
        }

        /// <summary>What the measuring panel shows when nothing is running.</summary>
        void ShowIdleResults()
        {
            if (lastTest != null) results.ShowResult(lastTest, lastBefore, lastVersus, lastFooter);
            else if (calibration != null) results.ShowResult(calibration.Baseline, null, "factory", "Factory baseline, measured during calibration on " + calibration.Date + ". " + calibration.Describe(limits.Factory));
            else if (limits != null) results.ShowMessage("Press \"" + Durations.ButtonText(testSeconds) + "\" to measure speed, voltage, power and peak temperature under load.");
            else results.ShowMessage("Nothing to test yet.");
        }

        void UpdateProgress()
        {
            int elapsed = (int)workClock.Elapsed.TotalSeconds;
            if (mode == Mode.Testing)
                results.ShowProgress(progressTitle, elapsed / (double)(testSeconds + 3),
                    Durations.Clock(Math.Min(elapsed, testSeconds)) + " / " + Durations.Clock(testSeconds), progressFooter);
            else if (mode == Mode.Calibrating)
            {
                double within = Math.Min(1.0, stepClock.Elapsed.TotalSeconds / (testSeconds + 15));
                results.ShowProgress(calStep == 0 ? "Calibrating" : "Calibrating: " + calWhat, calTotal == 0 ? 0 : (Math.Max(0, calStep - 1) + within) / calTotal,
                    (calStep == 0 ? "" : "step " + calStep + " of at most " + calTotal + "  ·  ") + Durations.Clock(elapsed),
                    "Do not use the GPU. Your settings are put back when this ends.");
            }
        }

        // ---- live readings ----

        void OpenSensors() { if (sensors == null) sensors = Sensors.Open(gpu.Name); }
        void CloseSensors() { if (sensors != null) { sensors.Dispose(); sensors = null; } }

        void ReadSensors()
        {
            if (WindowState == FormWindowState.Minimized) return;
            Reading r = sensors == null ? null : sensors.Read();
            if (r != null) { history.Add(r); UpdateGraphs(); }     // the graphs work even on a GPU that cannot be tuned
            if (limits == null) return;
            if (r == null) { foreach (Rail rail in rails) rail.SetLive(-1, ""); return; }
            if (r.PowerW <= 0 && r.HotspotC <= 0)
            {
                // Windows powers a laptop's discrete GPU off when nothing uses it; every sensor then reads zero.
                rails[0].SetLive(0, "GPU is asleep");
                for (int i = 1; i < 4; i++) rails[i].SetLive(-1, "");
                return;
            }
            rails[0].SetLive(r.ClockMHz, r.ClockMHz <= 0 ? "idle" : "now " + r.ClockMHz + " MHz");
            rails[1].SetLive(r.PowerW, "now " + r.PowerW + " W");
            rails[2].SetLive(r.VoltageMv <= 0 ? -1 : r.VoltageMv, "now " + r.VoltageMv + " mV");
            // The limit acts on the hotspot (the hottest point of the chip), which runs well above the GPU temperature.
            rails[3].SetLive(r.HotspotC, "hotspot " + r.HotspotC + " °C  ·  GPU " + r.EdgeC + " °C");
        }

        // ---- running slow work ----

        /// <summary>Runs slow work off the window's thread; the note it returns becomes the status line.</summary>
        void RunWork(Func<Note> work)
        {
            Thread t = new Thread(delegate ()
            {
                Note note;
                try { note = work(); }
                catch (Exception ex) { note = Note.Bad("That did not work: " + ex.Message); }
                try { BeginInvoke((MethodInvoker)delegate { WorkDone(note); }); }
                catch (InvalidOperationException) { /* window closed meanwhile */ }
            });
            t.IsBackground = true;
            t.Start();
        }

        void WorkDone(Note note)
        {
            progressTimer.Stop();
            OpenSensors();
            sensorTimer.Start();
            bool wasCalibrating = mode == Mode.Calibrating;
            stopRequested = false;
            if (wasCalibrating)
            {
                lastTest = null;
                mode = Mode.Idle;
                LoadGpuState();            // picks up the new limits and presets
                status.Say(note);
                return;
            }
            string problem = ReloadActive();
            SetMode(Mode.Idle, problem == null ? note : Note.Warn(note.Text + "\r\n" + problem));
            ShowIdleResults();
        }

        /// <summary>Called on the worker thread: asks before restarting a GPU that programs are using.</summary>
        bool AppsAllowRestart()
        {
            List<string> apps = gpu.AppsUsingIt();
            if (apps.Count == 0) return true;
            bool go = false;
            Invoke((MethodInvoker)delegate
            {
                go = MessageBox.Show(this,
                    "These programs are using the " + gpu.Name + " and will probably crash when it restarts:\r\n\r\n    "
                  + string.Join(", ", apps.ToArray()) + "\r\n\r\nClose them first, then choose OK.",
                    "Programs are using the GPU", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
            });
            return go;
        }

        bool ConfirmUntested()
        {
            if (gpu.IsTested) return true;
            string ack = Path.Combine(TablesDir, gpu.Key + ".accepted");
            if (File.Exists(ack)) return true;
            if (MessageBox.Show(this,
                    App.Name + " has only been measured on a Radeon RX 5500M laptop. Your " + gpu.Name + " uses the same table format, "
                  + "so it should behave the same way, but nobody has confirmed that yet.\r\n\r\n"
                  + "The limits still apply (power, voltage and temperature can only go down). If anything goes wrong, apply Factory "
                  + "or run Reset-to-factory.cmd.\r\n\r\nContinue?",
                    "Untested GPU model", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return false;
            try { Directory.CreateDirectory(TablesDir); File.WriteAllText(ack, DateTime.Now.ToString("s")); } catch (IOException) { }
            return true;
        }

        // ---- apply ----

        void ApplyClicked(object sender, EventArgs e)
        {
            if (mode != Mode.Idle || limits == null) return;
            Settings want = Pending();
            if (!ConfirmUntested()) return;
            if (Advice.IsOverclock(want, limits) && MessageBox.Show(this,
                    "Top speed " + want.MaxClockMHz + " MHz is above factory. The GPU will run hotter and games can crash.\r\n\r\n"
                  + "If that happens, open " + App.Name + " and apply the Factory preset.",
                    "Overclock", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            SetMode(Mode.Applying, Note.Info("Checking what is using the GPU..."));
            RunWork(delegate
            {
                if (!AppsAllowRestart()) return Note.Info("Nothing was changed.");
                Invoke((MethodInvoker)delegate
                {
                    status.Say(Note.Info("Restarting the GPU (takes a few seconds)..."));
                    sensorTimer.Stop();
                    CloseSensors();
                });
                string detail;
                RestartOutcome outcome = gpu.Apply(reference, want, BackupDir, out detail);
                needsReboot = outcome != RestartOutcome.Restarted;
                return outcome == RestartOutcome.Restarted
                    ? Note.Good("Applied. Press \"" + Durations.ButtonText(testSeconds) + "\" to see what the GPU does under load.")
                    : Note.Warn(Pnp.Message(outcome, detail));
            });
        }

        // ---- test ----

        /// <summary>For "--preview FILE --show NAME": moves the stops to a preset without applying it.</summary>
        public void ShowPreset(string name) { Preset p = Presets.ByName(presets, name); if (p != null) SetSliders(p.Settings); }

        /// <summary>For "--preview FILE --test": true while something is running.</summary>
        public bool Busy { get { return mode != Mode.Idle; } }
        public void StartTest() { if (mode == Mode.Idle && limits != null) TestClicked(this, EventArgs.Empty); }

        void TestClicked(object sender, EventArgs e)
        {
            if (mode == Mode.Testing || mode == Mode.Calibrating)
            {
                stopRequested = true;
                status.Say(Note.Info(mode == Mode.Testing ? "Stopping the test..." : "Stopping after the current step, then putting your settings back..."));
                RefreshTexts();
                return;
            }
            if (mode != Mode.Idle || limits == null) return;

            // A test always measures what is applied to the GPU - never the stops that have not been applied yet.
            bool unapplied = !Pending().Equals(active);
            Settings tested = active.Clone();
            int seconds = testSeconds;
            progressTitle = "Testing the applied settings";
            progressFooter = tested.Pretty() + (unapplied ? "   ·   your unapplied changes are not part of this test" : "");
            workClock.Restart();
            stopRequested = false;
            SetMode(Mode.Testing, unapplied
                ? Note.Warn("This test runs on the applied settings (the ghost markers), not on the changes you have not applied yet.")
                : Note.Info("Testing for " + Durations.Label(seconds) + ". The fans will speed up; watch the teal markers."));
            progressTimer.Start();
            UpdateProgress();

            RunWork(delegate
            {
                LoadResult r = LoadTest.RunInChild(seconds, gpu.Name, delegate { return stopRequested; });
                if (!r.Ok) return r.Error == LoadTest.Stopped ? Note.Info("Test stopped. Nothing was measured.") : Note.Bad("Test failed: " + r.Error);
                // compare with the previous test, or with the calibrated factory baseline for the first one
                lastBefore = lastTest != null ? lastTest : calibration != null ? calibration.Baseline : null;
                lastVersus = lastTest != null ? "the last test" : calibration != null ? "the factory baseline" : "";
                lastFooter = "Tested for " + Durations.Label(seconds) + " on " + tested.Pretty() + (lastVersus.Length > 0 ? "   ·   changes are against " + lastVersus : "");
                lastTest = r;
                return r.PeakHotspot >= tested.TempLimitC - 1
                    ? Note.Warn("Test finished. The hotspot reached its limit, so the GPU slowed itself down.")
                    : Note.Good("Test finished. The GPU's calculations were checked and stayed correct.");
            });
        }

        void LengthClicked(object sender, EventArgs e)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            foreach (int option in Durations.Options)
            {
                int seconds = option;
                ToolStripMenuItem item = new ToolStripMenuItem(Durations.Label(seconds));
                item.Checked = seconds == testSeconds;
                item.Click += delegate
                {
                    testSeconds = seconds;
                    prefs.TestSeconds = seconds;
                    SavePrefs();
                    RefreshTexts();
                    ShowIdleResults();
                };
                menu.Items.Add(item);
            }
            menu.Show(lengthButton, new Point(0, -menu.PreferredSize.Height - 2));   // opens upwards: the button is at the bottom of the window
        }

        // ---- calibration ----

        void CalibrateClicked()
        {
            if (limits == null || mode != Mode.Idle || !ConfirmUntested()) return;
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                MessageBox.Show(this, "Plug the laptop in first. Calibration loads the GPU for several minutes.", "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Limits range = limits;   // the ranges chosen in Settings: calibration searches inside them and nowhere else
            int seconds = testSeconds;
            if (MessageBox.Show(this,
                    "Calibration measures this GPU step by step, inside the ranges chosen in Settings:\r\n\r\n"
                  + "  1. factory settings, as the baseline\r\n"
                  + "  2. a lower and lower voltage ceiling (down to " + range.VoltMin + " mV), until a test fails or speed drops\r\n"
                  + "  3. a higher and higher top speed (up to " + range.ClockMax + " MHz), until a test fails, the hotspot reaches its limit or it stops helping\r\n\r\n"
                  + "The results set the Efficient and Boost presets. The sliders keep the ranges from Settings.\r\n\r\n"
                  + "Each step is a " + Durations.Label(seconds) + " test (change that with \"Length\"), so this takes up to " + Calibrator.MinutesFor(range, seconds) + " minutes. "
                  + "The GPU restarts before every step and the fans will be loud. A failing step can make the GPU driver reset; that is expected and it recovers.\r\n\r\n"
                  + "Do not use the GPU meanwhile. Your current settings are put back at the end, and also if the run is stopped or interrupted.",
                    "Calibrate this GPU", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

            calStep = 0; calTotal = Calibration.MaxSteps(range); calWhat = "";
            stopRequested = false;
            workClock.Restart(); stepClock.Restart();
            SetMode(Mode.Calibrating, Note.Info("Calibrating. \"Stop calibration\" ends it and puts your settings back."));
            progressTimer.Start();
            UpdateProgress();

            RunWork(delegate
            {
                if (!AppsAllowRestart()) return Note.Info("Nothing was changed.");
                Invoke((MethodInvoker)delegate { sensorTimer.Stop(); CloseSensors(); });
                string restoreNote;
                CalibrationResult r = Calibrator.Run(gpu, reference, range, TablesDir, seconds,
                    delegate (int step, int total, string what)
                    {
                        try { BeginInvoke((MethodInvoker)delegate { calStep = step; calTotal = total; calWhat = what; stepClock.Restart(); }); }
                        catch (InvalidOperationException) { }
                    },
                    delegate (bool measuring)
                    {
                        // sensors are closed while the GPU restarts and reopened for each test, so the graphs keep running
                        try { Invoke((MethodInvoker)delegate { if (measuring) { OpenSensors(); sensorTimer.Start(); } else { sensorTimer.Stop(); CloseSensors(); } }); }
                        catch (InvalidOperationException) { }
                    },
                    delegate { return stopRequested; }, out restoreNote);
                if (r.Ok) return Note.Good("Calibration finished. " + r.Describe(range.Factory) + restoreNote);
                return r.Error == "Stopped."
                    ? Note.Info("Calibration stopped. Your previous settings were put back and nothing was saved." + restoreNote)
                    : Note.Bad("Calibration did not finish: " + r.Error + " Your previous settings were put back." + restoreNote);
            });
        }

        // ---- "More" menu: profiles, calibration, vBIOS import, data folder ----

        void MoreClicked(object sender, EventArgs e)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            if (limits != null)
            {
                menu.Items.Add("Save these settings as a profile...", null, delegate { SaveProfile(); });
                if (Directory.Exists(ProfileDir))
                    foreach (string file in Directory.GetFiles(ProfileDir, "*.profile"))
                    {
                        string path = file;
                        menu.Items.Add("Load profile: " + Path.GetFileNameWithoutExtension(path), null, delegate { LoadProfile(path); });
                    }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(calibration == null ? "Calibrate this GPU..." : "Calibrate this GPU again...", null, delegate { CalibrateClicked(); });
                if (calibration != null)
                    menu.Items.Add("Forget calibration (back to rules of thumb)", null, delegate { Calibrator.Forget(gpu, TablesDir); lastTest = null; LoadGpuState(); });
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add("Import vBIOS file...", null, delegate { ImportVbios(); });
            menu.Items.Add("Open data folder", null, delegate { Process.Start(appDir); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(prefs.Monitor ? "Hide the monitor graphs" : "Show the monitor graphs", null, delegate { prefs.Monitor = !prefs.Monitor; SavePrefs(); RestartRequested = true; Close(); });
            menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
            menu.Show(moreButton, new Point(0, moreButton.Height + 2));
        }

        /// <summary>For "--preview FILE --settings": the dialog as it would open now.</summary>
        public SettingsDialog NewSettingsDialog()
        {
            Limits hard = reference == null ? null : Limits.Hard(reference);
            return new SettingsDialog(prefs, hard, reference == null ? null : Limits.For(reference), limits);
        }

        void OpenSettings()
        {
            using (SettingsDialog d = NewSettingsDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                prefs = d.Result;
                SavePrefs();
                if (d.NewRanges != null)
                {
                    try { gpu.SaveRanges(TablesDir, d.RangesAreDefault ? null : d.NewRanges); }
                    catch (IOException ex) { MessageBox.Show(this, "The ranges could not be saved: " + ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
            RestartRequested = true;   // rebuilt with the new colours, graphs and ranges; nothing on the GPU changes
            Close();
        }

        void ImportVbios()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose the vBIOS of your " + gpu.Name + " (for example saved with GPU-Z)";
                d.Filter = "vBIOS or table files (*.rom;*.bin)|*.rom;*.bin|All files (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    byte[] table = Vbios.ExtractPowerTable(File.ReadAllBytes(d.FileName));
                    gpu.StoreReference(TablesDir, table, true);
                    LoadGpuState();
                    status.Say(Note.Good("Imported. Factory values for this GPU: " + limits.Factory.Pretty() + "."));
                }
                catch (TableFormatException ex) { status.Say(Note.Bad("Not imported: " + ex.Message)); }
                catch (IOException ex) { status.Say(Note.Bad("Not imported: " + ex.Message)); }
            }
        }

        void SaveProfile()
        {
            string name = Ask("Save profile", "Name for these settings:");
            if (name == null) return;
            try
            {
                string safe = ProfileText.SafeName(name);
                Directory.CreateDirectory(ProfileDir);
                File.WriteAllText(Path.Combine(ProfileDir, safe + ".profile"), ProfileText.Format(Pending()));
                status.Say(Note.Good("Saved profile \"" + safe + "\". Find it under More ▾."));
            }
            catch (ArgumentException ex) { status.Say(Note.Bad("Profile not saved: " + ex.Message)); }
            catch (IOException ex) { status.Say(Note.Bad("Profile not saved: " + ex.Message)); }
        }

        void LoadProfile(string path)
        {
            try
            {
                SetSliders(ProfileText.Parse(File.ReadAllText(path), limits));
                status.Say(Note.Info("Loaded \"" + Path.GetFileNameWithoutExtension(path) + "\". Press \"Apply to GPU\" to use it."));
            }
            catch (FormatException ex) { status.Say(Note.Bad("That profile could not be loaded: " + ex.Message)); }
            catch (IOException ex) { status.Say(Note.Bad("That profile could not be loaded: " + ex.Message)); }
        }

        string Ask(string title, string question)
        {
            using (Form f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.Font = Theme.Body(9f);
                f.BackColor = Theme.Window; f.ForeColor = Theme.Ink;
                f.AutoScaleDimensions = new SizeF(96F, 96F); f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(340, 124);
                f.HandleCreated += delegate { Theme.StyleTitleBar(f.Handle); };
                Label l = new Label(); l.Text = question; l.SetBounds(16, 14, 308, 18);
                TextBox t = new TextBox(); t.MaxLength = 40; t.SetBounds(16, 38, 308, 24);
                t.BackColor = Theme.Panel; t.ForeColor = Theme.Ink; t.BorderStyle = BorderStyle.FixedSingle;
                MakeButton(f, "Save", 152, 78, 82, 32, true, delegate { f.DialogResult = DialogResult.OK; });
                MakeButton(f, "Cancel", 242, 78, 82, 32, false, delegate { f.DialogResult = DialogResult.Cancel; });
                f.Controls.Add(l); f.Controls.Add(t);
                t.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; e.SuppressKeyPress = true; }
                    else if (e.KeyCode == Keys.Escape) f.DialogResult = DialogResult.Cancel;
                };
                f.ActiveControl = t;
                return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
            }
        }
    }
}
