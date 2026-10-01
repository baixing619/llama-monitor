using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LlamaMonitor
{
    /// <summary>A view of the main monitor's latest data; the mini window never polls on its own.</summary>
    internal sealed class MiniSnapshot
    {
        public bool Online, Paused, SupportsSlots;
        public string Service = "", Uptime = "", Gpu = "";
        public List<SlotRow> Slots = new List<SlotRow>();
    }

    internal sealed class MiniMonitorForm : Form
    {
        private static readonly Color BackgroundColor = Color.FromArgb(18, 20, 23);
        private static readonly Color PanelColor = Color.FromArgb(28, 31, 36);
        private static readonly Color BorderColor = Color.FromArgb(40, 44, 50);
        private static readonly Color TextColor = Color.FromArgb(226, 230, 236);
        private static readonly Color DimColor = Color.FromArgb(138, 146, 156);
        private static readonly Color AccentColor = Color.FromArgb(90, 180, 255);
        private static readonly Color OnlineColor = Color.FromArgb(110, 210, 130);
        private static readonly Color OfflineColor = Color.FromArgb(240, 105, 105);

        private readonly Font _normalFont = new Font("Segoe UI", 9f);
        private readonly Font _headingFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private readonly Font _valueFont = new Font("Consolas", 13f, FontStyle.Bold);
        private readonly ToolTip _toolTip = new ToolTip { ShowAlways = true };
        private readonly List<SlotCard> _cards = new List<SlotCard>();
        private readonly float _layoutScale;
        private Label _service, _uptime, _gpu, _slotHeading, _empty;
        private Button _fullButton, _pauseButton, _languageButton;
        private CheckBox _topmost;
        private MiniFlowPanel _slots;
        private MiniSnapshot _snapshot = new MiniSnapshot();
        private bool _raisingExit;

        public event EventHandler FullRequested;
        public event EventHandler ExitRequested;
        public event EventHandler PauseRequested;
        public event EventHandler LanguageRequested;

        public MiniMonitorForm(bool topmost)
        {
            _layoutScale = DesktopScale();
            // Fonts use points; scale the pixel layout once, including cards added later.
            AutoScaleMode = AutoScaleMode.None;
            Font = _normalFont;
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(450), Px(450));
            MinimumSize = new Size(Px(360), Px(350));
            TopMost = topmost;
            MaximizeBox = false;
            BuildUi(topmost);
            RenderSnapshot();
        }

        private static float DesktopScale()
        {
            try
            {
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float scale = graphics.DpiX / 96f;
                    if (scale > 0 && !float.IsNaN(scale) && !float.IsInfinity(scale)) return scale;
                }
            }
            catch { }
            return 1f;
        }

        private static int Pixels(int value, float scale) { return (int)Math.Round(value * scale); }
        private int Px(int value) { return Pixels(value, _layoutScale); }

        public void UpdateSnapshot(MiniSnapshot snapshot)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<MiniSnapshot>(UpdateSnapshot), snapshot); }
                catch (InvalidOperationException) { }
                return;
            }
            _snapshot = snapshot ?? new MiniSnapshot();
            RenderSnapshot();
        }

        private static string Local(string chinese, string english)
        {
            return UiText.English ? english : chinese;
        }

        private static string SingleLine(string text)
        {
            return (text ?? "").Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private Label MakeLabel(Color color, Font font)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = color,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = font,
                Margin = new Padding(0)
            };
        }

        private Button MakeButton(int width)
        {
            var button = new Button
            {
                Width = Px(width),
                Height = Px(28),
                FlatStyle = FlatStyle.Flat,
                BackColor = BackgroundColor,
                ForeColor = TextColor,
                Font = _normalFont,
                Margin = new Padding(0, 0, Px(6), 0)
            };
            button.FlatAppearance.BorderColor = BorderColor;
            return button;
        }

        private void BuildUi(bool topmost)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = BackgroundColor,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(124)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(28)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = PanelColor,
                Margin = new Padding(0),
                Padding = new Padding(Px(10), Px(8), Px(10), Px(6))
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(28)));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(22)));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.Controls.Add(header, 0, 0);

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = PanelColor,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            header.Controls.Add(toolbar, 0, 0);
            _fullButton = MakeButton(72);
            _pauseButton = MakeButton(72);
            _languageButton = MakeButton(48);
            _topmost = new CheckBox
            {
                Checked = topmost,
                Width = Px(78),
                Height = Px(28),
                BackColor = PanelColor,
                ForeColor = TextColor,
                Margin = new Padding(Px(2), 0, 0, 0)
            };
            toolbar.Controls.Add(_fullButton);
            toolbar.Controls.Add(_pauseButton);
            toolbar.Controls.Add(_languageButton);
            toolbar.Controls.Add(_topmost);
            _fullButton.Click += (s, e) => RaiseFullRequested();
            _pauseButton.Click += (s, e) =>
            {
                var handler = PauseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                if (!IsDisposed && !Disposing) RenderSnapshot();
            };
            _languageButton.Click += (s, e) =>
            {
                var handler = LanguageRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                if (!IsDisposed && !Disposing) RenderSnapshot();
            };
            _topmost.CheckedChanged += (s, e) => TopMost = _topmost.Checked;

            _service = MakeLabel(OnlineColor, _headingFont);
            _uptime = MakeLabel(DimColor, _normalFont);
            _gpu = MakeLabel(TextColor, _normalFont);
            header.Controls.Add(_service, 0, 1);
            header.Controls.Add(_uptime, 0, 2);
            header.Controls.Add(_gpu, 0, 3);
            _service.DoubleClick += (s, e) => RaiseFullRequested();

            _slotHeading = MakeLabel(AccentColor, _normalFont);
            _slotHeading.Padding = new Padding(Px(10), 0, Px(10), 0);
            root.Controls.Add(_slotHeading, 0, 1);
            _slots = new MiniFlowPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = BackgroundColor,
                Padding = new Padding(Px(10), 0, Px(10), Px(10)),
                Margin = new Padding(0)
            };
            root.Controls.Add(_slots, 0, 2);
            _empty = MakeLabel(DimColor, _normalFont);
            _empty.Dock = DockStyle.None;
            _empty.AutoEllipsis = false;
            _empty.Height = Px(72);
            _empty.Padding = new Padding(Px(8));
            _empty.BackColor = PanelColor;
            _empty.Margin = new Padding(0);
            _slots.Controls.Add(_empty);
            _slots.ClientSizeChanged += (s, e) => SizeCards();
        }

        private void SetLabel(Label label, string text)
        {
            if (label.Text != text) label.Text = text;
            _toolTip.SetToolTip(label, text);
        }

        private void RenderSnapshot()
        {
            Text = Local("LlamaMonitor — 迷你监控", "LlamaMonitor — Mini Monitor");
            _fullButton.Text = Local("完整窗口", "Full");
            _pauseButton.Text = Local(_snapshot.Paused ? "继续" : "暂停", _snapshot.Paused ? "Resume" : "Pause");
            _languageButton.Text = Local("EN", "中文");
            _topmost.Text = Local("置顶", "Pin");
            _toolTip.SetToolTip(_fullButton, Local("恢复完整监控窗口", "Restore the full monitor"));
            _toolTip.SetToolTip(_pauseButton, Local("暂停或继续共享数据刷新", "Pause or resume shared data refresh"));
            _toolTip.SetToolTip(_topmost, Local("将迷你窗口保持在最上层", "Keep this window on top"));

            string service = SingleLine(_snapshot.Service);
            string state = _snapshot.Online ? Local("在线", "Online") : Local("离线", "Offline");
            string paused = _snapshot.Paused ? Local(" · 已暂停", " · Paused") : "";
            SetLabel(_service, "● " + state + paused + (service.Length > 0 ? "   " + service : ""));
            _service.ForeColor = _snapshot.Online ? OnlineColor : OfflineColor;
            SetLabel(_uptime, _snapshot.Online && !string.IsNullOrEmpty(_snapshot.Uptime)
                ? SingleLine(_snapshot.Uptime) : Local("运行时间不可用", "Uptime unavailable"));
            SetLabel(_gpu, _snapshot.Online && !string.IsNullOrEmpty(_snapshot.Gpu)
                ? SingleLine(_snapshot.Gpu) : Local("GPU 数据不可用", "GPU data unavailable"));

            var rows = new List<SlotRow>();
            if (_snapshot.Online && _snapshot.SupportsSlots && _snapshot.Slots != null)
                foreach (var row in _snapshot.Slots)
                    if (row != null) rows.Add(row);

            _slots.SuspendLayout();
            try
            {
                while (_cards.Count > rows.Count)
                {
                    var card = _cards[_cards.Count - 1];
                    _cards.RemoveAt(_cards.Count - 1);
                    _slots.Controls.Remove(card);
                    card.Dispose();
                }
                while (_cards.Count < rows.Count)
                {
                    var card = new SlotCard(_normalFont, _headingFont, _valueFont, _toolTip, _layoutScale);
                    _cards.Add(card);
                    _slots.Controls.Add(card);
                }
                for (int i = 0; i < rows.Count; i++) _cards[i].UpdateRow(rows[i]);

                _empty.Visible = rows.Count == 0;
                if (!_snapshot.Online)
                {
                    _slotHeading.Text = Local("槽位数据不可用", "Slot data unavailable");
                    _empty.Text = Local("服务离线，当前数据不可用。", "Service offline. Current data is unavailable.");
                }
                else if (!_snapshot.SupportsSlots)
                {
                    _slotHeading.Text = Local("槽位信息", "Slot information");
                    _empty.Text = Local("此后端不提供槽位明细。", "This backend does not provide per-slot data.");
                }
                else
                {
                    _slotHeading.Text = string.Format(CultureInfo.InvariantCulture,
                        Local("槽位  {0}", "Slots  {0}"), rows.Count);
                    _empty.Text = Local("当前没有可用槽位数据。", "No slot data is currently available.");
                }
                SizeCards();
            }
            finally { _slots.ResumeLayout(true); }
        }

        private void SizeCards()
        {
            if (_slots == null) return;
            // Reserve scrollbar space even before it appears, so card widths do not oscillate.
            int width = Math.Max(1, _slots.ClientSize.Width - _slots.Padding.Horizontal
                - SystemInformation.VerticalScrollBarWidth - Px(1));
            if (_empty.Width != width) _empty.Width = width;
            foreach (var card in _cards)
                if (card.Width != width) card.Width = width;
        }

        private void RaiseFullRequested()
        {
            var handler = FullRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || e.CloseReason != CloseReason.UserClosing || _raisingExit) return;
            _raisingExit = true;
            try
            {
                var handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            finally { _raisingExit = false; }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _toolTip.Dispose();
                _normalFont.Dispose();
                _headingFont.Dispose();
                _valueFont.Dispose();
            }
        }

        private sealed class MiniFlowPanel : FlowLayoutPanel
        {
            public MiniFlowPanel() { DoubleBuffered = true; }
        }

        private sealed class SlotCard : Panel
        {
            private readonly ToolTip _toolTip;
            private readonly float _layoutScale;
            private readonly Label _title, _generatedCaption, _cachedCaption, _speedCaption;
            private readonly Label _generated, _cached, _speed, _context;

            public SlotCard(Font normalFont, Font headingFont, Font valueFont, ToolTip toolTip, float layoutScale)
            {
                _toolTip = toolTip;
                _layoutScale = layoutScale;
                DoubleBuffered = true;
                BackColor = PanelColor;
                BorderStyle = BorderStyle.FixedSingle;
                Height = Px(142);
                Margin = new Padding(0, 0, 0, Px(8));
                Padding = new Padding(Px(8), Px(5), Px(8), Px(5));

                var table = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 3,
                    RowCount = 4,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    BackColor = PanelColor
                };
                for (int i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(27)));
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(30)));
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(22)));
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                Controls.Add(table);
                _title = CardLabel(headingFont, AccentColor);
                _generatedCaption = CardLabel(normalFont, DimColor);
                _cachedCaption = CardLabel(normalFont, DimColor);
                _speedCaption = CardLabel(normalFont, DimColor);
                _generated = CardLabel(valueFont, TextColor);
                _cached = CardLabel(valueFont, TextColor);
                _speed = CardLabel(valueFont, OnlineColor);
                _context = CardLabel(normalFont, TextColor);
                table.Controls.Add(_title, 0, 0);
                table.SetColumnSpan(_title, 3);
                table.Controls.Add(_context, 0, 1);
                table.SetColumnSpan(_context, 3);
                table.Controls.Add(_generatedCaption, 0, 2);
                table.Controls.Add(_cachedCaption, 1, 2);
                table.Controls.Add(_speedCaption, 2, 2);
                table.Controls.Add(_generated, 0, 3);
                table.Controls.Add(_cached, 1, 3);
                table.Controls.Add(_speed, 2, 3);
            }

            private int Px(int value) { return Pixels(value, _layoutScale); }

            private Label CardLabel(Font font, Color color)
            {
                return new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = font,
                    ForeColor = color,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(Px(2), 0, Px(2), 0)
                };
            }

            private void SetText(Label label, string text)
            {
                if (label.Text != text) label.Text = text;
                _toolTip.SetToolTip(label, text);
            }

            public void UpdateRow(SlotRow row)
            {
                SetText(_title, string.Format(CultureInfo.InvariantCulture, Local("槽 {0} · {1}", "Slot {0} · {1}"),
                    row.Id, UiText.T(row.Processing ? "Processing" : "Idle")));
                SetText(_generatedCaption, Local("已生成", "Generated"));
                SetText(_cachedCaption, Local("缓存", "Cached"));
                SetText(_speedCaption, Local("当前 tok/s", "Current tok/s"));
                SetText(_generated, row.NDecoded < 0 ? "—" : row.NDecoded.ToString("N0", CultureInfo.InvariantCulture));
                SetText(_cached, row.PromptCache < 0 ? "—" : row.PromptCache.ToString("N0", CultureInfo.InvariantCulture));
                double speed = row.TpsInstant;
                SetText(_speed, !row.Processing || row.NDecoded < 0
                    || double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0
                    ? "—" : speed.ToString("0.0", CultureInfo.InvariantCulture));
                SetText(_context, Local("Token 占用  ", "Token usage  ")
                    + (row.NCtx <= 0 || row.NPast < 0 || string.IsNullOrEmpty(row.ContextText)
                        ? "—" : SingleLine(row.ContextText)));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    foreach (var label in new[] { _title, _generatedCaption, _cachedCaption,
                        _speedCaption, _generated, _cached, _speed, _context })
                        _toolTip.SetToolTip(label, null);
                base.Dispose(disposing);
            }
        }
    }
}
