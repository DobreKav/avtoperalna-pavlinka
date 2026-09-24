// Shared by the add-ons (CardEmulator.exe, PlcEmulator.exe). C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PeralnaAddons
{
    static class Brand
    {
        public const string Name = "Автоперална Павлинка";
        public static readonly Color Blue = Color.FromArgb(0x1F, 0x6F, 0xEB);
        public static readonly Color Ink = Color.FromArgb(0x1B, 0x23, 0x30);
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x76, 0x86);
        public static readonly Color Good = Color.FromArgb(0x22, 0xA4, 0x5D);
        public static readonly Color Bad = Color.FromArgb(0xC6, 0x28, 0x28);
        public static readonly Color Panel = Color.FromArgb(0xF4, 0xF6, 0xFA);

        // Register 4 of the agent.
        public static string StatusText(int status)
        {
            switch (status)
            {
                case 0: return "Стави картичка";
                case 1: return "Работи";
                case 2: return "Парите се потрошени";
                case 3: return "Нема доволно пари на картичката";
                case 4: return "Непозната картичка";
                case 5: return "Блокирана картичка";
                case 6: return "Грешка на серверот";
                case 7: return "Нема читач на картички";
                case 8: return "Картичката не може да се прочита";
                default: return "Статус " + status;
            }
        }

        public static string Clock(int seconds)
        {
            seconds = Math.Max(0, seconds);
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        public static void ApplyIcon(Form f)
        {
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Reads a key from agent.ini next to the add-on (the install folder).
    static class AgentIni
    {
        public static string Get(string key, string fallback)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.ini");
            if (!File.Exists(path)) return fallback;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq < 0) continue;
                if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    string v = line.Substring(eq + 1).Trim();
                    return v.Length > 0 ? v : fallback;
                }
            }
            return fallback;
        }
    }

    // A round status lamp.
    class Lamp : Control
    {
        bool on;
        public Color OnColor = Brand.Good;
        public Color OffColor = Color.FromArgb(0xD5, 0xDB, 0xE4);

        public Lamp()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(18, 18);
        }

        public bool On
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = on ? OnColor : OffColor;
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(on ? 70 : 0, c)))
                e.Graphics.FillEllipse(halo, 0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(c))
                e.Graphics.FillEllipse(b, Width * 0.18f, Height * 0.18f, Width * 0.64f, Height * 0.64f);
        }
    }
}
