// Setup.exe for Автоперална Павлинка. payload.zip (built by installer\build.bat) is embedded
// as a resource. C# 5 / .NET Framework 4.x, compiled with the csc.exe that ships with Windows.
using System;
using System.Diagnostics;
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
        public const string Version = "1.0.0";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AvtoperalnaPavlinka";
        public const string FirewallRule = "Avtoperalna Pavlinka - PLC Modbus 502";
        public const string AdminUrl = "http://localhost:8080/";
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
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Product.Name);
                Directory.CreateDirectory(menu);
                Shortcut(Path.Combine(menu, Product.Name + " — агент.lnk"), agent, dir, 1);
                UrlShortcut(Path.Combine(menu, Product.Name + " — админ панел.url"), Product.AdminUrl, agent);
                Shortcut(Path.Combine(menu, "Деинсталирај.lnk"), Path.Combine(dir, "uninstall.bat"), dir, 1);
                string startupLink = Path.Combine(startup, Product.Name + " — агент.lnk");
                if (autoStart.Checked) Shortcut(startupLink, agent, dir, 7);
                else if (File.Exists(startupLink)) File.Delete(startupLink);
                if (desktop.Checked)
                {
                    Shortcut(Path.Combine(desk, Product.Name + " — агент.lnk"), agent, dir, 1);
                    UrlShortcut(Path.Combine(desk, Product.Name + " — админ панел.url"), Product.AdminUrl, agent);
                }

                if (firewall.Checked)
                {
                    Step("Firewall правило за PLC…", 85);
                    Run("netsh", "advfirewall firewall delete rule name=\"" + Product.FirewallRule + "\"");
                    Run("netsh", "advfirewall firewall add rule name=\"" + Product.FirewallRule + "\" dir=in action=allow protocol=TCP localport=502");
                }

                Step("Регистрирам ја програмата…", 92);
                WriteUninstaller(dir, startupLink, desk, menu);
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(Product.UninstallKey))
                {
                    k.SetValue("DisplayName", Product.Name);
                    k.SetValue("DisplayVersion", Product.Version);
                    k.SetValue("Publisher", Product.Name);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", agent);
                    k.SetValue("UninstallString", "\"" + Path.Combine(dir, "uninstall.bat") + "\"");
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

        static void StopRunning(string dir)
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

        static void Shortcut(string path, string target, string workDir, int windowStyle)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = workDir;
            link.WindowStyle = windowStyle;
            link.IconLocation = Path.Combine(workDir, "PeralnaAgent.exe") + ",0";
            link.Save();
        }

        static void UrlShortcut(string path, string url, string iconFile)
        {
            File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + url + "\r\nIconFile=" + iconFile + "\r\nIconIndex=0\r\n", Encoding.Default);
        }

        static void Run(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi)) p.WaitForExit(15000);
        }

        static void WriteUninstaller(string dir, string startupLink, string desk, string menu)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("@echo off");
            b.AppendLine("chcp 65001 >nul");
            b.AppendLine("net session >nul 2>&1 || (powershell -Command \"Start-Process '%~f0' -Verb RunAs\" & exit /b)");
            b.AppendLine("echo Деинсталирање на " + Product.Name + ". Базата (data\\peralna.sqlite) останува.");
            b.AppendLine("pause");
            b.AppendLine("taskkill /F /IM PeralnaAgent.exe >nul 2>&1");
            b.AppendLine("powershell -NoProfile -Command \"Get-Process php -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '" + dir + "\\*' } | Stop-Process -Force\"");
            b.AppendLine("netsh advfirewall firewall delete rule name=\"" + Product.FirewallRule + "\" >nul 2>&1");
            b.AppendLine("del \"" + startupLink + "\" >nul 2>&1");
            b.AppendLine("del \"" + Path.Combine(desk, Product.Name + " — агент.lnk") + "\" >nul 2>&1");
            b.AppendLine("del \"" + Path.Combine(desk, Product.Name + " — админ панел.url") + "\" >nul 2>&1");
            b.AppendLine("rmdir /S /Q \"" + menu + "\" >nul 2>&1");
            b.AppendLine("reg delete \"HKLM\\" + Product.UninstallKey + "\" /f >nul 2>&1");
            foreach (string sub in new[] { "php", "www", "app", "plc" })
                b.AppendLine("rmdir /S /Q \"" + Path.Combine(dir, sub) + "\" >nul 2>&1");
            b.AppendLine("del \"" + Path.Combine(dir, "PeralnaAgent.exe") + "\" \"" + Path.Combine(dir, "README.md") + "\" >nul 2>&1");
            b.AppendLine("echo Готово. Во " + dir + " останаа data\\ и agent.ini.");
            b.AppendLine("pause");
            b.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(Path.Combine(dir, "uninstall.bat"), b.ToString(), new UTF8Encoding(false));
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new SetupForm());
        }
    }
}
