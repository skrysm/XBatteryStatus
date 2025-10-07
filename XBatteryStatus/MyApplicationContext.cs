using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation.Collections;
using Windows.Storage.Streams;

using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

using Octokit;

using XBatteryStatus.Properties;

using Application = System.Windows.Forms.Application;

namespace XBatteryStatus
{
    public class MyApplicationContext : ApplicationContext
    {
        private string version = "V1.3.4";
        private string releaseUrl = @"https://github.com/tommaier123/XBatteryStatus/releases";

        private NotifyIcon notifyIcon = new NotifyIcon();
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem themeButton;
        private ToolStripMenuItem hideButton;
        private ToolStripMenuItem numbersButton;

        private Timer UpdateTimer;
        private Timer DiscoverTimer;
        private Timer HideTimeoutTimer;
        private Timer SoftwareUpdateTimer;

        public List<BluetoothLEDevice> pairedGamepads = new List<BluetoothLEDevice>();
        public BluetoothLEDevice connectedGamepad;
        public GattCharacteristic batteryCharacteristic;
        public Radio bluetoothRadio;

        private int lastBattery = 100;

        private bool lightMode;

        public MyApplicationContext()
        {
            this.HideTimeoutTimer = new Timer();
            this.HideTimeoutTimer.Tick += (x, y) => HideTimeout();
            this.HideTimeoutTimer.Interval = 10000;
            this.HideTimeoutTimer.Start();

            this.SoftwareUpdateTimer = new Timer();
            this.SoftwareUpdateTimer.Tick += (x, y) => { CheckSoftwareUpdate(); };
            this.SoftwareUpdateTimer.Interval = 30000;
            this.SoftwareUpdateTimer.Start();

            this.lightMode = IsLightMode();
            SetIcon(-1, "?");
            this.notifyIcon.Text = "XBatteryStatus: Looking for paired controller";
            this.notifyIcon.Visible = true;

            this.contextMenu = new ContextMenuStrip();

            this.themeButton = new ToolStripMenuItem("Theme");
            this.themeButton.DropDownItems.Add("Auto", null, ThemeClicked);
            this.themeButton.DropDownItems.Add("Light", null, ThemeClicked);
            this.themeButton.DropDownItems.Add("Dark", null, ThemeClicked);
            UpdateThemeButton();
            this.contextMenu.Items.Add(this.themeButton);

            this.hideButton = new ToolStripMenuItem("Auto Hide", null, HideClicked);
            UpdateHideButton();
            this.contextMenu.Items.Add(this.hideButton);

            this.numbersButton = new ToolStripMenuItem("Numeric", null, NumbersClicked);
            UpdateNumbersButton();
            this.contextMenu.Items.Add(this.numbersButton);

            ToolStripMenuItem versionButton = new ToolStripMenuItem(this.version, null, VersionClicked);
            this.contextMenu.Items.Add(versionButton);

            ToolStripMenuItem exitButton = new ToolStripMenuItem("Exit", null, ExitClicked);
            this.contextMenu.Items.Add(exitButton);

            this.notifyIcon.ContextMenuStrip = this.contextMenu;

            var radios = Radio.GetRadiosAsync().GetResults();
            this.bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            if (this.bluetoothRadio != null)
            {
                this.bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
            }


            FindBleController();

            this.UpdateTimer = new Timer();
            this.UpdateTimer.Tick += (x, y) => Update();
            this.UpdateTimer.Interval = 10000;
            this.UpdateTimer.Start();

            this.DiscoverTimer = new Timer();
            this.DiscoverTimer.Tick += (x, y) => FindBleController();
            this.DiscoverTimer.Interval = 60000;
            this.DiscoverTimer.Start();
        }

