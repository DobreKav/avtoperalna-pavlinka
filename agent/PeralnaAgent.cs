// Peralna card agent: PC/SC card reader -> billing server (api.php) -> PLC over Modbus TCP.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x); build.bat compiles it.
//
// Two ways to talk to the S7-1200, chosen on the admin panel page "PLC врска" (plc.php):
//   server  this program listens, the PLC connects with MB_CLIENT  (plc\FB_Peralna.scl)
//   client  this program connects to the PLC, which runs MB_SERVER  (plc\FB_Peralna_Server.scl)
// IP addresses and ports are typed on that page; the agent applies them within seconds.
//
// Holding registers (the same map in both modes, 0-based):
//   0  enable          1 = machine may run, 0 = stop       (also coil 0, FC1)
//   1  seconds left    on the current balance (max 65535)
//   2  balance         denars on the card (max 65535)
//   3  charged         denars charged in this session
//   4  status          see Status below
//   5  heartbeat       +1 every second; if it stops changing, the PLC must stop the machine
//   6  minutes left
//   10 program         written by the PLC (FC6/FC16): 0 stop, 1 foam, 2 water. The card is billed
//                      only while this is 1 or 2, so СТОП on the bay stops the money running out.
//   11-19              free for the PLC to write
//   20 name length     card holder name for the touch panel, in characters (max 32)
//   21-52 name         one UTF-16 character per register (Cyrillic works; WString on the PLC)
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Peralna
{
    enum Status
    {
        Idle = 0,          // no card in the reader
        Running = 1,
        Stopped = 2,       // card still inside, but the session ended (balance used up)
        LowBalance = 3,
        UnknownCard = 4,
        Blocked = 5,
        ServerError = 6,
        NoReader = 7,
        ReadError = 8,
    }

    class Config
    {
        public string Machine = "B1";
        public string Server = "http://127.0.0.1:8080/api.php";
        public string ApiKey = "";
        public int ModbusPort = 502;
        public string Reader = "";
        public int TickSeconds = 15;
        public int OfflineStopSeconds = 45;
        public bool Simulate = false;
        public int WebPort = 8080;          // 0 = do not start the built-in web server (e.g. under XAMPP)
        public string WebBind = "127.0.0.1";
        public int VirtualReaderPort = 5021; // CardEmulator.exe add-on (only in test mode); 0 = off
        // PLC link used until the admin panel answers (normally within 2 s). After that the
        // settings from the "PLC врска" page win.
        public PlcSettings Plc = new PlcSettings();

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || !line.Contains("=")) continue;
                int eq = line.IndexOf('=');
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "machine": c.Machine = val; break;
                    case "server": c.Server = val; break;
                    case "api_key": c.ApiKey = val; break;
                    case "modbus_port": c.ModbusPort = int.Parse(val); c.Plc.ListenPort = c.ModbusPort; break;
                    case "plc_mode": c.Plc.Mode = val.ToLowerInvariant() == "client" ? "client" : "server"; break;
                    case "plc_listen_ip": c.Plc.ListenIp = val; break;
                    case "plc_allowed_ip": c.Plc.AllowedIp = val; break;
                    case "plc_ip": c.Plc.PlcIp = val; break;
                    case "plc_port": c.Plc.PlcPort = int.Parse(val); break;
                    case "reader": c.Reader = val; break;
                    case "tick_seconds": c.TickSeconds = Math.Max(5, int.Parse(val)); break;
                    case "offline_stop_seconds": c.OfflineStopSeconds = Math.Max(10, int.Parse(val)); break;
                    case "web_port": c.WebPort = int.Parse(val); break;
                    case "web_bind": c.WebBind = val; break;
                    case "virtual_reader_port": c.VirtualReaderPort = int.Parse(val); break;
                    case "simulate": c.Simulate = val == "1" || val.ToLowerInvariant() == "true"; break;
                }
            }
            return c;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath;

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            lock (Gate)
            {
                Console.WriteLine(line);
                try { if (FilePath != null) File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            }
        }
    }

    // ─── PLC link settings ───────────────────────────────────────

    class PlcSettings
    {
        public string Mode = "server";       // "server" or "client"
        public string ListenIp = "0.0.0.0";  // server mode: local address to listen on
        public int ListenPort = 502;
        public string AllowedIp = "";        // server mode: only this PLC may connect ("" = any)
        public string PlcIp = "192.168.1.20";// client mode: the PLC's address
        public int PlcPort = 502;
        public int UnitId = 1;
        public int PollMs = 200;
        public bool TestMode = false;        // emulators and the demo card allowed

        public string Key()
        {
            return Mode + "|" + ListenIp + "|" + ListenPort + "|" + AllowedIp + "|" + PlcIp + "|" + PlcPort + "|" + UnitId + "|" + PollMs + "|" + TestMode;
        }

        public string LinkKey()
        {
            // Test mode only matters to the server's loopback rule, not to the client.
            return Mode == "client" ? Mode + "|" + PlcIp + "|" + PlcPort + "|" + UnitId + "|" + PollMs : Key();
        }

        public string Describe()
        {
            return Mode == "client"
                ? "Лаптоп → PLC " + PlcIp + ":" + PlcPort
                : "Лаптопот слуша на " + ListenIp + ":" + ListenPort + (AllowedIp.Length > 0 ? " (само " + AllowedIp + ")" : "");
        }

        public PlcSettings Clone() { return (PlcSettings)MemberwiseClone(); }

        static int Num(Dictionary<string, object> d, string key, int fallback, int min, int max)
        {
            object v;
            int n;
            if (d.TryGetValue(key, out v) && v != null && int.TryParse(v.ToString(), out n) && n >= min && n <= max) return n;
            return fallback;
        }

        static string Ip(Dictionary<string, object> d, string key, string fallback, bool allowEmpty)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            string s = v.ToString().Trim();
            IPAddress ip;
            if (s.Length == 0) return allowEmpty ? "" : fallback;
            return IPAddress.TryParse(s, out ip) && ip.AddressFamily == AddressFamily.InterNetwork ? s : fallback;
        }

        // Settings from the admin panel (api.php status reply). Anything invalid keeps the old value.
        public static PlcSettings FromServer(Dictionary<string, object> d, PlcSettings old)
        {
            PlcSettings p = old.Clone();
            object v;
            if (d.TryGetValue("plc_mode", out v) && v != null) p.Mode = v.ToString() == "client" ? "client" : "server";
            p.ListenIp = Ip(d, "plc_listen_ip", p.ListenIp, false);
            p.ListenPort = Num(d, "plc_listen_port", p.ListenPort, 1, 65535);
            p.AllowedIp = Ip(d, "plc_allowed_ip", p.AllowedIp, true);
            p.PlcIp = Ip(d, "plc_ip", p.PlcIp, false);
            p.PlcPort = Num(d, "plc_port", p.PlcPort, 1, 65535);
            p.UnitId = Num(d, "plc_unit_id", p.UnitId, 0, 255);
            p.PollMs = Num(d, "plc_poll_ms", p.PollMs, 50, 2000);
            if (d.TryGetValue("test_mode", out v) && v != null) p.TestMode = v.ToString() == "1";
            return p;
        }
    }

    interface IPlcLink
    {
        void Start();
        void Stop();
        bool Connected { get; }   // a successful exchange within the last 5 s
        string Peer { get; }      // address of the PLC
        string Error { get; }     // why it is not connected, in Macedonian ("" = no problem)
    }

    // ─── Shared register table ───────────────────────────────────

    class Registers
    {
        readonly ushort[] regs = new ushort[100];
        readonly object gate = new object();

        public ushort Get(int i) { lock (gate) return regs[i]; }
        public void Set(int i, int value) { lock (gate) regs[i] = (ushort)Math.Max(0, Math.Min(65535, value)); }
        public void Bump(int i) { lock (gate) regs[i] = unchecked((ushort)(regs[i] + 1)); }

        public ushort[] Snapshot(int start, int count)
        {
            lock (gate)
            {
                ushort[] copy = new ushort[count];
                Array.Copy(regs, start, copy, 0, count);
                return copy;
            }
        }

        public bool WritableByPlc(int start, int count) { return start >= 10 && start + count <= 20; }
    }

    // ─── Modbus TCP framing shared by the server and the client ──

    static class Modbus
    {
        public static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public static string SocketReason(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionRefused: return "PLC-то не слуша на таа порта (провери ја портата и дали програмата е симната во PLC-то)";
                case SocketError.TimedOut: return "PLC-то не одговара (провери кабел, IP адреса и маска)";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable: return "лаптопот нема мрежа до таа адреса (провери IP на лаптопот и маската)";
                case SocketError.AddressAlreadyInUse: return "портата е зафатена од друга програма";
                case SocketError.AddressNotAvailable: return "лаптопот ја нема таа IP адреса";
                case SocketError.AccessDenied: return "Windows не дозволува таа порта (пробај 502 или стартувај како администратор)";
                case SocketError.ConnectionReset: return "PLC-то ја прекина врската";
                default: return ex.Message;
            }
        }
    }

    // ─── Modbus TCP server (the PLC is the client) ───────────────

    class ModbusServer : IPlcLink
    {
        long lastRequestTicks;
        volatile string peer = "";
        volatile string error = "";
        volatile bool stopped;
        TcpListener listener;
        readonly List<TcpClient> clients = new List<TcpClient>();

        readonly Registers regs;
        readonly PlcSettings cfg;

        public ModbusServer(Registers regs, PlcSettings cfg) { this.regs = regs; this.cfg = cfg; }

        public bool Connected
        {
            get
            {
                long ticks = Interlocked.Read(ref lastRequestTicks);
                return ticks > 0 && (DateTime.UtcNow - new DateTime(ticks)).TotalSeconds < 5;
            }
        }
        public string Peer { get { return peer; } }
        public string Error
        {
            get
            {
                if (error.Length > 0 || Connected) return error;
                return "Чекам PLC-то да се поврзе на " + cfg.ListenIp + ":" + cfg.ListenPort;
            }
        }

        public void Start()
        {
            Thread t = new Thread(Listen);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            stopped = true;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (clients) { foreach (TcpClient c in clients) { try { c.Close(); } catch { } } clients.Clear(); }
        }

        void Listen()
        {
            // Retry until the port can be opened (e.g. another program still holds it).
            while (!stopped)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Parse(cfg.ListenIp), cfg.ListenPort);
                    listener.Start();
                    error = "";
                    Log.Write("Modbus TCP server listening on " + cfg.ListenIp + ":" + cfg.ListenPort
                        + (cfg.AllowedIp.Length > 0 ? " (only " + cfg.AllowedIp + ")" : ""));
                    break;
                }
                catch (SocketException ex)
                {
                    error = "Не може да се отвори " + cfg.ListenIp + ":" + cfg.ListenPort + ": " + Modbus.SocketReason(ex);
                    Log.Write(error);
                    for (int i = 0; i < 50 && !stopped; i++) Thread.Sleep(100);
                }
            }
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { if (stopped) return; Thread.Sleep(500); continue; }
                string from = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                bool loopback = from == "127.0.0.1";
                if (cfg.AllowedIp.Length > 0 && from != cfg.AllowedIp && !(loopback && cfg.TestMode))
                {
                    error = "Одбиена врска од " + from + ": дозволено е само PLC " + cfg.AllowedIp;
                    Log.Write(error);
                    client.Close();
                    continue;
                }
                Log.Write("PLC connected from " + client.Client.RemoteEndPoint);
                lock (clients) clients.Add(client);
                Thread h = new Thread(delegate () { Serve(client, from); });
                h.IsBackground = true;
                h.Start();
            }
        }

        void Serve(TcpClient client, string from)
        {
            try
            {
                using (client)
                using (NetworkStream s = client.GetStream())
                {
                    byte[] header = new byte[7];
                    while (!stopped && Modbus.ReadExact(s, header, 7))
                    {
                        Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
                        peer = from;
                        error = "";
                        int length = (header[4] << 8) | header[5];
                        if (length < 2 || length > 260) return;
                        byte[] pdu = new byte[length - 1];
                        if (!Modbus.ReadExact(s, pdu, pdu.Length)) return;
                        byte[] reply = Handle(pdu);
                        byte[] frame = new byte[7 + reply.Length];
                        Array.Copy(header, frame, 4);            // transaction id + protocol id
                        frame[4] = (byte)((reply.Length + 1) >> 8);
                        frame[5] = (byte)((reply.Length + 1) & 0xFF);
                        frame[6] = header[6];                    // unit id
                        Array.Copy(reply, 0, frame, 7, reply.Length);
                        s.Write(frame, 0, frame.Length);
                    }
                }
            }
            catch (Exception) { }
            lock (clients) clients.Remove(client);
            if (!stopped) Log.Write("PLC disconnected");
        }

        static byte[] Fail(byte fc, byte code) { return new byte[] { (byte)(fc | 0x80), code }; }

        byte[] Handle(byte[] pdu)
        {
            byte fc = pdu[0];
            if (pdu.Length < 5) return Fail(fc, 3);
            int addr = (pdu[1] << 8) | pdu[2];
            int qty = (pdu[3] << 8) | pdu[4];
            switch (fc)
            {
                case 1: // read coils: coil 0 mirrors register 0 (enable)
                case 2:
                {
                    if (qty < 1 || qty > 2000 || addr + qty > 100) return Fail(fc, 2);
                    byte[] bits = new byte[(qty + 7) / 8];
                    for (int i = 0; i < qty; i++)
                        if (regs.Get(addr + i) != 0 && addr + i == 0) bits[i / 8] |= (byte)(1 << (i % 8));
                    byte[] r = new byte[2 + bits.Length];
                    r[0] = fc; r[1] = (byte)bits.Length;
                    Array.Copy(bits, 0, r, 2, bits.Length);
                    return r;
                }
                case 3:
                case 4:
                {
                    if (qty < 1 || qty > 125 || addr + qty > 100) return Fail(fc, 2);
                    ushort[] v = regs.Snapshot(addr, qty);
                    byte[] r = new byte[2 + qty * 2];
                    r[0] = fc; r[1] = (byte)(qty * 2);
                    for (int i = 0; i < qty; i++) { r[2 + i * 2] = (byte)(v[i] >> 8); r[3 + i * 2] = (byte)(v[i] & 0xFF); }
                    return r;
                }
                case 6:
                {
                    if (!regs.WritableByPlc(addr, 1)) return Fail(fc, 2);
                    regs.Set(addr, qty); // for FC6 the "qty" field holds the value
                    return (byte[])pdu.Clone();
                }
                case 16:
                {
                    if (pdu.Length < 6 + qty * 2 || !regs.WritableByPlc(addr, qty)) return Fail(fc, 2);
                    for (int i = 0; i < qty; i++) regs.Set(addr + i, (pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                    return new byte[] { fc, pdu[1], pdu[2], pdu[3], pdu[4] };
                }
                default:
                    return Fail(fc, 1);
            }
        }
    }

    // ─── Modbus TCP client (the PLC is the server, MB_SERVER) ────
    //
    // Every PollMs: write registers 0..9 and 20..52 to the PLC (FC16), then read 10..19 back
    // (FC3). Registers 10..19 belong to the PLC and are never written from here, so the
    // program the bay chose (register 10) cannot be overwritten.

    class ModbusClient : IPlcLink
    {
        long lastOkTicks;
        volatile string error = "";
        volatile bool stopped;
        TcpClient tcp;
        ushort transaction;

        readonly Registers regs;
        readonly PlcSettings cfg;

        public ModbusClient(Registers regs, PlcSettings cfg) { this.regs = regs; this.cfg = cfg; }

        public bool Connected
        {
            get
            {
                long ticks = Interlocked.Read(ref lastOkTicks);
                return ticks > 0 && (DateTime.UtcNow - new DateTime(ticks)).TotalSeconds < 5;
            }
        }
        public string Peer { get { return cfg.PlcIp + ":" + cfg.PlcPort; } }
        public string Error { get { return error; } }

        public void Start()
        {
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            stopped = true;
            Close();
        }

        void Close()
        {
            try { if (tcp != null) tcp.Close(); } catch { }
            tcp = null;
        }

        void Loop()
        {
            bool wasConnected = false;
            while (!stopped)
            {
                try
                {
                    tcp = new TcpClient();
                    IAsyncResult r = tcp.BeginConnect(IPAddress.Parse(cfg.PlcIp), cfg.PlcPort, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(3000)) throw new SocketException((int)SocketError.TimedOut);
                    tcp.EndConnect(r);   // throws the real reason (e.g. connection refused)
                    tcp.NoDelay = true;
                    NetworkStream s = tcp.GetStream();
                    s.ReadTimeout = 2000;
                    s.WriteTimeout = 2000;
                    Log.Write("Connected to PLC " + Peer);
                    while (!stopped)
                    {
                        Exchange(s);
                        Interlocked.Exchange(ref lastOkTicks, DateTime.UtcNow.Ticks);
                        if (!wasConnected) { wasConnected = true; error = ""; }
                        Thread.Sleep(cfg.PollMs);
                    }
                }
                catch (SocketException ex) { Fail("Нема врска со PLC " + Peer + ": " + Modbus.SocketReason(ex)); }
                catch (IOException ex)
                {
                    SocketException se = ex.InnerException as SocketException;
                    Fail("Врската со PLC " + Peer + " падна: " + (se != null ? Modbus.SocketReason(se) : ex.Message));
                }
                catch (Exception ex) { Fail("PLC " + Peer + ": " + ex.Message); }
                if (wasConnected && !stopped) Log.Write("PLC disconnected");
                wasConnected = false;
                Close();
                for (int i = 0; i < 20 && !stopped; i++) Thread.Sleep(100);   // retry every 2 s
            }
        }

        void Fail(string message)
        {
            if (stopped) return;
            if (message != error) Log.Write(message);
            error = message;
        }

        void Exchange(NetworkStream s)
        {
            WriteBlock(s, 0, regs.Snapshot(0, 10));
            WriteBlock(s, 20, regs.Snapshot(20, 33));
            ushort[] back = ReadBlock(s, 10, 10);
            for (int i = 0; i < back.Length; i++) regs.Set(10 + i, back[i]);
        }

        byte[] Request(NetworkStream s, byte[] pdu)
        {
            transaction++;
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(transaction >> 8); frame[1] = (byte)transaction;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)((pdu.Length + 1) & 0xFF);
            frame[6] = (byte)cfg.UnitId;
            Array.Copy(pdu, 0, frame, 7, pdu.Length);
            s.Write(frame, 0, frame.Length);

            byte[] header = new byte[7];
            if (!Modbus.ReadExact(s, header, 7)) throw new IOException("PLC-то ја затвори врската");
            int length = (header[4] << 8) | header[5];
            if (length < 2 || length > 260) throw new IOException("неисправен Modbus одговор");
            byte[] reply = new byte[length - 1];
            if (!Modbus.ReadExact(s, reply, reply.Length)) throw new IOException("PLC-то ја затвори врската");
            if (header[0] != frame[0] || header[1] != frame[1]) throw new IOException("Modbus одговорот е за друго барање");
            if ((reply[0] & 0x80) != 0)
            {
                int code = reply.Length > 1 ? reply[1] : 0;
                throw new Exception(code == 2
                    ? "PLC-то ја одби адресата: MB_HOLD_REG во PLC-то мора да има најмалку 53 регистри (Array[0..52] of Word)"
                    : "PLC-то врати Modbus грешка " + code);
            }
            if (reply[0] != pdu[0]) throw new IOException("неочекуван Modbus одговор");
            return reply;
        }

        void WriteBlock(NetworkStream s, int addr, ushort[] values)
        {
            byte[] pdu = new byte[6 + values.Length * 2];
            pdu[0] = 16;
            pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = (byte)(values.Length >> 8); pdu[4] = (byte)values.Length;
            pdu[5] = (byte)(values.Length * 2);
            for (int i = 0; i < values.Length; i++) { pdu[6 + i * 2] = (byte)(values[i] >> 8); pdu[7 + i * 2] = (byte)values[i]; }
            Request(s, pdu);
        }

        ushort[] ReadBlock(NetworkStream s, int addr, int qty)
        {
            byte[] reply = Request(s, new byte[] { 3, (byte)(addr >> 8), (byte)addr, (byte)(qty >> 8), (byte)qty });
            if (reply.Length < 2 + qty * 2) throw new IOException("прекраток Modbus одговор");
            ushort[] v = new ushort[qty];
            for (int i = 0; i < qty; i++) v[i] = (ushort)((reply[2 + i * 2] << 8) | reply[3 + i * 2]);
            return v;
        }
    }

    // ─── Web server: PHP's built-in server, bundled in .\php ──────

    static class WebServer
    {
        static Process child;

        public static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect("127.0.0.1", port, null, null);
                    bool ok = r.AsyncWaitHandle.WaitOne(500) && c.Connected;
                    if (ok) c.EndConnect(r);
                    return ok;
                }
            }
            catch { return false; }
        }

        public static void Start(string dir, Config cfg)
        {
            string php = Path.Combine(dir, "php", "php.exe");
            if (!File.Exists(php)) { Log.Write("No php\\php.exe next to the agent: web server not started"); return; }
            Launch(dir, php, cfg);
            for (int i = 0; i < 20 && !PortOpen(cfg.WebPort); i++) Thread.Sleep(250);
            Log.Write(PortOpen(cfg.WebPort) ? "Admin panel: http://localhost:" + cfg.WebPort + "/" : "Web server did not start");

            Thread watch = new Thread(delegate ()
            {
                while (true)
                {
                    Thread.Sleep(5000);
                    if (!PortOpen(cfg.WebPort)) { Log.Write("Web server down, restarting"); Launch(dir, php, cfg); }
                }
            });
            watch.IsBackground = true;
            watch.Start();
            AppDomain.CurrentDomain.ProcessExit += delegate { try { if (child != null && !child.HasExited) child.Kill(); } catch { } };
        }

        static void Launch(string dir, string php, Config cfg)
        {
            if (PortOpen(cfg.WebPort)) return; // left over from a previous run: keep using it
            string sessions = Path.Combine(dir, "data", "sessions");
            Directory.CreateDirectory(sessions);
            ProcessStartInfo psi = new ProcessStartInfo(php,
                "-c \"" + Path.Combine(dir, "php", "php.ini") + "\""
                + " -d extension_dir=\"" + Path.Combine(dir, "php", "ext") + "\""
                + " -d session.save_path=\"" + sessions + "\""
                + " -d error_log=\"" + Path.Combine(dir, "data", "php-errors.log") + "\""
                + " -S " + cfg.WebBind + ":" + cfg.WebPort + " -t \"" + Path.Combine(dir, "www") + "\"");
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            child = Process.Start(psi);
            child.OutputDataReceived += delegate { };
            child.ErrorDataReceived += delegate { };  // one line per request: not worth logging
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }
    }

    // ─── Virtual card reader (CardEmulator.exe add-on) ───────────
    //
    // Line protocol on 127.0.0.1, UTF-8:
    //   emulator -> agent:  HELLO <name> | IN <uid> | OUT
    //   agent -> emulator:  STATE <status>|<enable>|<balance>|<seconds>|<charged>|<holder name>   (every second)

    class VirtualReader
    {
        readonly Queue<string> events = new Queue<string>();
        readonly object gate = new object();
        StreamWriter writer;
        public volatile bool Connected;
        public string Name = "";
        TcpListener listener;
        TcpClient current;
        public bool Running { get { return listener != null; } }

        // Only while test mode is on (admin panel → PLC врска).
        public void Start(int port)
        {
            if (listener != null) return;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Write("Virtual card reader port " + port + " not available: " + ex.Message);
                listener = null;
                return;
            }
            Log.Write("Test mode: virtual card reader on port " + port + " (CardEmulator.exe)");
            TcpListener own = listener;
            Thread t = new Thread(delegate ()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = own.AcceptTcpClient(); } catch { return; }   // stopped
                    Thread h = new Thread(delegate () { Serve(client); });
                    h.IsBackground = true;
                    h.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            if (listener == null) return;
            try { listener.Stop(); } catch { }
            listener = null;
            lock (gate) { try { if (current != null) current.Close(); } catch { } }
            Log.Write("Test mode off: virtual card reader closed");
        }

        void Serve(TcpClient client)
        {
            lock (gate) current = client;
            try
            {
                using (client)
                using (NetworkStream s = client.GetStream())
                using (StreamReader r = new StreamReader(s, new UTF8Encoding(false)))
                {
                    lock (gate) { writer = new StreamWriter(s, new UTF8Encoding(false)); writer.AutoFlush = true; }
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.StartsWith("HELLO"))
                        {
                            Name = line.Length > 6 ? line.Substring(6).Trim() : "emulator";
                            Connected = true;
                            Log.Write("Virtual card reader connected: " + Name);
                        }
                        else if (line.StartsWith("IN ") || line == "OUT")
                        {
                            lock (events) events.Enqueue(line);
                        }
                    }
                }
            }
            catch (Exception) { }
            lock (gate) writer = null;
            Connected = false;
            lock (events) events.Enqueue("OUT");   // emulator closed: its card is gone too
            Log.Write("Virtual card reader disconnected");
        }

        public string Next()
        {
            lock (events) return events.Count > 0 ? events.Dequeue() : null;
        }

        public void Send(string line)
        {
            lock (gate)
            {
                try { if (writer != null) writer.WriteLine(line); } catch { writer = null; }
            }
        }
    }

    // ─── PC/SC card reader ───────────────────────────────────────

    static class PcSc
    {
        public const uint SCOPE_USER = 0;
        public const uint SHARE_SHARED = 2;
        public const uint PROTOCOL_T0_T1 = 3;
        public const uint LEAVE_CARD = 0;
        public const uint STATE_UNAWARE = 0;
        public const uint STATE_CHANGED = 0x2;
        public const uint STATE_EMPTY = 0x10;
        public const uint STATE_PRESENT = 0x20;
        public const uint E_TIMEOUT = 0x8010000A;
        public const uint E_NO_READERS = 0x8010002E;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct READERSTATE
        {
            public string szReader;
            public IntPtr pvUserData;
            public uint dwCurrentState;
            public uint dwEventState;
            public uint cbAtr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] rgbAtr;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_REQUEST { public uint dwProtocol; public uint cbPciLength; }

        [DllImport("winscard.dll")] public static extern uint SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr ctx);
        [DllImport("winscard.dll")] public static extern uint SCardReleaseContext(IntPtr ctx);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW")]
        public static extern uint SCardListReaders(IntPtr ctx, string groups, char[] readers, ref uint len);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW")]
        public static extern uint SCardGetStatusChange(IntPtr ctx, uint timeout, [In, Out] READERSTATE[] states, uint count);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardConnectW")]
        public static extern uint SCardConnect(IntPtr ctx, string reader, uint share, uint protocols, out IntPtr card, out uint active);
        [DllImport("winscard.dll")] public static extern uint SCardDisconnect(IntPtr card, uint disposition);
        [DllImport("winscard.dll")]
        public static extern uint SCardTransmit(IntPtr card, ref IO_REQUEST send, byte[] sendBuf, uint sendLen, IntPtr recvPci, byte[] recvBuf, ref uint recvLen);

        public static List<string> ListReaders(IntPtr ctx)
        {
            List<string> result = new List<string>();
            uint len = 0;
            if (SCardListReaders(ctx, null, null, ref len) != 0 || len == 0) return result;
            char[] buf = new char[len];
            if (SCardListReaders(ctx, null, buf, ref len) != 0) return result;
            foreach (string name in new string(buf, 0, (int)len).Split('\0'))
                if (name.Length > 0) result.Add(name);
            return result;
        }

        // "Get Data" pseudo-APDU for the card UID (ACR122U, ACR1252 and most PC/SC NFC readers).
        public static string ReadUid(IntPtr ctx, string reader)
        {
            IntPtr card;
            uint protocol;
            uint rc = SCardConnect(ctx, reader, SHARE_SHARED, PROTOCOL_T0_T1, out card, out protocol);
            if (rc != 0) throw new Exception("SCardConnect 0x" + rc.ToString("X8"));
            try
            {
                IO_REQUEST pci = new IO_REQUEST { dwProtocol = protocol, cbPciLength = 8 };
                byte[] apdu = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };
                byte[] resp = new byte[258];
                uint respLen = (uint)resp.Length;
                rc = SCardTransmit(card, ref pci, apdu, (uint)apdu.Length, IntPtr.Zero, resp, ref respLen);
                if (rc != 0) throw new Exception("SCardTransmit 0x" + rc.ToString("X8"));
                if (respLen < 2 || resp[respLen - 2] != 0x90 || resp[respLen - 1] != 0x00)
                    throw new Exception("card refused the UID command");
                return BitConverter.ToString(resp, 0, (int)respLen - 2).Replace("-", "");
            }
            finally { SCardDisconnect(card, LEAVE_CARD); }
        }
    }

    // ─── Billing server client ───────────────────────────────────

    class Api
    {
        readonly Config cfg;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public Api(Config cfg) { this.cfg = cfg; }

        class TimedClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest r = base.GetWebRequest(address);
                r.Timeout = 5000;
                return r;
            }
        }

        // Returns null when the server cannot be reached.
        public Dictionary<string, object> Call(string action, NameValueCollection fields)
        {
            fields["action"] = action;
            try
            {
                using (TimedClient web = new TimedClient())
                {
                    if (cfg.ApiKey.Length > 0) web.Headers["X-Api-Key"] = cfg.ApiKey;
                    byte[] body;
                    try { body = web.UploadValues(cfg.Server, "POST", fields); }
                    catch (WebException ex)
                    {
                        HttpWebResponse resp = ex.Response as HttpWebResponse;
                        if (resp == null) throw;
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                            body = Encoding.UTF8.GetBytes(sr.ReadToEnd());
                    }
                    return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Server unreachable (" + action + "): " + ex.Message);
                return null;
            }
        }
    }

    // ─── Main loop ───────────────────────────────────────────────

    class Agent
    {
        readonly Config cfg;
        readonly Registers regs = new Registers();
        readonly Api api;

        IntPtr ctx = IntPtr.Zero;
        string reader;
        bool cardPresent;
        int sessionId;
        int secondsLeft;
        DateTime lastTick = DateTime.MinValue;
        DateTime lastServerOk = DateTime.Now;
        DateTime lastSecond = DateTime.Now;
        DateTime lastStatus = DateTime.MinValue;
        readonly List<int> pendingStops = new List<int>();
        readonly Queue<string> commands = new Queue<string>();
        string simulatedUid;
        bool simActive;     // the card came from the virtual reader (CardEmulator.exe)
        bool spraying;      // last state sent to the server: foam or water on
        readonly VirtualReader virtualReader = new VirtualReader();
        PlcSettings plc;
        IPlcLink link;

        public Agent(Config cfg) { this.cfg = cfg; api = new Api(cfg); }

        // Starts the PLC link, or swaps it when the settings on the admin panel change.
        void ApplyPlc(PlcSettings next)
        {
            bool linkChanged = plc == null || plc.LinkKey() != next.LinkKey();
            bool testChanged = plc == null || plc.TestMode != next.TestMode;
            if (linkChanged)
            {
                if (link != null)
                {
                    Log.Write("PLC settings changed: " + next.Describe());
                    link.Stop();
                }
                link = next.Mode == "client" ? (IPlcLink)new ModbusClient(regs, next) : new ModbusServer(regs, next);
                if (next.Mode == "client") Log.Write("PLC link: " + next.Describe() + ", every " + next.PollMs + " ms, unit " + next.UnitId);
                link.Start();
            }
            if (testChanged)
            {
                if (next.TestMode && cfg.VirtualReaderPort > 0) virtualReader.Start(cfg.VirtualReaderPort);
                else virtualReader.Stop();
            }
            plc = next;
        }

        static string LocalIps()
        {
            List<string> ips = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !ips.Contains(a.Address.ToString())) ips.Add(a.Address.ToString());
                }
            }
            catch { }
            return string.Join(",", ips.ToArray());
        }

        static int Int(Dictionary<string, object> r, string key)
        {
            object v;
            return r != null && r.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v) : 0;
        }

        static bool Bool(Dictionary<string, object> r, string key)
        {
            object v;
            return r != null && r.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static string Str(Dictionary<string, object> r, string key)
        {
            object v;
            return r != null && r.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        void Show(Status status, bool enable)
        {
            regs.Set(0, enable ? 1 : 0);
            regs.Set(4, (int)status);
        }

        void Apply(Dictionary<string, object> r)
        {
            regs.Set(2, Int(r, "balance"));
            regs.Set(3, Int(r, "charged"));
            secondsLeft = Int(r, "seconds_left");
            regs.Set(1, secondsLeft);
            regs.Set(6, secondsLeft / 60);
            object name;
            if (r != null && r.TryGetValue("holder_name", out name) && name != null) SetName(name.ToString());
        }

        const int NameLength = 20, NameStart = 21, NameMax = 32;

        void SetName(string name)
        {
            if (name.Length > NameMax) name = name.Substring(0, NameMax);
            for (int i = 0; i < NameMax; i++) regs.Set(NameStart + i, i < name.Length ? name[i] : 0);
            regs.Set(NameLength, name.Length);
        }

        public void Run()
        {
            ApplyPlc(cfg.Plc.Clone());       // from agent.ini until the admin panel answers
            Show(Status.NoReader, false);
            Dictionary<string, object> ping = api.Call("ping", new NameValueCollection());
            Log.Write(ping != null && Bool(ping, "ok") ? "Billing server OK: " + cfg.Server : "Billing server NOT reachable: " + cfg.Server);
            SendStatus();                    // picks up the PLC settings from the admin panel

            if (cfg.Simulate)
            {
                Log.Write("SIMULATION: type 'in <UID>' to insert a card, 'out' to remove it.");
                Thread input = new Thread(delegate ()
                {
                    string line;
                    while ((line = Console.ReadLine()) != null) lock (commands) commands.Enqueue(line.Trim());
                });
                input.IsBackground = true;
                input.Start();
                Show(Status.Idle, false);
            }

            while (true)
            {
                try { if (cfg.Simulate) SimulatedStep(); else Step(); }
                catch (Exception ex) { Log.Write("Error: " + ex.Message); Thread.Sleep(1000); }
            }
        }

        void Step()
        {
            HandleVirtualReader();
            // While the emulator's card is "inside", the real reader is ignored.
            if (simActive) { Thread.Sleep(200); EverySecond(); return; }
            if (!EnsureReader())
            {
                if (virtualReader.Connected && !cardPresent && regs.Get(4) == (int)Status.NoReader) Show(Status.Idle, false);
                Thread.Sleep(virtualReader.Connected ? 200 : 1000);
                EverySecond();
                return;
            }

            PcSc.READERSTATE[] st = new PcSc.READERSTATE[1];
            st[0].szReader = reader;
            st[0].dwCurrentState = cardPresent ? PcSc.STATE_PRESENT : PcSc.STATE_EMPTY;
            st[0].rgbAtr = new byte[36];
            uint rc = PcSc.SCardGetStatusChange(ctx, 500, st, 1);
            if (rc != 0 && rc != PcSc.E_TIMEOUT)
            {
                Log.Write("Reader lost (0x" + rc.ToString("X8") + ")");
                ResetReader();
                if (cardPresent) CardRemoved();
                return;
            }
            bool present = (st[0].dwEventState & PcSc.STATE_PRESENT) != 0;
            if (rc == 0 && present != cardPresent)
            {
                cardPresent = present;
                if (present) CardInserted(); else CardRemoved();
            }
            EverySecond();
        }

        void HandleVirtualReader()
        {
            string ev;
            while ((ev = virtualReader.Next()) != null)
            {
                if (ev.StartsWith("IN ") && !cardPresent)
                {
                    simulatedUid = ev.Substring(3).Trim();
                    simActive = true;
                    cardPresent = true;
                    CardInserted();
                }
                else if (ev == "OUT" && simActive)
                {
                    simActive = false;
                    cardPresent = false;
                    CardRemoved();
                }
            }
        }

        void SimulatedStep()
        {
            string cmd = null;
            lock (commands) if (commands.Count > 0) cmd = commands.Dequeue();
            if (cmd != null)
            {
                string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "in" && !cardPresent)
                {
                    simulatedUid = parts[1];
                    cardPresent = true;
                    CardInserted();
                }
                else if (parts.Length == 1 && parts[0] == "out" && cardPresent)
                {
                    cardPresent = false;
                    CardRemoved();
                }
                else Log.Write("Commands: 'in <UID>' or 'out'");
            }
            Thread.Sleep(200);
            EverySecond();
        }

        bool EnsureReader()
        {
            if (ctx != IntPtr.Zero && reader != null) return true;
            if (ctx == IntPtr.Zero && PcSc.SCardEstablishContext(PcSc.SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx) != 0)
            {
                ctx = IntPtr.Zero;
                Show(Status.NoReader, false);
                return false;
            }
            foreach (string name in PcSc.ListReaders(ctx))
            {
                if (cfg.Reader.Length == 0 || name.IndexOf(cfg.Reader, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // ACR122U exposes a SAM slot too; skip it.
                    if (cfg.Reader.Length == 0 && name.IndexOf("SAM", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    reader = name;
                    cardPresent = false;
                    Log.Write("Using reader: " + reader);
                    Show(Status.Idle, false);
                    return true;
                }
            }
            ResetReader();
            Show(Status.NoReader, false);
            return false;
        }

        void ResetReader()
        {
            if (ctx != IntPtr.Zero) PcSc.SCardReleaseContext(ctx);
            ctx = IntPtr.Zero;
            reader = null;
        }

        void CardInserted()
        {
            string uid;
            try { uid = cfg.Simulate || simActive ? simulatedUid : PcSc.ReadUid(ctx, reader); }
            catch (Exception ex)
            {
                Log.Write("Could not read card: " + ex.Message);
                Show(Status.ReadError, false);
                return;
            }
            Log.Write("Card in: " + uid);
            NameValueCollection f = new NameValueCollection();
            f["machine"] = cfg.Machine;
            f["uid"] = uid;
            Dictionary<string, object> r = api.Call("start", f);
            if (r == null) { Show(Status.ServerError, false); return; }
            lastServerOk = DateTime.Now;
            Apply(r);
            if (Bool(r, "running"))
            {
                sessionId = Int(r, "session_id");
                spraying = Bool(r, "active");
                lastTick = DateTime.Now;
                Show(Status.Running, true);
                Log.Write("Machine ON  session " + sessionId + ", balance " + Int(r, "balance") + ", ~" + (secondsLeft / 60) + " min");
                return;
            }
            string reason = Str(r, "reason");
            Status s = reason == "unknown_card" ? Status.UnknownCard
                : reason == "blocked" ? Status.Blocked
                : reason == "no_balance" ? Status.LowBalance
                : Status.ServerError;
            Show(s, false);
            SetName("");
            Log.Write("Refused: " + Str(r, "message"));
        }

        void CardRemoved()
        {
            Show(Status.Idle, false);   // stop the machine first, then settle the bill
            secondsLeft = 0;
            regs.Set(1, 0);
            regs.Set(6, 0);
            SetName("");
            spraying = false;
            Log.Write("Card out");
            if (sessionId != 0)
            {
                pendingStops.Add(sessionId);
                sessionId = 0;
                FlushStops();
            }
        }

        void FlushStops()
        {
            for (int i = pendingStops.Count - 1; i >= 0; i--)
            {
                NameValueCollection f = new NameValueCollection();
                f["session_id"] = pendingStops[i].ToString();
                Dictionary<string, object> r = api.Call("stop", f);
                if (r == null) continue; // retried every second; the server also times it out
                Log.Write("Session " + pendingStops[i] + " closed, charged " + Int(r, "charged") + ", balance " + Int(r, "balance"));
                if (!cardPresent) { regs.Set(2, Int(r, "balance")); regs.Set(3, Int(r, "charged")); }
                pendingStops.RemoveAt(i);
            }
        }

        void SendStatus()
        {
            lastStatus = DateTime.Now;
            NameValueCollection f = new NameValueCollection();
            f["machine"] = cfg.Machine;
            string readerName = cfg.Simulate ? "SIMULATION" : (reader ?? "");
            if (virtualReader.Connected)
                readerName = readerName.Length > 0 ? readerName + " + виртуелен" : "Виртуелен читач (" + virtualReader.Name + ")";
            f["reader"] = readerName;
            f["card"] = cardPresent ? "1" : "0";
            f["plc"] = link.Connected ? "1" : "0";
            f["plc_peer"] = link.Peer;
            f["plc_link"] = plc.Describe();
            f["plc_error"] = link.Connected ? "" : link.Error;
            f["local_ips"] = LocalIps();
            f["version"] = Program.Version;
            // What this agent runs now; the admin panel takes it over the first time, so an
            // upgraded install keeps the port from its old agent.ini.
            f["cur_mode"] = plc.Mode;
            f["cur_listen_ip"] = plc.ListenIp;
            f["cur_listen_port"] = plc.ListenPort.ToString();
            f["cur_allowed_ip"] = plc.AllowedIp;
            f["cur_plc_ip"] = plc.PlcIp;
            f["cur_plc_port"] = plc.PlcPort.ToString();
            Dictionary<string, object> r = api.Call("status", f);
            object conf;
            if (r != null && r.TryGetValue("config", out conf) && conf is Dictionary<string, object>)
                ApplyPlc(PlcSettings.FromServer((Dictionary<string, object>)conf, plc));
        }

        void EverySecond()
        {
            if ((DateTime.Now - lastSecond).TotalSeconds < 1) return;
            lastSecond = DateTime.Now;
            regs.Bump(5);
            if (virtualReader.Connected)
            {
                StringBuilder name = new StringBuilder();
                for (int i = 0; i < regs.Get(NameLength); i++) name.Append((char)regs.Get(NameStart + i));
                virtualReader.Send("STATE " + regs.Get(4) + "|" + regs.Get(0) + "|" + regs.Get(2) + "|" + regs.Get(1) + "|" + regs.Get(3) + "|" + name);
            }
            if ((DateTime.Now - lastStatus).TotalSeconds >= 2) SendStatus();
            if (pendingStops.Count > 0) FlushStops();
            if (sessionId == 0) return;

            // Foam or water is on: the PLC writes its program to register 10. A stale value from a
            // PLC that has gone away does not count.
            bool nowSpraying = regs.Get(0) == 1 && regs.Get(10) != 0 && link.Connected;
            bool changed = nowSpraying != spraying;
            if (nowSpraying)
            {
                secondsLeft = Math.Max(0, secondsLeft - 1);
                regs.Set(1, secondsLeft);
                regs.Set(6, secondsLeft / 60);
                if (secondsLeft == 0) regs.Set(0, 0); // out of money by our own count: stop now, the server confirms
            }
            bool due = changed || (DateTime.Now - lastTick).TotalSeconds >= cfg.TickSeconds || (nowSpraying && secondsLeft == 0);
            if (!due) return;

            lastTick = DateTime.Now;
            NameValueCollection f = new NameValueCollection();
            f["session_id"] = sessionId.ToString();
            f["active"] = nowSpraying ? "1" : "0";
            Dictionary<string, object> r = api.Call("tick", f);
            if (r == null)
            {
                if ((DateTime.Now - lastServerOk).TotalSeconds > cfg.OfflineStopSeconds)
                {
                    Show(Status.ServerError, false);
                    Log.Write("Server offline too long: machine OFF");
                }
                return;
            }
            lastServerOk = DateTime.Now;
            if (changed) Log.Write(nowSpraying ? "Spraying: billing runs" : "STOP: billing paused");
            spraying = nowSpraying;
            Apply(r);
            if (Bool(r, "running"))
            {
                Show(Status.Running, true);
                return;
            }
            Log.Write("Session " + sessionId + " ended by server (" + Str(r, "end_reason") + ")");
            sessionId = 0;
            spraying = false;
            Show(Status.Stopped, false);
        }
    }

    static class Program
    {
        public const string Version = "1.3.0";

        static void Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Log.FilePath = Path.Combine(dir, "agent.log");
            Config cfg = Config.Load(Path.Combine(dir, args.Length > 0 ? args[0] : "agent.ini"));
            Console.Title = "Автоперална Павлинка - агент";
            Console.OutputEncoding = Encoding.UTF8;
            Log.Write("Avtoperalna Pavlinka agent " + Version + " - machine " + cfg.Machine + ", server " + cfg.Server);
            if (cfg.WebPort > 0) WebServer.Start(dir, cfg);
            new Agent(cfg).Run();
        }
    }
}
