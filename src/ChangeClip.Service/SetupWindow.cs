using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChangeClip.Contracts;
using ChangeClip.Devices;

namespace ChangeClip.Service
{
    public sealed class SetupWindow : Window
    {
        private static readonly int[] CoinValues = { 5, 10, 20, 50, 100, 200 };
        private readonly RuntimePaths _paths;
        private readonly Func<DeviceOptions, CancellationToken, Task<DeviceDiscoveryResult>> _detect;
        private readonly Func<DeviceOptions, IBillAcceptor> _billFactory;
        private readonly Func<DeviceOptions, ICoinHopper> _hopperFactory;
        private DeviceOptions _options;
        private bool _working;
        private bool _loadedScanStarted;
        private bool _closed;
        private bool _closing;
        private bool _allowClose;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        public SetupWindow(RuntimePaths paths,
            Func<DeviceOptions, CancellationToken, Task<DeviceDiscoveryResult>> detect = null,
            Func<DeviceOptions, IBillAcceptor> billFactory = null,
            Func<DeviceOptions, ICoinHopper> hopperFactory = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            var discovery = new DeviceDiscovery();
            _detect = detect ?? discovery.DetectAsync;
            _billFactory = billFactory;
            _hopperFactory = hopperFactory;
            _options = LoadOptions();
            Title = "ChangeClip — Provjera uređaja";
            Width = 860;
            Height = 800;
            MinWidth = 740;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#10162B");
            FontFamily = new FontFamily("Segoe UI");
            Content = BuildContent();
            Loaded += SetupWindow_Loaded;
            Closing += SetupWindow_Closing;
            Closed += (sender, args) => { _closed = true; _lifetime.Cancel(); };
        }

        public static bool IsDefaultLaunch(string[] args)
        {
            return args == null || args.Length == 0;
        }

        public TextBlock Nv9StatusText { get; private set; }
        public TextBlock HopperStatusText { get; private set; }
        public TextBlock MessageText { get; private set; }
        public ComboBox CoinValueComboBox { get; private set; }
        public Button FindDevicesButton { get; private set; }
        public Button SaveButton { get; private set; }
        public Nv9TestControl Nv9Test { get; private set; }
        public HopperTestControl HopperTest { get; private set; }
        public DeviceOptions CurrentOptions { get { return _options.Copy(); } }

        public async Task ScanDevicesAsync()
        {
            if (_working || _closed || _closing || TestsActive) return;
            SetWorking(true);
            MessageText.Text = "Tražim povezane uređaje…";
            try
            {
                using (ApplicationLease.Acquire())
                {
                    var scanOptions = _options.Copy();
                    scanOptions.Mode = "Hardware";
                    scanOptions.AutoDetectPorts = true;
                    scanOptions.SimulatorCoins = 0;
                    var result = await _detect(scanOptions, _lifetime.Token);
                    if (_closed) return;
                    _options.AutoDetectPorts = true;
                    _options.Nv9Port = result.Nv9Port ?? "";
                    _options.HopperPort = result.HopperPort ?? "";
                    _options.HopperEcho = result.HopperEcho;
                    SetDeviceStatuses(_options.Nv9Port, _options.HopperPort);
                    MessageText.Text = result.Complete
                        ? "Uređaji su pronađeni. Izaberite apoen i sačuvajte podešavanje."
                        : String.IsNullOrWhiteSpace(result.Message)
                            ? "Uređaji nisu pronađeni. Sačuvajte apoen; automatsko prepoznavanje ostaje uključeno."
                            : result.Message + " Sačuvajte apoen; automatsko prepoznavanje ostaje uključeno.";
                }
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (InvalidOperationException error)
            {
                MessageText.Text = error.Message.IndexOf("Zatvorite aplikaciju", StringComparison.OrdinalIgnoreCase) >= 0
                    ? error.Message : "Zatvorite ChangeClip prije traženja uređaja. " + error.Message;
            }
            catch (Exception error)
            {
                MessageText.Text = "Traženje uređaja nije uspjelo: " + error.Message;
            }
            finally { if (!_closed) SetWorking(false); }
        }

        public Task SaveConfigurationAsync()
        {
            if (_working || _closed || _closing || TestsActive) return Task.CompletedTask;
            var selected = SelectedCoinValue();
            if (selected == 0)
            {
                MessageText.Text = "Izaberite apoen kovanice prije čuvanja.";
                return Task.CompletedTask;
            }

            SetWorking(true);
            try
            {
                var options = _options.Copy();
                options.Mode = "Hardware";
                options.AutoDetectPorts = true;
                options.SimulatorCoins = 0;
                options.CoinValueMinor = selected;
                options.Nv9Port = options.Nv9Port ?? "";
                options.HopperPort = options.HopperPort ?? "";
                DeviceOptionsValidator.Validate(options, requireHardware: true);
                using (ApplicationLease.Acquire())
                    DeviceConfiguration.WriteHardware(_paths.HardwareConfig, options);
                _options = options;
                MessageText.Text = String.IsNullOrWhiteSpace(options.Nv9Port) || String.IsNullOrWhiteSpace(options.HopperPort)
                    ? "Podešavanje je sačuvano. ChangeClip će nastaviti automatsko prepoznavanje uređaja."
                    : "Podešavanje je sačuvano. Pokrenite ChangeClip kada budete spremni.";
            }
            catch (InvalidOperationException error)
            {
                MessageText.Text = error.Message.IndexOf("Zatvorite aplikaciju", StringComparison.OrdinalIgnoreCase) >= 0
                    ? error.Message : "Zatvorite ChangeClip prije čuvanja podešavanja. " + error.Message;
            }
            catch (Exception error)
            {
                MessageText.Text = "Podešavanje nije sačuvano: " + error.Message;
            }
            finally { SetWorking(false); }
            return Task.CompletedTask;
        }

        private async void SetupWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedScanStarted) return;
            _loadedScanStarted = true;
            await ScanDevicesAsync();
        }

