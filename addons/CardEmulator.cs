// CardEmulator.exe: a virtual card reader for testing without hardware.
// Connects to PeralnaAgent.exe on 127.0.0.1:<virtual_reader_port> (agent.ini, default 5021).
// While it runs, the admin panel shows the card reader as connected.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PeralnaAddons
{
    class CardEmulatorForm : Form
    {
        readonly int port = int.Parse(AgentIni.Get("virtual_reader_port", "5021"));
        readonly Lamp linkLamp = new Lamp();
        readonly Label linkText = new Label();
        readonly ComboBox uidBox = new ComboBox();
        readonly Button insert = new Button();
        readonly CardView card = new CardView();
        readonly Label status = new Label();
        readonly Label balance = new Label();
        readonly Label seconds = new Label();
        readonly Label charged = new Label();
        readonly Lamp machineLamp = new Lamp();
        readonly Label machineText = new Label();
        readonly System.Windows.Forms.Timer reconnect = new System.Windows.Forms.Timer();

        TcpClient client;
        StreamWriter writer;
        bool inserted;

        public CardEmulatorForm()
        {
            Text = Brand.Name + " — емулатор на читач";
            Brand.ApplyIcon(this);
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 520);

            linkLamp.SetBounds(20, 18, 18, 18);
            linkText.SetBounds(44, 16, 380, 24);

            card.SetBounds(20, 52, 400, 190);

            Label uidLabel = new Label();
            uidLabel.Text = "Број на картичката (UID):";
            uidLabel.ForeColor = Brand.Muted;
            uidLabel.SetBounds(20, 254, 400, 22);
            uidBox.SetBounds(20, 278, 400, 30);
            // DEMO0001 exists in every installation, refills itself and is left out of the totals.
            uidBox.Items.AddRange(new object[] { "DEMO0001", "TEST0001", "TEST0002" });
            uidBox.Text = "DEMO0001";
            uidBox.TextChanged += delegate { if (!inserted) { card.Uid = Uid(); card.Invalidate(); } };

            insert.SetBounds(20, 318, 400, 50);
            insert.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            insert.FlatStyle = FlatStyle.Flat;
            insert.FlatAppearance.BorderSize = 0;
            insert.ForeColor = Color.White;
            insert.Cursor = Cursors.Hand;
            insert.Click += delegate { Toggle(); };

            status.SetBounds(20, 380, 400, 26);
            status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            balance.SetBounds(20, 410, 200, 24);
            seconds.SetBounds(220, 410, 200, 24);
            charged.SetBounds(20, 436, 200, 24);
            machineLamp.SetBounds(220, 439, 18, 18);
            machineText.SetBounds(242, 436, 180, 24);

            Label hint = new Label();
            hint.Text = "DEMO0001 е демо картичка: секогаш има пари. Друга картичка: стави ја, па во админ панелот кликни „Регистрирај ја“.";
            hint.ForeColor = Brand.Muted;
            hint.Font = new Font("Segoe UI", 9f);
            hint.SetBounds(20, 470, 400, 40);

            Controls.AddRange(new Control[] { linkLamp, linkText, card, uidLabel, uidBox, insert, status, balance, seconds, charged, machineLamp, machineText, hint });

            card.Uid = Uid();
            ShowState(0, 0, 0, 0, 0, "");
            UpdateButton();
            reconnect.Interval = 2000;
            reconnect.Tick += delegate { if (client == null) Connect(); };
            reconnect.Start();
            Connect();
        }

        string Uid()
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in uidBox.Text.ToUpperInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
            return b.ToString();
        }

        void Connect()
        {
            try
            {
                TcpClient c = new TcpClient();
                c.Connect("127.0.0.1", port);
                client = c;
                writer = new StreamWriter(c.GetStream(), new UTF8Encoding(false));
                writer.AutoFlush = true;
                writer.WriteLine("HELLO Емулатор на читач");
                if (inserted) writer.WriteLine("IN " + card.Uid);
                Thread t = new Thread(delegate () { ReadLoop(c); });
                t.IsBackground = true;
                t.Start();
                SetLink(true);
            }
            catch (Exception)
            {
                client = null;
                SetLink(false);
            }
        }

        void ReadLoop(TcpClient c)
        {
            try
            {
                using (StreamReader r = new StreamReader(c.GetStream(), new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (!line.StartsWith("STATE ")) continue;
                        string[] p = line.Substring(6).Split(new[] { '|' }, 6);
                        if (p.Length < 6) continue;
                        int st = int.Parse(p[0]), en = int.Parse(p[1]), bal = int.Parse(p[2]), sec = int.Parse(p[3]), ch = int.Parse(p[4]);
                        string name = p[5];
                        BeginInvoke((MethodInvoker)delegate { ShowState(st, en, bal, sec, ch, name); });
                    }
                }
            }
            catch (Exception) { }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (client == c) { client = null; writer = null; SetLink(false); }
                });
            }
            catch (InvalidOperationException) { } // form closed
        }

        void SetLink(bool ok)
        {
            linkLamp.On = ok;
            linkLamp.OnColor = Brand.Good;
            linkText.Text = ok ? "Поврзан со агентот · читачот е вклучен" : "Агентот не работи (порта " + port + ") — се обидувам повторно…";
            linkText.ForeColor = ok ? Brand.Good : Brand.Bad;
            insert.Enabled = ok;
            UpdateButton();
        }

        void Toggle()
        {
            if (writer == null) return;
            if (!inserted)
            {
                if (Uid().Length < 4) { MessageBox.Show(this, "Внеси број на картичка (најмалку 4 знаци).", Text); return; }
                card.Uid = Uid();
                writer.WriteLine("IN " + card.Uid);
                inserted = true;
            }
            else
            {
                writer.WriteLine("OUT");
                inserted = false;
            }
            uidBox.Enabled = !inserted;
            card.Inserted = inserted;
            card.Invalidate();
            UpdateButton();
        }

        void UpdateButton()
        {
            insert.Text = inserted ? "Извади картичка" : "Стави картичка во читачот";
            insert.BackColor = !insert.Enabled ? Color.Silver : inserted ? Brand.Bad : Brand.Blue;
        }

        void ShowState(int st, int en, int bal, int sec, int ch, string name)
        {
            status.Text = inserted || st != 0 ? Brand.StatusText(st) : "Картичката не е во читачот";
            status.ForeColor = st == 1 ? Brand.Good : (st >= 2 ? Brand.Bad : Brand.Ink);
            balance.Text = "Салдо: " + (inserted ? bal + " ден." : "—");
            seconds.Text = "Преостанато: " + (en == 1 ? Brand.Clock(sec) : "—");
            charged.Text = "Наплатено: " + (inserted ? ch + " ден." : "—");
            machineLamp.On = en == 1;
            machineText.Text = en == 1 ? "Боксот РАБОТИ" : "Боксот стои";
            if (card.Holder != name) { card.Holder = name; card.Invalidate(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (writer != null && inserted) writer.WriteLine("OUT"); } catch { }
            try { if (client != null) client.Close(); } catch { }
            base.OnFormClosing(e);
        }
    }

    // The card drawn in the window.
    class CardView : Control
    {
        public string Uid = "";
        public string Holder = "";
        public bool Inserted;

        public CardView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.White);
            RectangleF r = new RectangleF(Inserted ? 30 : 10, 8, Width - 40, Height - 16);
            using (GraphicsPath shadow = Brand.RoundRect(new RectangleF(r.X + 3, r.Y + 5, r.Width, r.Height), 16))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(40, 0, 0, 40)))
                g.FillPath(sb, shadow);
            using (GraphicsPath p = Brand.RoundRect(r, 16))
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(0x29, 0xB6, 0xF6), Color.FromArgb(0x0D, 0x47, 0xA1), 35f))
                g.FillPath(b, p);
            using (SolidBrush chip = new SolidBrush(Color.FromArgb(0xFF, 0xD5, 0x4F)))
            using (GraphicsPath c = Brand.RoundRect(new RectangleF(r.X + 24, r.Y + 58, 46, 34), 6))
                g.FillPath(chip, c);
            using (Font title = new Font("Segoe UI", 13f, FontStyle.Bold))
                g.DrawString(Brand.Name, title, Brushes.White, r.X + 22, r.Y + 16);
            using (Font mono = new Font("Consolas", 15f, FontStyle.Bold))
                g.DrawString(Uid.Length > 0 ? Uid : "—", mono, Brushes.White, r.X + 22, r.Y + 104);
            using (Font small = new Font("Segoe UI", 10f))
                g.DrawString(Holder.Length > 0 ? Holder : (Uid == "DEMO0001" ? "Демо картичка" : (Inserted ? "" : "картичка за тест")), small, Brushes.White, r.X + 22, r.Y + 140);
            if (Inserted)
            {
                using (Font tag = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(0x22, 0xA4, 0x5D)))
                {
                    RectangleF tr = new RectangleF(r.Right - 132, r.Bottom - 40, 112, 24);
                    using (GraphicsPath tp = Brand.RoundRect(tr, 12)) g.FillPath(tb, tp);
                    g.DrawString("ВО ЧИТАЧОТ", tag, Brushes.White, tr.X + 16, tr.Y + 3);
                }
            }
        }
    }

    static class CardEmulatorProgram
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new CardEmulatorForm());
        }
    }
}