        private void CheckSoftwareUpdate()
        {
            try
            {
                GitHubClient github = new GitHubClient(new ProductHeaderValue("XBatteryStatus"));
                var all = github.Repository.Release.GetAll("tommaier123", "XBatteryStatus").Result.Where(x => !x.Prerelease).ToList();
                var latest = all.OrderByDescending(x => int.Parse(x.TagName.Substring(1).Replace(".", ""))).FirstOrDefault();

                if (latest != null && int.Parse(this.version.Substring(1).Replace(".", "")) < int.Parse(latest.TagName.Substring(1).Replace(".", "")))
                {
                    if (Settings.Default.updateVersion != latest.TagName)
                    {
                        Settings.Default.updateVersion = latest.TagName;
                        Settings.Default.reminderCount = 0;
                    }

                    if (Settings.Default.reminderCount < 3)
                    {
                        Settings.Default.reminderCount++;
                        Settings.Default.Save();

                        ToastNotificationManagerCompat.OnActivated += toastArgs =>
                        {
                            ToastArguments args = ToastArguments.Parse(toastArgs.Argument);
                            ValueSet userInput = toastArgs.UserInput;

                            if (args.ToString() == "action=update")
                            {
                                ToastNotificationManagerCompat.Uninstall();
                                ToastNotificationManagerCompat.History.Clear();

                                string path = Path.Combine(Path.GetTempPath(), "XBatteryStatus", "XBatteryStatus.msi");

                                if (!Directory.Exists(Path.GetDirectoryName(path)))
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                                }

                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                }

                                using (var client = new WebClient())
                                {
                                    client.DownloadFile(latest.Assets.Where(x => x.BrowserDownloadUrl.EndsWith(".msi")).First().BrowserDownloadUrl, path);
                                }

                                Process process = new Process();
                                process.StartInfo.FileName = "msiexec";
                                process.StartInfo.Arguments = " /i " + path + " /qr";
                                process.StartInfo.Verb = "runas";
                                process.Start();

                                Exit();
                            }
                        };

                        new ToastContentBuilder()
                        .AddText("XBatteryStatus")
                        .AddText("New Version Available on GitHub")
                        .AddButton(new ToastButton()
                                .SetContent("Download")
                                .SetProtocolActivation(new Uri(this.releaseUrl)))
                        .AddButton(new ToastButton()
                                .SetContent("Update")
                                .AddArgument("action", "update"))
                        .AddButton(new ToastButton()
                                .SetContent("Dismiss")
                                .SetDismissActivation())
                        .Show();
                    }
                }

