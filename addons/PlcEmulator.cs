// PlcEmulator.exe: stands in for the Siemens S7-1200 while testing. It is a Modbus TCP client of
// PeralnaAgent.exe exactly like the real PLC (same registers, same FB_Peralna logic), and shows the
// touch panel, the three push buttons, the outputs and the digital seconds display.
using System;
using System.Drawing;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace PeralnaAddons
{
    // Minimal Modbus TCP client: read holding registers (FC3).
    class ModbusClient
    {
        TcpClient tcp;
        NetworkStream stream;
        ushort tid;

        public ushort[] ReadHolding(string host, int port, int start, int count)
        {
            try
            {
                if (tcp == null)
                {
                    tcp = new TcpClient();
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1000) || !tcp.Connected) throw new Exception("connect timeout");
                    tcp.EndConnect(ar);
                    stream = tcp.GetStream();
                    stream.ReadTimeout = 1500;
                }
                tid++;
                byte[] req = { (byte)(tid >> 8), (byte)tid, 0, 0, 0, 6, 1, 3, (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count };
                stream.Write(req, 0, req.Length);
                byte[] head = Read(9);
                if ((head[7] & 0x80) != 0) throw new Exception("Modbus exception " + head[8]);
                byte[] data = Read(head[8]);
                ushort[] regs = new ushort[count];
                for (int i = 0; i < count; i++) regs[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                return regs;
            }
            catch (Exception)
            {
                Close();
                return null;
            }
        }

        // FC6: the PLC reports its program in register 10.
        public bool WriteSingle(string host, int port, int address, int value)
        {
            try
            {
                if (tcp == null && ReadHolding(host, port, 0, 1) == null) return false;
                tid++;
                byte[] req = { (byte)(tid >> 8), (byte)tid, 0, 0, 0, 6, 1, 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value };
                stream.Write(req, 0, req.Length);
                byte[] resp = Read(12);
                return (resp[7] & 0x80) == 0;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        byte[] Read(int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = stream.Read(b, got, n - got);
                if (r <= 0) throw new Exception("closed");
                got += r;
            }
            return b;
        }

        public void Close()
        {
            try { if (tcp != null) tcp.Close(); } catch { }
            tcp = null;
            stream = null;
        }
    }

    class PlcEmulatorForm : Form
    {
        // Same register map as agent/PeralnaAgent.cs and plc/FB_Peralna.scl
        const int RegCount = 53, NameLen = 20, NameStart = 21;

        readonly TextBox hostBox = new TextBox();
        readonly TextBox portBox = new TextBox();
        readonly Lamp linkLamp = new Lamp();
        readonly Label linkText = new Label();

        // touch panel
        readonly Panel hmi = new Panel();
        readonly Lamp hmiLamp = new Lamp();
        readonly Label hmiName = new Label();
        readonly Label hmiClock = new Label();
        readonly Label hmiInfo = new Label();
        readonly Label hmiStatus = new Label();
        readonly Button hmiFoam = new Button();
        readonly Button hmiWater = new Button();
        readonly Button hmiStop = new Button();

        // outputs + display
        readonly Lamp qFoam = new Lamp();
        readonly Lamp qWater = new Lamp();
        readonly Lamp qEnable = new Lamp();
        readonly Label display = new Label();
        readonly Label displayWord = new Label();
        readonly Label watchdog = new Label();

        readonly ModbusClient modbus = new ModbusClient();
        readonly System.Windows.Forms.Timer ui = new System.Windows.Forms.Timer();
        volatile ushort[] regs;
        volatile string host = "127.0.0.1";
        volatile int port = 502;
        int lastHeartbeat = -1;
        DateTime lastHeartbeatChange = DateTime.MinValue;
        volatile int program;   // 0 none, 1 foam, 2 water; written to register 10 for billing

        public PlcEmulatorForm()
        {
            Text = Brand.Name + " — PLC емулатор (S7-1200)";
            Brand.ApplyIcon(this);
            Font = new Font("Segoe UI", 10f);
            BackColor = Brand.Panel;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 470);

            port = int.Parse(AgentIni.Get("modbus_port", "502"));
            BuildHmi();
            BuildSide();

            Thread poll = new Thread(PollLoop);
            poll.IsBackground = true;
            poll.Start();
            ui.Interval = 200;
            ui.Tick += delegate { Scan(); };
            ui.Start();
        }

        // ─── Layout ───────────────────────────────────────────────

        void BuildHmi()
        {
            Label caption = new Label();
            caption.Text = "Екран на допир (HMI)";
            caption.ForeColor = Brand.Muted;
            caption.SetBounds(20, 12, 300, 22);
            Controls.Add(caption);

            hmi.SetBounds(20, 36, 520, 410);
            hmi.BackColor = Color.FromArgb(0x10, 0x18, 0x26);
            Controls.Add(hmi);

            Label brand = new Label();
            brand.Text = Brand.Name;
            brand.ForeColor = Color.FromArgb(0x9F, 0xB3, 0xC8);
            brand.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            brand.SetBounds(18, 12, 300, 22);
            hmiLamp.SetBounds(492, 14, 18, 18);

            hmiName.ForeColor = Color.White;
            hmiName.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            hmiName.SetBounds(18, 44, 490, 44);
            hmiName.AutoEllipsis = true;

            hmiClock.ForeColor = Color.FromArgb(0x4F, 0xC3, 0xF7);
            hmiClock.Font = new Font("Consolas", 54f, FontStyle.Bold);
            hmiClock.SetBounds(14, 92, 494, 92);

            hmiInfo.ForeColor = Color.FromArgb(0xC9, 0xD3, 0xE3);
            hmiInfo.Font = new Font("Segoe UI", 12f);
            hmiInfo.SetBounds(18, 186, 490, 28);

            hmiStatus.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            hmiStatus.SetBounds(18, 218, 490, 30);

            HmiButton(hmiFoam, "ПЕНА", Color.FromArgb(0xF9, 0xA8, 0x25), 18);
            HmiButton(hmiWater, "ВОДА", Color.FromArgb(0x1E, 0x88, 0xE5), 184);
            HmiButton(hmiStop, "СТОП", Color.FromArgb(0xC6, 0x28, 0x28), 350);
            hmiFoam.Click += delegate { Press(1); };
            hmiWater.Click += delegate { Press(2); };
            hmiStop.Click += delegate { Press(0); };

            hmi.Controls.AddRange(new Control[] { brand, hmiLamp, hmiName, hmiClock, hmiInfo, hmiStatus, hmiFoam, hmiWater, hmiStop });
        }

        void HmiButton(Button b, string text, Color color, int x)
        {
            b.Text = text;
            b.Tag = color;
            b.SetBounds(x, 280, 152, 110);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = color;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
        }

        void BuildSide()
        {
            int x = 560, w = 280;

            GroupBox link = Group("Врска со агентот (Modbus TCP)", x, 30, w, 104);
            Label hl = new Label(); hl.Text = "IP"; hl.SetBounds(12, 30, 30, 24);
            hostBox.Text = host; hostBox.SetBounds(40, 27, 130, 26);
            Label pl = new Label(); pl.Text = "Порта"; pl.SetBounds(178, 30, 50, 24);
            portBox.Text = port.ToString(); portBox.SetBounds(228, 27, 40, 26);
            hostBox.TextChanged += delegate { host = hostBox.Text.Trim(); modbus.Close(); };
            portBox.TextChanged += delegate { int p; if (int.TryParse(portBox.Text, out p)) { port = p; modbus.Close(); } };
            linkLamp.SetBounds(12, 68, 18, 18);
            linkText.SetBounds(36, 66, 236, 24);
            link.Controls.AddRange(new Control[] { hl, hostBox, pl, portBox, linkLamp, linkText });

            GroupBox inputs = Group("Копчиња на боксот (влезови)", x, 142, w, 72);
            Button iFoam = SmallButton("I0.0 ПЕНА", 12), iWater = SmallButton("I0.1 ВОДА", 100), iStop = SmallButton("I0.2 СТОП", 188);
            iFoam.Click += delegate { Press(1); };
            iWater.Click += delegate { Press(2); };
            iStop.Click += delegate { Press(0); };
            inputs.Controls.AddRange(new Control[] { iFoam, iWater, iStop });

            GroupBox outputs = Group("Излези", x, 222, w, 104);
            AddLamp(outputs, qFoam, "Q0.0  Пумпа ПЕНА", 26, Color.FromArgb(0xF9, 0xA8, 0x25));
            AddLamp(outputs, qWater, "Q0.1  Пумпа ВОДА", 50, Color.FromArgb(0x1E, 0x88, 0xE5));
            AddLamp(outputs, qEnable, "Картичка ОК (регистар 0)", 74, Brand.Good);

            GroupBox sec = Group("Дигитален секундарник", x, 334, w, 112);
            display.SetBounds(12, 24, 256, 52);
            display.BackColor = Color.Black;
            display.ForeColor = Color.FromArgb(0xFF, 0x3D, 0x00);
            display.Font = new Font("Consolas", 30f, FontStyle.Bold);
            display.TextAlign = ContentAlignment.MiddleCenter;
            displayWord.SetBounds(12, 80, 256, 22);
            displayWord.ForeColor = Brand.Muted;
            sec.Controls.AddRange(new Control[] { display, displayWord });

            watchdog.SetBounds(20, 448, 520, 20);
            watchdog.Font = new Font("Segoe UI", 9f);
            Controls.Add(watchdog);
        }

        GroupBox Group(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.SetBounds(x, y, w, h);
            Controls.Add(g);
            return g;
        }

        Button SmallButton(string text, int x)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, 28, 82, 32);
            b.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            return b;
        }

        void AddLamp(GroupBox g, Lamp lamp, string text, int y, Color color)
        {
            lamp.OnColor = color;
            lamp.SetBounds(12, y, 18, 18);
            Label l = new Label();
            l.Text = text;
            l.SetBounds(36, y - 1, 230, 22);
            g.Controls.Add(lamp);
            g.Controls.Add(l);
        }

        // ─── PLC cycle (same logic as FB_Peralna) ─────────────────

        void PollLoop()
        {
            while (true)
            {
                regs = modbus.ReadHolding(host, port, 0, RegCount);
                if (regs != null) modbus.WriteSingle(host, port, 10, program);
                Thread.Sleep(250);
            }
        }

        bool MayRun(ushort[] r, bool commOk) { return commOk && r != null && r[0] == 1; }

        void Press(int p)
        {
            ushort[] r = regs;
            if (p == 0 || !MayRun(r, CommOk())) program = 0;
            else program = p;
            Scan();
        }

        bool CommOk() { return (DateTime.Now - lastHeartbeatChange).TotalSeconds < 5; }

        void Scan()
        {
            ushort[] r = regs;
            if (r != null && r[5] != lastHeartbeat)
            {
                lastHeartbeat = r[5];
                lastHeartbeatChange = DateTime.Now;
            }
            bool connected = r != null;
            bool commOk = connected && CommOk();
            bool enabled = MayRun(r, commOk);
            if (!enabled) program = 0;

            linkLamp.On = commOk;
            linkLamp.OnColor = Brand.Good;
            linkText.Text = commOk ? "Поврзан · heartbeat " + r[5] : (connected ? "Нема heartbeat" : "Нема врска со " + host + ":" + port);
            linkText.ForeColor = commOk ? Brand.Good : Brand.Bad;
            hmiLamp.On = commOk;

            int status = r == null ? -1 : r[4];
            int secondsLeft = r == null ? 0 : r[1];
            string name = "";
            if (r != null) for (int i = 0; i < Math.Min((int)r[NameLen], 32); i++) name += (char)r[NameStart + i];

            hmiName.Text = name.Length > 0 ? name : (commOk ? "Стави картичка" : "—");
            hmiClock.Text = enabled ? Brand.Clock(secondsLeft) : "--:--";
            hmiInfo.Text = r != null && (enabled || name.Length > 0)
                ? "Салдо: " + r[2] + " ден.   ·   Наплатено: " + r[3] + " ден.   ·   " + secondsLeft + " s"
                : "";
            string statusText = !commOk ? "Нема врска со компјутерот — излезите се исклучени"
                : enabled ? (program == 1 ? "ПЕНА тече · се наплаќа" : program == 2 ? "ВОДА тече · се наплаќа" : "СТОП · не се наплаќа · избери ПЕНА или ВОДА")
                : Brand.StatusText(status);
            hmiStatus.Text = statusText;
            hmiStatus.ForeColor = !commOk || (status >= 2 && !enabled) ? Color.FromArgb(0xFF, 0x8A, 0x80) : Color.FromArgb(0xA5, 0xD6, 0xA7);

            foreach (Button b in new[] { hmiFoam, hmiWater, hmiStop })
            {
                Color c = (Color)b.Tag;
                bool active = (b == hmiFoam && program == 1) || (b == hmiWater && program == 2);
                b.BackColor = enabled ? (active ? ControlPaint.Light(c, 0.4f) : c) : Color.FromArgb(0x3A, 0x44, 0x55);
                b.FlatAppearance.BorderSize = active ? 4 : 0;
                b.FlatAppearance.BorderColor = Color.White;
            }

            qFoam.On = enabled && program == 1;
            qWater.On = enabled && program == 2;
            qEnable.On = enabled;

            int shown = enabled ? Math.Min(9999, secondsLeft) : 0;
            display.Text = enabled ? shown.ToString("0000") : "----";
            int bcd = ((shown / 1000) << 12) | (((shown / 100) % 10) << 8) | (((shown / 10) % 10) << 4) | (shown % 10);
            displayWord.Text = "QW2 (BCD) = 16#" + bcd.ToString("X4");
            watchdog.Text = "Watchdog: " + (commOk ? "OK" : "ИСКЛУЧЕНО — нема heartbeat 5 s") + "   ·   логика иста како plc\\FB_Peralna.scl";
            watchdog.ForeColor = commOk ? Brand.Muted : Brand.Bad;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            modbus.Close();
            base.OnFormClosing(e);
        }
    }

    static class PlcEmulatorProgram
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new PlcEmulatorForm());
        }
    }
}
