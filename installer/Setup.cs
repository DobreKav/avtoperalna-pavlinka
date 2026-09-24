// Setup.exe for Автоперална Павлинка. payload.zip (built by installer\build.bat) is embedded
// as a resource. C# 5 / .NET Framework 4.x, compiled with the csc.exe that ships with Windows.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PeralnaSetup
{
    static class Product
    {
        public const string Name = "Автоперална Павлинка";
        public const string Version = "1.0.1";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AvtoperalnaPavlinka";
        public const string FirewallRule = "Avtoperalna Pavlinka - PLC Modbus 502";
        public const string AdminUrl = "http://localhost:8080/";

        public static string Menu { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name); } }
        public static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); } }
        public static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Name + " — агент.lnk"); } }
        public static string AgentLinkName { get { return Name + " — агент.lnk"; } }
        public static string AdminLinkName { get { return Name + " — админ панел.url"; } }
    }

    class SetupForm : Form
    {
        readonly TextBox dirBox = new TextBox();
        readonly CheckBox autoStart = new CheckBox();
        readonly CheckBox desktop = new CheckBox();
        readonly CheckBox firewall = new CheckBox();
        readonly Button install = new Button();
        readonly Button cancel = new Button();
        readonly Label status = new Label();
        readonly ProgressBar progress = new ProgressBar();

        public SetupForm()
        {
            Text = Product.Name + " — инсталација";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 330);

            Label title = new Label();
            title.Text = Product.Name;
            title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            title.SetBounds(20, 16, 480, 36);

            Label info = new Label();
            info.Text = "Систем за картички: админ панел, читач на картички и врска со PLC (Siemens S7-1200).";
            info.SetBounds(20, 56, 480, 40);

            Label dirLabel = new Label();
            dirLabel.Text = "Папка за инсталација:";
            dirLabel.SetBounds(20, 102, 480, 22);
            dirBox.Text = @"C:\AvtoperalnaPavlinka";
            dirBox.SetBounds(20, 126, 390, 28);
            Button browse = new Button();
            browse.Text = "Избери…";
            browse.SetBounds(418, 125, 82, 30);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = dirBox.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) dirBox.Text = d.SelectedPath;
                }
            };

            autoStart.Text = "Стартувај автоматски кога ќе се вклучи компјутерот";
            autoStart.Checked = true;
            autoStart.SetBounds(20, 166, 480, 24);
            desktop.Text = "Икони на Desktop";
            desktop.Checked = true;
            desktop.SetBounds(20, 192, 480, 24);
            firewall.Text = "Дозволи PLC врска во Windows Firewall (порта 502)";
            firewall.Checked = true;
            firewall.SetBounds(20, 218, 480, 24);

            progress.SetBounds(20, 252, 480, 14);
            status.SetBounds(20, 270, 480, 22);
            status.ForeColor = Color.DimGray;

            install.Text = "Инсталирај";
            install.SetBounds(300, 290, 110, 32);
            install.Click += delegate { RunInstall(); };
            cancel.Text = "Откажи";
            cancel.SetBounds(418, 290, 82, 32);
            cancel.Click += delegate { Close(); };
            AcceptButton = install;

            Controls.AddRange(new Control[] { title, info, dirLabel, dirBox, browse, autoStart, desktop, firewall, progress, status, install, cancel });

            string existing = ExistingInstallDir();
            if (existing != null)
            {
                dirBox.Text = existing;
                install.Text = "Ажурирај";
                status.Text = "Веќе е инсталирано. Картичките и поставките ќе останат.";
            }
        }

        static string ExistingInstallDir()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Product.UninstallKey))
            {
                object v = k == null ? null : k.GetValue("InstallLocation");
                return v != null && Directory.Exists(v.ToString()) ? v.ToString() : null;
            }
        }

        void Step(string text, int percent)
        {
            status.Text = text;
            progress.Value = Math.Max(0, Math.Min(100, percent));
            Application.DoEvents();
        }

        void RunInstall()
        {
            string dir = dirBox.Text.Trim();
            if (dir.Length < 4 || !Path.IsPathRooted(dir))
            {
                MessageBox.Show(this, "Избери валидна папка.", Text);
                return;
            }
            install.Enabled = cancel.Enabled = dirBox.Enabled = false;
            try
            {
                Step("Запирам ја старата верзија…", 5);
                StopRunning(dir);

                Step("Копирам фајлови…", 15);
                Extract(dir);

                Step("Правам икони…", 75);
                string agent = Path.Combine(dir, "PeralnaAgent.exe");
                string uninstaller = Path.Combine(dir, "uninstall.exe");
                File.Copy(Application.ExecutablePath, uninstaller, true);
                Directory.CreateDirectory(Product.Menu);
                Shortcut(Path.Combine(Product.Menu, Product.AgentLinkName), agent, "", dir, agent, 1);
                UrlShortcut(Path.Combine(Product.Menu, Product.AdminLinkName), Product.AdminUrl, agent);
                Shortcut(Path.Combine(Product.Menu, "Деинсталирај.lnk"), uninstaller, "/uninstall", dir, agent, 1);
                if (autoStart.Checked) Shortcut(Product.StartupLink, agent, "", dir, agent, 7);
                else if (File.Exists(Product.StartupLink)) File.Delete(Product.StartupLink);
                if (desktop.Checked)
                {
                    Shortcut(Path.Combine(Product.Desktop, Product.AgentLinkName), agent, "", dir, agent, 1);
                    UrlShortcut(Path.Combine(Product.Desktop, Product.AdminLinkName), Product.AdminUrl, agent);
                }

                if (firewall.Checked)
                {
                    Step("Firewall правило за PLC…", 85);
                    Run("netsh", "advfirewall firewall delete rule name=\"" + Product.FirewallRule + "\"");
                    Run("netsh", "advfirewall firewall add rule name=\"" + Product.FirewallRule + "\" dir=in action=allow protocol=TCP localport=502");
                }

                Step("Регистрирам ја програмата…", 92);
                File.Delete(Path.Combine(dir, "uninstall.bat"));   // left over from 1.0.0
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(Product.UninstallKey))
                {
                    k.SetValue("DisplayName", Product.Name);
                    k.SetValue("DisplayVersion", Product.Version);
                    k.SetValue("Publisher", Product.Name);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", agent);
                    k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }

                Step("Стартувам…", 97);
                ProcessStartInfo psi = new ProcessStartInfo(agent);
                psi.WorkingDirectory = dir;
                psi.WindowStyle = ProcessWindowStyle.Minimized;
                Process.Start(psi);
                Thread.Sleep(3000);
                Process.Start(Product.AdminUrl);

                Step("Готово.", 100);
                MessageBox.Show(this,
                    "Инсталацијата заврши.\n\nАдмин панел: " + Product.AdminUrl + "\n\nПрв пат направи админ корисник. " +
                    "Во " + Path.Combine(dir, "agent.ini") + " може да се менуваат поставките на читачот.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                status.Text = "Грешка.";
                MessageBox.Show(this, "Инсталацијата не успеа:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = cancel.Enabled = dirBox.Enabled = true;
            }
        }

        public static void StopRunning(string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (string name in new[] { "PeralnaAgent", "php" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(5000);
                        }
                    }
                    catch { }
                }
            }
        }

        // Keeps the database and the reader settings on an update.
        static bool KeepExisting(string relative)
        {
            string r = relative.Replace('/', '\\').ToLowerInvariant();
            return r.StartsWith("data\\") || r == "agent.ini" || r == "app\\config.php";
        }

        void Extract(string dir)
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            using (Stream payload = typeof(SetupForm).Assembly.GetManifestResourceStream("payload.zip"))
            using (ZipArchive zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                int i = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    i++;
                    if (i % 10 == 0) Step("Копирам фајлови… " + entry.FullName, 15 + 55 * i / zip.Entries.Count);
                    string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.FullName.EndsWith("/") || entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    if (File.Exists(target) && KeepExisting(entry.FullName)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        // IShellLinkW keeps the Cyrillic file names; WScript.Shell turned them into "????".
        static void Shortcut(string path, string target, string args, string workDir, string icon, int showCmd)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetArguments(args);
            link.SetWorkingDirectory(workDir);
            link.SetIconLocation(icon, 0);
            link.SetShowCmd(showCmd);
            ((IPersistFile)link).Save(path, true);
            Marshal.ReleaseComObject(link);
        }

        static void UrlShortcut(string path, string url, string iconFile)
        {
            File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + url + "\r\nIconFile=" + iconFile + "\r\nIconIndex=0\r\n", Encoding.Default);
        }

        public static void Run(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi)) p.WaitForExit(15000);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    // uninstall.exe /uninstall (a copy of Setup.exe in the install folder).
    // Removes the program, shortcuts, firewall rule and registry entry; keeps data\ and agent.ini.
    static class Uninstaller
    {
        public static void Run()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            if (MessageBox.Show("Да се отстрани " + Product.Name + "?\n\nБазата со картичките (data\\peralna.sqlite) и agent.ini остануваат во\n" + dir,
                    Product.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            SetupForm.StopRunning(dir);
            SetupForm.Run("netsh", "advfirewall firewall delete rule name=\"" + Product.FirewallRule + "\"");
            foreach (string f in new[] {
                Product.StartupLink,
                Path.Combine(Product.Desktop, Product.AgentLinkName),
                Path.Combine(Product.Desktop, Product.AdminLinkName) })
                TryDelete(f);
            try { if (Directory.Exists(Product.Menu)) Directory.Delete(Product.Menu, true); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(Product.UninstallKey, false); } catch { }
            foreach (string sub in new[] { "php", "www", "app", "plc" })
                try { if (Directory.Exists(Path.Combine(dir, sub))) Directory.Delete(Path.Combine(dir, sub), true); } catch { }
            foreach (string f in new[] { "PeralnaAgent.exe", "README.md", "uninstall.bat" })
                TryDelete(Path.Combine(dir, f));

            // This exe cannot delete itself while it runs: cmd removes it a moment later.
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + Application.ExecutablePath + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
            MessageBox.Show(Product.Name + " е отстранета.\n\nПодатоците останаа во " + Path.Combine(dir, "data"), Product.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            if (args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstaller.Run();
                return;
            }
            Application.Run(new SetupForm());
        }
    }
}