        private async void SetupWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            SetWorking(true);
            _lifetime.Cancel();
            MessageText.Text = "Zaustavljam uređaje i čekam završno očitanje…";
            await Task.Yield();
            try
            {
                await Task.WhenAll(Nv9Test.ShutdownAsync(), HopperTest.ShutdownAsync());
                _allowClose = true;
                Close();
            }
            catch (Exception error)
            {
                _closing = false;
                SetWorking(false);
                MessageText.Text = "Uređaji nisu završili zaustavljanje: " + error.Message;
            }
        }

        private bool TestsActive { get { return Nv9Test.IsActive || HopperTest.IsActive; } }

        private void TestStateChanged(object sender, EventArgs args)
        {
            UpdateActions();
        }

        private FrameworkElement BuildContent()
        {
            var outer = new Border { Background = Brush("#10162B"), Padding = new Thickness(30) };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = grid;

            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            heading.Children.Add(new TextBlock
            {
                Text = "CHANGECLIP", FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Brush("#A9B5D0")
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Provjera uređaja", FontSize = 28, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetRow(heading, 0);
            grid.Children.Add(heading);

            var deviceCard = new Border
            {
                Background = Brush("#1B2440"), BorderBrush = Brush("#273250"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 18)
            };
            var devices = new StackPanel();
            devices.Children.Add(new TextBlock
            {
                Text = "Povezani uređaji", FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Brush("#A9B5D0"), Margin = new Thickness(0, 0, 0, 8)
            });
            Nv9StatusText = DeviceStatusText("NV9: Nije pronađen.");
            HopperStatusText = DeviceStatusText("Hopper: Nije pronađen.");
            devices.Children.Add(Nv9StatusText);
            devices.Children.Add(HopperStatusText);
            deviceCard.Child = devices;
            Grid.SetRow(deviceCard, 1);
            grid.Children.Add(deviceCard);

            var tests = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            tests.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tests.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Nv9Test = new Nv9TestControl(() => _options.Copy(), _billFactory) { Margin = new Thickness(0, 0, 10, 0) };
            HopperTest = new HopperTestControl(() => _options.Copy(), () => Nv9Test.StopAsync(), _hopperFactory);
            Nv9Test.StateChanged += TestStateChanged;
            HopperTest.StateChanged += TestStateChanged;
            tests.Children.Add(Nv9Test);
            Grid.SetColumn(HopperTest, 1);
            tests.Children.Add(HopperTest);
            Grid.SetRow(tests, 2);
            grid.Children.Add(tests);

            var coinBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            coinBlock.Children.Add(new TextBlock
            {
                Text = "Apoen kovanice", FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Brush("#A9B5D0"), Margin = new Thickness(0, 0, 0, 7)
            });
            CoinValueComboBox = new ComboBox
            {
                Name = "CoinValueComboBox", Height = 42, FontSize = 16, Padding = new Thickness(10, 6, 10, 6),
                Background = Brushes.White, Foreground = Brush("#10162B"), BorderBrush = Brush("#384564")
            };
            foreach (int value in CoinValues)
                CoinValueComboBox.Items.Add(new ComboBoxItem
                {
                    Tag = value,
                    Content = (value / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("bs-Latn-BA")) + " KM (" + value + " feninga)",
                    Background = Brushes.White, Foreground = Brush("#10162B")
                });
            SelectSavedCoinValue();
            coinBlock.Children.Add(CoinValueComboBox);
            Grid.SetRow(coinBlock, 3);
            grid.Children.Add(coinBlock);

            var actions = new Grid { Margin = new Thickness(0, 2, 0, 14) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FindDevicesButton = ActionButton("Pronađi uređaje", "#252F4D");
            SaveButton = ActionButton("Sačuvaj", "#6D4CC3");
            FindDevicesButton.Click += async delegate { await ScanDevicesAsync(); };
            SaveButton.Click += async delegate { await SaveConfigurationAsync(); };
            Grid.SetColumn(FindDevicesButton, 0);
            Grid.SetColumn(SaveButton, 1);
            actions.Children.Add(FindDevicesButton);
            actions.Children.Add(SaveButton);
            Grid.SetRow(actions, 4);
            grid.Children.Add(actions);

            MessageText = new TextBlock
            {
                Text = "Automatsko prepoznavanje uređaja je uključeno.",
                Foreground = Brush("#A9B5D0"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 2, 0)
            };
            var messageScroll = new ScrollViewer
            {
                Content = MessageText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 120,
                Margin = new Thickness(0, 0, 0, 2)
            };
            Grid.SetRow(messageScroll, 5);
            grid.Children.Add(messageScroll);
            return new ScrollViewer
            {
                Content = outer,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        private DeviceOptions LoadOptions()
        {
            if (!File.Exists(_paths.HardwareConfig))
            {
                var fresh = new DeviceOptions { AutoDetectPorts = true, Mode = "Hardware", SimulatorCoins = 0 };
                return fresh;
            }
            try
            {
                var loaded = DeviceConfiguration.Read(_paths.HardwareConfig);
                loaded.AutoDetectPorts = true;
                loaded.Mode = "Hardware";
                loaded.SimulatorCoins = 0;
                return loaded;
            }
            catch { return new DeviceOptions { AutoDetectPorts = true, Mode = "Hardware", SimulatorCoins = 0 }; }
        }

        private void SelectSavedCoinValue()
        {
            for (int i = 0; i < CoinValueComboBox.Items.Count; i++)
            {
                var item = (ComboBoxItem)CoinValueComboBox.Items[i];
                if ((int)item.Tag == _options.CoinValueMinor)
                {
                    CoinValueComboBox.SelectedIndex = i;
                    return;
                }
            }
            CoinValueComboBox.SelectedIndex = -1;
        }

        private int SelectedCoinValue()
        {
            var item = CoinValueComboBox.SelectedItem as ComboBoxItem;
            return item == null ? 0 : (int)item.Tag;
        }

        private void SetDeviceStatuses(string nv9, string hopper)
        {
            Nv9StatusText.Text = String.IsNullOrWhiteSpace(nv9) ? "NV9: Nije pronađen." : "NV9: " + nv9;
            HopperStatusText.Text = String.IsNullOrWhiteSpace(hopper) ? "Hopper: Nije pronađen." : "Hopper: " + hopper;
        }

        private void SetWorking(bool working)
        {
            _working = working;
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool idle = !_working && !_closing && !TestsActive;
            FindDevicesButton.IsEnabled = idle;
            SaveButton.IsEnabled = idle;
            CoinValueComboBox.IsEnabled = idle;
            Nv9Test.IsEnabled = !_working && !_closing && !HopperTest.IsActive;
            HopperTest.IsEnabled = !_working && !_closing;
        }

        private static TextBlock DeviceStatusText(string text)
        {
            return new TextBlock
            {
                Text = text, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 0, 3)
            };
        }

        private static Button ActionButton(string caption, string background)
        {
            return new Button
            {
                Content = caption, FontSize = 16, FontWeight = FontWeights.SemiBold,
                Background = Brush(background), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Padding = new Thickness(20, 12, 20, 12),
                MinHeight = 50, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