                this.UpdateTimer.Stop();
            }
            catch (Exception e)
            {
                this.SoftwareUpdateTimer.Interval = 90 * 60000;
                LogError(e);
            }
        }

        private async void FindBleController()
        {
            if (this.bluetoothRadio?.State == RadioState.On)
            {
                List<BluetoothLEDevice> foundGamepads = new List<BluetoothLEDevice>();

                foreach (var device in await DeviceInformation.FindAllAsync())
                {
                    try
                    {
                        BluetoothLEDevice bleDevice = await BluetoothLEDevice.FromIdAsync(device.Id);

                        if (bleDevice?.Appearance.SubCategory == BluetoothLEAppearanceSubcategories.Gamepad)//get the gamepads
                        {
                            GattDeviceService service = bleDevice.GetGattService(new Guid("0000180f-0000-1000-8000-00805f9b34fb"));
                            GattCharacteristic characteristic = service.GetCharacteristics(new Guid("00002a19-0000-1000-8000-00805f9b34fb")).First();

                            if (service != null && characteristic != null)//get the gamepads with battery status
                            {
                                foundGamepads.Add(bleDevice);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // TODO: Add logging?
                        //LogError(e);
                    }
                }

                var newGamepads = foundGamepads.Except(this.pairedGamepads).ToList();
                var removedGamepads = this.pairedGamepads.Except(foundGamepads).ToList();

                foreach (var gamepad in newGamepads)
                {
                    gamepad.ConnectionStatusChanged += ConnectionStatusChanged;
                }

                foreach (var gamepad in removedGamepads)
                {
                    if (gamepad != null)
                    {
                        gamepad.ConnectionStatusChanged -= ConnectionStatusChanged;
                    }
                }

                this.pairedGamepads = foundGamepads;

                if (this.pairedGamepads.Count == 0)
                {
                    SetIcon(-1, "!");
                    this.notifyIcon.Text = "XBatteryStatus: No paired controller with battery service found";
                }
                else
                {
                    var connectedGamepads = this.pairedGamepads.Where(x => x.ConnectionStatus == BluetoothConnectionStatus.Connected).ToList();

                    if (connectedGamepads.Count == 0)
                    {
                        SetIcon(-1, "!");
                        this.notifyIcon.Text = "XBatteryStatus: No controller is connected";
                    }
                    else
                    {
                        ConnectGamepad(connectedGamepads.First());
                    }
                }
            }
            else
            {
                SetIcon(-1, "!");
                this.notifyIcon.Text = "XBatteryStatus: Bluetooth is turned off";
            }

            Update();
        }

        private void BluetoothRadio_StateChanged(Radio sender, object args)
        {
            FindBleController();
        }

        private void ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                ConnectGamepad(sender);
            }
            else if (sender == this.connectedGamepad)
            {
                FindBleController();//another controller might be connected
            }
        }

        public void ConnectGamepad(BluetoothLEDevice device)
        {
            if (this.connectedGamepad == null || this.connectedGamepad.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                try
                {
                    GattDeviceService service = device.GetGattService(new Guid("0000180f-0000-1000-8000-00805f9b34fb"));
                    GattCharacteristic characteristic = service.GetCharacteristics(new Guid("00002a19-0000-1000-8000-00805f9b34fb")).First();

                    if (service != null && characteristic != null)
                    {
                        this.connectedGamepad = device;
                        this.batteryCharacteristic = characteristic;
                        Update();
                    }
                }
                catch (Exception e) { LogError(e); }
            }
        }

        public void Update()
        {
            bool enabled = (this.bluetoothRadio?.State == RadioState.On && this.connectedGamepad?.ConnectionStatus == BluetoothConnectionStatus.Connected) || this.HideTimeoutTimer.Enabled;
            this.notifyIcon.Visible = Settings.Default.hide ? enabled : true;
            if (enabled)
            {
                ReadBattery();
            }
        }

        private async void ReadBattery()
        {
            if (this.connectedGamepad?.ConnectionStatus == BluetoothConnectionStatus.Connected && this.batteryCharacteristic != null)
            {
                GattReadResult result = await this.batteryCharacteristic.ReadValueAsync();

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var reader = DataReader.FromBuffer(result.Value);
                    int val = reader.ReadByte();
                    string notify = val + "% - " + this.connectedGamepad.Name;
                    this.notifyIcon.Text = "XBatteryStatus: " + notify;

                    SetIcon(val);

                    if ((this.lastBattery > 15 && val <= 15) || (this.lastBattery > 10 && val <= 10) || (this.lastBattery > 5 && val <= 5))
                    {
                        new ToastContentBuilder().AddText("Low Battery").AddText(notify)
                            .Show();
                    }

                    this.lastBattery = val;
                }
            }
        }

        private void ExitClicked(object sender, EventArgs e)
        {
            Exit();
        }

        private void Exit()
        {
            this.notifyIcon.Visible = false;
            ToastNotificationManagerCompat.Uninstall();
            ToastNotificationManagerCompat.History.Clear();
            Application.Exit();
        }

        private void ThemeClicked(object sender, EventArgs e)
        {
            if (sender == this.themeButton.DropDownItems[1]) { Settings.Default.theme = 1; }
            else if (sender == this.themeButton.DropDownItems[2]) { Settings.Default.theme = 2; }
            else { Settings.Default.theme = 0; }
            Settings.Default.Save();
            Update();
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            if (Settings.Default.theme == 1)
            {
                ((ToolStripMenuItem)this.themeButton.DropDownItems[0]).Checked = false;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[1]).Checked = true;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[2]).Checked = false;
            }
            else if (Settings.Default.theme == 2)
            {
                ((ToolStripMenuItem)this.themeButton.DropDownItems[0]).Checked = false;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[1]).Checked = false;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[2]).Checked = true;
            }
            else
            {
                ((ToolStripMenuItem)this.themeButton.DropDownItems[0]).Checked = true;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[1]).Checked = false;
                ((ToolStripMenuItem)this.themeButton.DropDownItems[2]).Checked = false;
            }

            FindBleController();
        }

        private void HideClicked(object sender, EventArgs e)
        {
            Settings.Default.hide = !Settings.Default.hide;
            Settings.Default.Save();
            UpdateHideButton();
        }

        private void UpdateHideButton()
        {
            this.hideButton.Checked = Settings.Default.hide;

            Update();
        }

        private void HideTimeout()
        {
            this.HideTimeoutTimer.Stop();
            Update();
        }

        private void NumbersClicked(object sender, EventArgs e)
        {
            Settings.Default.numbers = !Settings.Default.numbers;
            Settings.Default.Save();
            UpdateNumbersButton();
        }

        private void UpdateNumbersButton()
        {
            this.numbersButton.Checked = Settings.Default.numbers;
            Update();
        }

        public bool IsLightMode()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key != null)
            {
                object registryValueObject = key.GetValue("AppsUseLightTheme");

                if (registryValueObject != null)
                {
                    int registryValue = (int)registryValueObject;
                    return registryValue == 1;
                }
            }

            return true;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public void SetIcon(int val, string s = "")
        {
            if (this.notifyIcon.Icon != null)
            {
                DestroyIcon(this.notifyIcon.Icon.Handle);
            }

            this.notifyIcon.Icon = GetIcon(val, s);
        }

        public Icon GetIcon(int val, string s = "")
        {
            var icon = Resources.icon00;

            if (val >= 0)
            {
                if (Settings.Default.numbers)
                {
                    if (val >= 100)
                    {
                        val = 99;
                    }

                    AddDigit(icon, DigitToBitmap(val / 10), false);
                    AddDigit(icon, DigitToBitmap(val % 10), true);
                }
                else
                {
                    AddPercentage(icon, val);
                }
            }
            else
            {
                if (s == "!")
                {
                    AddSymbol(icon, Resources.symbolE);
                }
                else if (s == "?")
                {
                    AddSymbol(icon, Resources.symbolQ);
                }
            }

            if ((Settings.Default.theme == 0 && !this.lightMode) || Settings.Default.theme == 1)
            {
                IntPtr Hicon = icon.GetHicon();
                return Icon.FromHandle(Hicon);
            }
            else
            {
                IntPtr Hicon = InvertBitmap(icon).GetHicon();
                return Icon.FromHandle(Hicon);
            }
        }

        public Bitmap DigitToBitmap(int digit)
        {
            return digit switch
            {
                0 => Resources.number0,
                1 => Resources.number1,
                2 => Resources.number2,
                3 => Resources.number3,
                4 => Resources.number4,
                5 => Resources.number5,
                6 => Resources.number6,
                7 => Resources.number7,
                8 => Resources.number8,
                9 => Resources.number9,
                _ => Resources.number0
            };
        }

        public Bitmap AddDigit(Bitmap bitmap, Bitmap number, bool bottom)
        {
            int x_start = 21;
            int y_start = bottom ? 17 : 6;

            for (int y = 0; y < number.Height; y++)
            {
                for (int x = 0; x < number.Width; x++)
                {
                    Color pixelColor = number.GetPixel(x, y);
                    if (pixelColor.A > 0)
                    {
                        bitmap.SetPixel(x + x_start, y + y_start, pixelColor);
                    }
                }
            }

            return bitmap;
        }

        public Bitmap AddPercentage(Bitmap bitmap, int val)
        {
            int y_start = 7 + (int)((100 - val) / 5.0 + 0.5);

            for (int y = y_start; y < 27; y++)
            {
                for (int x = 20; x < 28; x++)
                {
                    Color pixelColor = Color.FromArgb(255, 255, 255, 255);
                    if (pixelColor.A > 0)
                    {
                        bitmap.SetPixel(x, y, pixelColor);
                    }
                }
            }

            return bitmap;
        }

        public Bitmap AddSymbol(Bitmap bitmap, Bitmap symbol)
        {
            int x_start = 19;
            int y_start = 6;

            for (int y = 0; y < symbol.Height; y++)
            {
                for (int x = 0; x < symbol.Width; x++)
                {
                    Color pixelColor = symbol.GetPixel(x, y);
                    if (pixelColor.A > 0)
                    {
                        bitmap.SetPixel(x + x_start, y + y_start, pixelColor);
                    }
                }
            }

            return bitmap;
        }

        public Bitmap InvertBitmap(Bitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color pixelColor = bitmap.GetPixel(x, y);
                    Color invertedColor = Color.FromArgb(pixelColor.A, 255 - pixelColor.R, 255 - pixelColor.G, 255 - pixelColor.B);
                    bitmap.SetPixel(x, y, invertedColor);
                }
            }
            return bitmap;
        }

        private void VersionClicked(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo(this.releaseUrl) { UseShellExecute = true });
        }

        private void Log(string s)
        {
#if DEBUG
            Console.WriteLine(s);
#endif
        }

        private void LogError(Exception e)
        {
#if DEBUG
            Log(e.StackTrace);
            Log(e.Message);
            Log("");
#endif
        }
    }
}
