using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PcHealth
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Panel _content;
        private readonly Label _footer;
        private readonly Button _refreshBtn;
        private readonly Timer _timer;

        public MainForm()
        {
            Text = "电脑健康速览";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(420, 480);
            Size = new Size(460, 560);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = Color.FromArgb(24, 32, 48),
                Padding = new Padding(16, 12, 16, 12)
            };
            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "电脑健康速览",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(title);

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = Color.White,
                Padding = new Padding(12, 8, 12, 8)
            };
            _footer = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(100, 110, 125),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "正在读取…"
            };
            _refreshBtn = new Button
            {
                Dock = DockStyle.Right,
                Width = 80,
                Text = "刷新",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _refreshBtn.Width = 96;
            _refreshBtn.FlatAppearance.BorderSize = 0;
            _refreshBtn.Click += (_, __) => RefreshData();

            bottom.Controls.Add(_footer);
            bottom.Controls.Add(_refreshBtn);

            _content = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14)
            };

            Controls.Add(_content);
            Controls.Add(bottom);
            Controls.Add(header);

            _timer = new Timer { Interval = 30000 };
            _timer.Tick += (_, __) => RefreshData();

            Shown += (_, __) =>
            {
                RefreshData();
                _timer.Start();
            };
        }

        private void RefreshData()
        {
            _refreshBtn.Enabled = false;
            _footer.Text = "正在读取…";
            UseWaitCursor = true;
            try
            {
                var report = HealthCollector.Collect();
                Render(report);
                _footer.Text = $"耗时 {report.Elapsed.TotalMilliseconds:0} ms · 每 30 秒自动刷新";
            }
            catch (Exception ex)
            {
                _content.Controls.Clear();
                _content.Controls.Add(new Label
                {
                    Dock = DockStyle.Top,
                    Height = 80,
                    Text = "读取失败：" + ex.Message,
                    ForeColor = Color.Firebrick,
                    Padding = new Padding(4)
                });
                _footer.Text = "读取失败";
            }
            finally
            {
                UseWaitCursor = false;
                _refreshBtn.Enabled = true;
            }
        }

        private void Render(HealthReport report)
        {
            _content.SuspendLayout();
            _content.Controls.Clear();

            int y = 0;
            y = AddSection(_content, y, "磁盘 SMART", report.OverallDisk,
                report.Disks.Count == 0
                    ? "无数据"
                    : $"{HealthCollector.LevelText(report.OverallDisk)} · 共 {report.Disks.Count} 块");

            foreach (var d in report.Disks)
                y = AddCard(_content, y, d);

            y += 8;
            y = AddSection(_content, y, "主要温度", report.OverallTemp,
                HealthCollector.LevelText(report.OverallTemp));

            foreach (var t in report.Temps)
                y = AddCard(_content, y, t);

            if (!string.IsNullOrWhiteSpace(report.Note))
            {
                y += 6;
                var note = new Label
                {
                    Left = 4,
                    Top = y,
                    Width = _content.ClientSize.Width - 36,
                    Height = 48,
                    Text = report.Note,
                    ForeColor = Color.FromArgb(110, 120, 135),
                    Font = new Font(Font.FontFamily, 8.5f)
                };
                _content.Controls.Add(note);
            }

            _content.ResumeLayout();
        }

        private int AddSection(Control parent, int y, string title, HealthLevel level, string rightText)
        {
            var bar = new Panel
            {
                Left = 2,
                Top = y,
                Width = Math.Max(380, parent.ClientSize.Width - 28),
                Height = 36,
                BackColor = Color.Transparent
            };
            var left = new Label
            {
                AutoSize = false,
                Left = 2,
                Top = 6,
                Width = 160,
                Height = 24,
                Text = title,
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59)
            };
            var badge = new StatusBadge
            {
                Left = bar.Width - 168,
                Top = 4,
                Width = 160,
                Height = 28,
                Level = level,
                Text = rightText
            };
            bar.Controls.Add(left);
            bar.Controls.Add(badge);
            parent.Controls.Add(bar);
            return y + 40;
        }

        private int AddCard(Control parent, int y, StatusItem item)
        {
            int width = Math.Max(380, parent.ClientSize.Width - 28);
            var card = new Panel
            {
                Left = 2,
                Top = y,
                Width = width,
                Height = 72,
                BackColor = Color.White
            };
            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 8);
                using var brush = new SolidBrush(Color.White);
                using var pen = new Pen(Color.FromArgb(226, 232, 240));
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
                using var accent = new SolidBrush(LevelColor(item.Level));
                g.FillRectangle(accent, 0, 10, 4, card.Height - 20);
            };

            var title = new Label
            {
                Left = 16,
                Top = 10,
                Width = width - 140,
                Height = 22,
                Text = item.Title,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                BackColor = Color.White
            };
            var summary = new Label
            {
                Left = width - 118,
                Top = 12,
                Width = 100,
                Height = 22,
                Text = item.Summary,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = LevelColor(item.Level),
                BackColor = Color.White
            };
            var detail = new Label
            {
                Left = 16,
                Top = 36,
                Width = width - 28,
                Height = 28,
                Text = item.Detail,
                ForeColor = Color.FromArgb(100, 116, 139),
                BackColor = Color.White
            };

            card.Controls.Add(title);
            card.Controls.Add(summary);
            card.Controls.Add(detail);
            parent.Controls.Add(card);
            return y + 80;
        }

        private static Color LevelColor(HealthLevel level) => level switch
        {
            HealthLevel.Good => Color.FromArgb(22, 163, 74),
            HealthLevel.Warning => Color.FromArgb(217, 119, 6),
            HealthLevel.Bad => Color.FromArgb(220, 38, 38),
            _ => Color.FromArgb(100, 116, 139)
        };

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class StatusBadge : Label
        {
            public HealthLevel Level { get; set; }

            public StatusBadge()
            {
                TextAlign = ContentAlignment.MiddleCenter;
                ForeColor = Color.White;
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 10);
                using var brush = new SolidBrush(LevelColor(Level));
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
