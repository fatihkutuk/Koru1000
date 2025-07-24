using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using Koru1000.DatabaseManager;
using Koru1000.DatabaseManager.Repositories;
using Koru1000.DatabaseManager.Services;
using Koru1000.Core.Models;
using Koru1000.Core.Models.ExchangerModels;
using Koru1000.Core.Models.KbinModels;
using Koru1000.Core.Models.ViewModels;
using Koru1000.Shared;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Koru1000.Core.Models.DomainModels;
using Koru1000.Core.Models.DomainModels;
using System.Windows.Controls;
using System.Windows.Media;
namespace Koru1000.ManagerUI
{
    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();
        private SystemHierarchyService _systemHierarchyService; // EKLE
        private Timer _tagRefreshTimer;
        private DeviceNode _currentSelectedDevice;
        private DatabaseManager.DatabaseManager _dbManager;
        private ChannelDeviceRepository _channelDeviceRepo;
        private ChannelTypesRepository _channelTypesRepo;
        private DeviceTypeRepository _deviceTypeRepo;
        private TagRepository _tagRepo;
        private HierarchyService _hierarchyService;
        private AppSettings _settings;
        private System.Windows.Threading.DispatcherTimer _statusTimer;

        public MainWindow()
        {
            AllocConsole(); // Console penceresi aç - Debug için
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeTimer();
            LoadSettings();
        }

        private void InitializeTimer()
        {
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += (s, e) => TimeStatusText.Text = DateTime.Now.ToString("HH:mm:ss");
            _statusTimer.Start();
        }
        private async void CheckServiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckServiceButton.IsEnabled = false;
                CheckServiceButton.Content = "Checking...";

                // OPC Service'in çalışıp çalışmadığını kontrol et
                var isServiceRunning = await CheckOpcServiceStatusAsync();

                if (isServiceRunning)
                {
                    ServiceStatusText.Text = "✅ OPC Service is running and processing tags";
                    ServiceStatusText.Foreground = Brushes.Green;
                }
                else
                {
                    ServiceStatusText.Text = "❌ OPC Service is not running or not processing data";
                    ServiceStatusText.Foreground = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                ServiceStatusText.Text = $"❌ Error checking service: {ex.Message}";
                ServiceStatusText.Foreground = Brushes.Red;
            }
            finally
            {
                CheckServiceButton.IsEnabled = true;
                CheckServiceButton.Content = "Check Service Status";
            }
        }
        private async void RestartDriverMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HierarchyTreeView.SelectedItem is DriverNode driver && string.IsNullOrEmpty(driver.DriverTypeName))
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to restart driver '{driver.Name}'?\n\nThis will:\n" +
                    "1. Stop the driver\n" +
                    "2. Wait 2 seconds\n" +
                    "3. Start the driver again",
                    "Restart Driver",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await RestartDriverAsync(driver.Id, driver.Name);
                }
            }
        }

        private void ViewDriverConfigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HierarchyTreeView.SelectedItem is DriverNode driver && string.IsNullOrEmpty(driver.DriverTypeName))
            {
                ShowDriverConfigWindow(driver);
            }
        }

        // Restart driver metodu
        private async Task RestartDriverAsync(int driverId, string driverName)
        {
            try
            {
                StatusText.Text = $"Restarting driver {driverName}...";

                // 1. Stop driver
                StatusText.Text = $"Stopping driver {driverName}...";
                await Task.Delay(1000); // Simülasyon - gerçekte OPC Service API'si çağrılacak

                // 2. Wait
                StatusText.Text = $"Waiting before restart...";
                await Task.Delay(2000);

                // 3. Start driver
                StatusText.Text = $"Starting driver {driverName}...";
                await Task.Delay(1000); // Simülasyon - gerçekte OPC Service API'si çağrılacak

                MessageBox.Show($"Driver {driverName} restart command sent.\nCheck OPC Service logs for status.",
                    "Driver Restart", MessageBoxButton.OK, MessageBoxImage.Information);

                StatusText.Text = $"Driver {driverName} restart completed";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restart driver: {ex.Message}", "Error");
                StatusText.Text = "Driver restart failed";
            }
        }

        // Driver config gösterme metodu
        private void ShowDriverConfigWindow(DriverNode driver)
        {
            try
            {
                // Driver bilgilerini SystemHierarchy'den al
                var systemHierarchy = _systemHierarchyService?.CurrentHierarchy;
                if (systemHierarchy == null)
                {
                    MessageBox.Show("Hierarchy not loaded. Please refresh first.", "Error");
                    return;
                }

                // Driver'ı bul
                DriverModel driverModel = null;
                foreach (var driverType in systemHierarchy.DriverTypes)
                {
                    driverModel = driverType.Drivers.FirstOrDefault(d => d.Id == driver.Id);
                    if (driverModel != null) break;
                }

                if (driverModel == null)
                {
                    MessageBox.Show("Driver not found in hierarchy.", "Error");
                    return;
                }

                // Config window oluştur
                var configWindow = new Window
                {
                    Title = $"Driver Configuration - {driver.Name}",
                    Width = 600,
                    Height = 500,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this
                };

                var scrollViewer = new ScrollViewer();
                var stackPanel = new StackPanel { Margin = new Thickness(20) };
                scrollViewer.Content = stackPanel;
                configWindow.Content = scrollViewer;

                // Header
                stackPanel.Children.Add(new TextBlock
                {
                    Text = $"🔧 Driver: {driver.Name}",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 20)
                });

                // Basic Info
                stackPanel.Children.Add(new TextBlock { Text = $"Driver ID: {driverModel.Id}", Margin = new Thickness(0, 5, 0, 5) });
                stackPanel.Children.Add(new TextBlock { Text = $"Driver Type ID: {driverModel.DriverTypeId}", Margin = new Thickness(0, 5, 0, 5) });
                stackPanel.Children.Add(new TextBlock { Text = $"Endpoint URL: {driverModel.EndpointUrl}", Margin = new Thickness(0, 5, 0, 5) });
                stackPanel.Children.Add(new TextBlock { Text = $"Protocol: {driverModel.ProtocolType}", Margin = new Thickness(0, 5, 0, 5) });
                stackPanel.Children.Add(new TextBlock { Text = $"Namespace: {driverModel.Namespace}", Margin = new Thickness(0, 5, 0, 5) });

                stackPanel.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });

                // Connection Settings
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "🔗 Connection Settings",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                stackPanel.Children.Add(new TextBlock { Text = $"Update Rate: {driverModel.ConnectionSettings.UpdateRate} ms", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Publishing Interval: {driverModel.ConnectionSettings.PublishingInterval} ms", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Session Timeout: {driverModel.ConnectionSettings.SessionTimeout} ms", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Max Tags Per Subscription: {driverModel.ConnectionSettings.MaxTagsPerSubscription}", Margin = new Thickness(10, 3, 0, 3) });

                stackPanel.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });

                // Security Settings
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "🔒 Security Settings",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                stackPanel.Children.Add(new TextBlock { Text = $"Security Mode: {driverModel.SecuritySettings.Mode}", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Security Policy: {driverModel.SecuritySettings.Policy}", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"User Token Type: {driverModel.SecuritySettings.UserTokenType}", Margin = new Thickness(10, 3, 0, 3) });

                stackPanel.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });

                // Credentials (güvenlik için password gizli)
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "👤 Credentials",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                stackPanel.Children.Add(new TextBlock { Text = $"Username: {driverModel.Credentials.Username}", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Password: {(string.IsNullOrEmpty(driverModel.Credentials.Password) ? "Not Set" : "***")}", Margin = new Thickness(10, 3, 0, 3) });

                stackPanel.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });

                // Custom Settings (JSON)
                if (driverModel.CustomSettings.Any())
                {
                    stackPanel.Children.Add(new TextBlock
                    {
                        Text = "⚙️ Custom Settings (JSON)",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 0, 0, 10)
                    });

                    var jsonTextBox = new TextBox
                    {
                        Text = System.Text.Json.JsonSerializer.Serialize(driverModel.CustomSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Height = 150,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Background = Brushes.LightGray
                    };

                    stackPanel.Children.Add(jsonTextBox);
                }

                // Statistics
                stackPanel.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "📊 Statistics",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                var totalChannels = driverModel.Channels.Count;
                var totalDevices = driverModel.Channels.SelectMany(c => c.Devices).Count();
                var totalTags = driverModel.Channels.SelectMany(c => c.Devices).SelectMany(d => d.Tags).Count();

                stackPanel.Children.Add(new TextBlock { Text = $"Total Channels: {totalChannels}", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Total Devices: {totalDevices}", Margin = new Thickness(10, 3, 0, 3) });
                stackPanel.Children.Add(new TextBlock { Text = $"Total Configured Tags: {totalTags}", Margin = new Thickness(10, 3, 0, 3) });

                // Close button
                var closeButton = new Button
                {
                    Content = "Close",
                    Width = 80,
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 20, 0, 0)
                };

                closeButton.Click += (s, e) => configWindow.Close();
                stackPanel.Children.Add(closeButton);

                configWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing driver config: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async Task<bool> CheckOpcServiceStatusAsync()
        {
            try
            {
                // Son 1 dakikada tag yazılmış mı kontrol et
                const string sql = @"
            SELECT COUNT(*) 
            FROM kbindb._tagoku 
            WHERE readTime > DATE_SUB(NOW(), INTERVAL 1 MINUTE)";

                var recentTagCount = await _dbManager.QueryFirstKbinAsync<int>(sql);

                // Son 1 dakikada en az 1 tag yazılmışsa servis çalışıyor demektir
                return recentTagCount > 0;
            }
            catch
            {
                return false;
            }
        }
        private void LoadSettings()
        {
            try
            {
                _settings = SettingsManager.LoadSettings();

                if (!SettingsManager.SettingsExist() ||
                    string.IsNullOrEmpty(_settings.Database.ExchangerServer) ||
                    string.IsNullOrEmpty(_settings.Database.KbinServer))
                {
                    MessageBox.Show("Hoş geldiniz! İlk olarak veritabanı ayarlarını yapılandırmanız gerekiyor.",
                        "Koru1000 - İlk Kurulum", MessageBoxButton.OK, MessageBoxImage.Information);

                    ShowSettingsWindow();
                }
                else
                {
                    InitializeDatabase();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ayarlar yüklenirken hata oluştu: {ex.Message}\nAyarlar penceresi açılacak.",
                    "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowSettingsWindow();
            }
        }

        private void ShowSettingsWindow()
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings ?? new AppSettings());
                settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                bool? result = settingsWindow.ShowDialog();

                if (settingsWindow.SettingsSaved)
                {
                    _settings = settingsWindow.Settings;
                    InitializeDatabase();
                    MessageBox.Show("Ayarlar başarıyla kaydedildi!", "Başarılı",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    if (_dbManager == null)
                    {
                        MessageBox.Show("Veritabanı ayarları yapılandırılmadan uygulama çalışamaz. Uygulama kapatılacak.",
                            "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Application.Current.Shutdown();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ayarlar penceresi açılırken hata: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                if (_settings?.Database != null)
                {
                    _dbManager = DatabaseManager.DatabaseManager.Instance(
                        _settings.Database.GetExchangerConnectionString(),
                        _settings.Database.GetKbinConnectionString());

                    // Repository'leri initialize et
                    _channelDeviceRepo = new ChannelDeviceRepository(_dbManager);
                    _channelTypesRepo = new ChannelTypesRepository(_dbManager);
                    _deviceTypeRepo = new DeviceTypeRepository(_dbManager);
                    _tagRepo = new TagRepository(_dbManager);

                    // YENİ: System hierarchy service'i initialize et
                    _systemHierarchyService = new SystemHierarchyService(_dbManager);
                    _hierarchyService = new HierarchyService(_systemHierarchyService);

                    StatusText.Text = "Veritabanı yapılandırıldı - Otomatik bağlanılıyor...";

                    // Otomatik bağlantı dene
                    _ = Task.Run(async () =>
                    {
                        await AutoConnectAsync();
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Veritabanı yapılandırma hatası: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Veritabanı yapılandırma hatası";
            }
        }


        private async Task AutoConnectAsync()
        {
            try
            {
                bool exchangerConnected = await _dbManager.TestExchangerConnectionAsync();

                if (exchangerConnected)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        ConnectionStatus.Fill = Brushes.Green;
                        ConnectionText.Text = "Bağlı";
                        StatusText.Text = "Otomatik olarak bağlanıldı - Hiyerarşi yükleniyor...";

                        // Hiyerarşiyi yükle
                        await LoadHierarchy();

                        // Dashboard stats'ları da yükle
                        await LoadDashboardStats();
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusText.Text = "Veritabanı yapılandırıldı - Manuel bağlantı gerekli";
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"Otomatik bağlantı başarısız: {ex.Message}";
                });
            }
        }

        #region Menü Event'leri
        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsWindow();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RefreshButton_Click(sender, e);
        }

        private void ExpandAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExpandCollapseAll(HierarchyTreeView.ItemsSource as System.Collections.IEnumerable, true);
        }

        private void CollapseAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExpandCollapseAll(HierarchyTreeView.ItemsSource as System.Collections.IEnumerable, false);
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Koru1000 Database Manager\nVersion 1.0\n\nEndüstriyel veri toplama ve yönetim sistemi\n\nLazy Loading ile performanslı hiyerarşi görüntüleme",
                "Hakkında", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Hiyerarşi Event'leri

        /// <summary>
        /// TreeView item genişletildiğinde çağrılır - Lazy loading için kritik
        /// </summary>
        private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem treeViewItem && treeViewItem.DataContext is TreeNodeBase node)
            {
                if (!node.IsChildrenLoaded && _hierarchyService != null)
                {
                    try
                    {
                        StatusText.Text = $"Loading children for {node.Name}...";
                        Console.WriteLine($"=== EXPANDING NODE: {node.NodeType} - {node.Name} ===");

                        await _hierarchyService.LoadChildrenAsync(node);

                        StatusText.Text = $"Loaded {node.Children.Count} children for {node.Name}";
                        Console.WriteLine($"=== LOADED {node.Children.Count} CHILDREN FOR {node.Name} ===");

                        // Hierarchy stats'ı güncelle
                        UpdateHierarchyStats();
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"Error loading children: {ex.Message}";
                        Console.WriteLine($"=== ERROR LOADING CHILDREN: {ex.Message} ===");
                        MessageBox.Show($"Error loading children for {node.Name}: {ex.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void HierarchyTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNodeBase selectedNode)
            {
                ShowNodeDetails(selectedNode);
            }
        }

        private void ExpandCollapseAll(System.Collections.IEnumerable items, bool expand)
        {
            if (items == null) return;

            foreach (TreeNodeBase item in items)
            {
                item.IsExpanded = expand;
                if (item.Children.Any())
                {
                    ExpandCollapseAll(item.Children, expand);
                }
            }
        }

        private void ShowNodeDetails(TreeNodeBase node)
        {
            if (DetailsPanel == null || NoSelectionText == null) return;

            DetailsPanel.Children.Clear();
            NoSelectionText.Visibility = Visibility.Collapsed;

            switch (node)
            {
                case DriverNode driver:
                    ShowDriverDetails(driver);
                    break;
                case ChannelNode channel:
                    ShowChannelDetails(channel);
                    break;
                case DeviceNode device:
                    ShowDeviceDetails(device);
                    break;
                case TagNode tag:
                    ShowTagDetails(tag);
                    break;
                case DummyNode dummy:
                    ShowDummyDetails(dummy);
                    break;
            }

            if (DetailsTab != null)
                DetailsTab.IsSelected = true;
        }

        private void ShowDriverDetails(DriverNode driver)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"🔌 Driver: {driver.Name}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {driver.DriverTypeName ?? "Driver Instance"}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"ID: {driver.Id}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Children: {driver.Children.Count}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Children Loaded: {(driver.IsChildrenLoaded ? "Yes" : "No")}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Is Loading: {(driver.IsLoading ? "Yes" : "No")}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Connected: {(driver.IsConnected ? "Yes" : "No")}" });
        }

        private void ShowChannelDetails(ChannelNode channel)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"📂 Channel: {channel.Name}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {channel.ChannelTypeName ?? "Channel Instance"}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"ID: {channel.Id}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Channel Type ID: {channel.ChannelTypeId}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Children: {channel.Children.Count}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Children Loaded: {(channel.IsChildrenLoaded ? "Yes" : "No")}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Is Loading: {(channel.IsLoading ? "Yes" : "No")}" });
        }

        // ShowDeviceDetails metodunu düzeltilmiş haliyle değiştirin


        private async void ShowDeviceDetails(DeviceNode device)
        {
            // Timer'ı durdur
            _tagRefreshTimer?.Dispose();
            _currentSelectedDevice = device;

            DetailsPanel.Children.Clear();
            NoSelectionText.Visibility = Visibility.Collapsed;

            // Device Info
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"🔧 Device: {device.Name}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            DetailsPanel.Children.Add(new TextBlock { Text = $"Status: {device.StatusDescription} {GetDeviceStatusIcon(device.StatusCode)}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {device.DeviceTypeName}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Device ID: {device.Id}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Last Update: {device.LastUpdateTime}" });

            // Real-time Tag Container
            var realTimeContainer = new Border
            {
                BorderBrush = Brushes.Blue,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 10, 0, 10),
                Padding = new Thickness(10)
            };

            var realTimePanel = new StackPanel();
            realTimeContainer.Child = realTimePanel;

            realTimePanel.Children.Add(new TextBlock
            {
                Text = "📡 Real-Time Tag Values (Auto-Refresh)",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var tagListContainer = new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var tagListPanel = new StackPanel { Name = "RealTimeTagPanel" };
            tagListContainer.Content = tagListPanel;
            realTimePanel.Children.Add(tagListContainer);

            DetailsPanel.Children.Add(realTimeContainer);

            // Control Panel
            var controlPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };

            var pauseButton = new Button
            {
                Content = "⏸️ Pause Refresh",
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(10, 5, 10, 5)
            };

            var loadAllButton = new Button
            {
                Content = "📋 Load All Tags",
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(10, 5, 10, 5)
            };

            controlPanel.Children.Add(pauseButton);
            controlPanel.Children.Add(loadAllButton);
            DetailsPanel.Children.Add(controlPanel);

            // İlk yükleme
            await RefreshRealTimeTagsAsync(device, tagListPanel);

            // Timer başlat - Her 2 saniyede bir refresh
            _tagRefreshTimer = new Timer(async _ =>
            {
                if (_currentSelectedDevice == device) // Hala aynı device seçili mi?
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await RefreshRealTimeTagsAsync(device, tagListPanel);
                    });
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            // Pause butonu
            bool isPaused = false;
            pauseButton.Click += (s, e) =>
            {
                isPaused = !isPaused;
                if (isPaused)
                {
                    _tagRefreshTimer?.Dispose();
                    pauseButton.Content = "▶️ Resume Refresh";
                }
                else
                {
                    _tagRefreshTimer = new Timer(async _ =>
                    {
                        if (_currentSelectedDevice == device)
                        {
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                await RefreshRealTimeTagsAsync(device, tagListPanel);
                            });
                        }
                    }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                    pauseButton.Content = "⏸️ Pause Refresh";
                }
            };

            // Load All butonu (debug amaçlı)
            loadAllButton.Click += async (s, e) =>
            {
                try
                {
                    loadAllButton.IsEnabled = false;
                    loadAllButton.Content = "Loading...";

                    var tags = await _systemHierarchyService.LoadTagsForDeviceAsync(device.Id, device.DeviceTypeId);
                    MessageBox.Show($"Device has {tags.Count} total tags configured.", "Tag Count");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error: {ex.Message}", "Error");
                }
                finally
                {
                    loadAllButton.IsEnabled = true;
                    loadAllButton.Content = "📋 Load All Tags";
                }
            };

            if (DetailsTab != null)
                DetailsTab.IsSelected = true;
        }

        private async Task RefreshRealTimeTagsAsync(DeviceNode device, StackPanel tagListPanel)
        {
            try
            {
                // Son 1 saatte güncellenen tag'leri getir (OPC servisi tarafından yazılanlar)
                const string sql = @"
            SELECT 
                t.tagName,
                t.tagValue,
                t.readTime,
                TIMESTAMPDIFF(SECOND, t.readTime, NOW()) as secondsAgo
            FROM kbindb._tagoku t
            WHERE t.devId = @DeviceId 
            AND t.readTime > DATE_SUB(NOW(), INTERVAL 1 HOUR)
            ORDER BY t.readTime DESC
            LIMIT 20";

                var results = await _dbManager.QueryKbinAsync<dynamic>(sql, new { DeviceId = device.Id });

                tagListPanel.Children.Clear();

                if (!results.Any())
                {
                    tagListPanel.Children.Add(new TextBlock
                    {
                        Text = "❌ No recent tag data found. Is OPC Service running?",
                        Foreground = Brushes.Red,
                        FontWeight = FontWeights.Bold
                    });
                    return;
                }

                foreach (var tag in results)
                {
                    var secondsAgo = (int)tag.secondsAgo;
                    var timeColor = secondsAgo < 10 ? Brushes.Green :
                                   secondsAgo < 60 ? Brushes.Orange : Brushes.Red;

                    var tagDisplay = new Border
                    {
                        BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 1, 0, 1),
                        Padding = new Thickness(5)
                    };

                    var tagInfo = new TextBlock
                    {
                        Text = $"🏷️ {tag.tagName} = {tag.tagValue} ({secondsAgo}s ago)",
                        Foreground = timeColor,
                        FontSize = 11
                    };

                    tagDisplay.Child = tagInfo;
                    tagListPanel.Children.Add(tagDisplay);
                }

                // Summary
                tagListPanel.Children.Add(new TextBlock
                {
                    Text = $"✅ {results.Count()} active tags | Last refresh: {DateTime.Now:HH:mm:ss}",
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Blue,
                    Margin = new Thickness(0, 5, 0, 0),
                    FontSize = 10
                });
            }
            catch (Exception ex)
            {
                tagListPanel.Children.Clear();
                tagListPanel.Children.Add(new TextBlock
                {
                    Text = $"❌ Error refreshing tags: {ex.Message}",
                    Foreground = Brushes.Red
                });
            }
        }

        // Driver Context Menu Events
        private void StartDriverMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HierarchyTreeView.SelectedItem is DriverNode driver && string.IsNullOrEmpty(driver.DriverTypeName))
            {
                StartDriverAsync(driver.Id, driver.Name);
            }
        }

        private void StopDriverMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HierarchyTreeView.SelectedItem is DriverNode driver && string.IsNullOrEmpty(driver.DriverTypeName))
            {
                StopDriverAsync(driver.Id, driver.Name);
            }
        }

        private async void StartDriverAsync(int driverId, string driverName)
        {
            try
            {
                StatusText.Text = $"Starting driver {driverName}...";

                // Bu kısmı OPC Service API'si hazır olduğunda implement edeceğiz
                // Şimdilik basit bir simülasyon
                await Task.Delay(1000);

                MessageBox.Show($"Driver {driverName} start command sent.\nCheck OPC Service logs for status.",
                    "Driver Start", MessageBoxButton.OK, MessageBoxImage.Information);

                StatusText.Text = $"Driver {driverName} start command sent";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start driver: {ex.Message}", "Error");
                StatusText.Text = "Driver start failed";
            }
        }

        private async void StopDriverAsync(int driverId, string driverName)
        {
            try
            {
                StatusText.Text = $"Stopping driver {driverName}...";

                // Bu kısmı OPC Service API'si hazır olduğunda implement edeceğiz
                await Task.Delay(1000);

                MessageBox.Show($"Driver {driverName} stop command sent.\nCheck OPC Service logs for status.",
                    "Driver Stop", MessageBoxButton.OK, MessageBoxImage.Information);

                StatusText.Text = $"Driver {driverName} stop command sent";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop driver: {ex.Message}", "Error");
                StatusText.Text = "Driver stop failed";
            }
        }



        // Helper methods - bunları MainWindow class'ına ekleyin
        private async Task<(int typeTagCount, int individualTagCount)> GetTagCountsAsync(int deviceId, int deviceTypeId)
        {
            var typeTagTask = _dbManager.QueryFirstExchangerAsync<int>(
                "SELECT COUNT(*) FROM devicetypetag WHERE deviceTypeId = @DeviceTypeId",
                new { DeviceTypeId = deviceTypeId });

            var individualTagTask = _dbManager.QueryFirstExchangerAsync<int>(
                "SELECT COUNT(*) FROM deviceindividualtag WHERE channelDeviceId = @DeviceId",
                new { DeviceId = deviceId });

            await Task.WhenAll(typeTagTask, individualTagTask);

            return (await typeTagTask, await individualTagTask);
        }

        private async Task<List<TagModel>> LoadRecentTagsForDeviceAsync(int deviceId, int deviceTypeId, int limit)
        {
            // Sadece recent value'su olan tag'leri getir - HIZLI
            const string sql = @"
        SELECT DISTINCT 
            0 as TagId,
            t.tagName as TagName,
            t.tagValue,
            t.readTime,
            'Unknown' as DataType,
            0 as IsWritable
        FROM kbindb._tagoku t
        WHERE t.devId = @DeviceId 
        AND t.readTime > DATE_SUB(NOW(), INTERVAL 1 HOUR)
        ORDER BY t.readTime DESC
        LIMIT @Limit";

            var results = await _dbManager.QueryKbinAsync<dynamic>(sql,
                new { DeviceId = deviceId, Limit = limit });

            var tags = new List<TagModel>();
            foreach (var result in results)
            {
                tags.Add(new TagModel
                {
                    Id = 0,
                    Name = result.TagName?.ToString() ?? "Unknown",
                    CurrentValue = result.tagValue,
                    DataType = "Unknown",
                    Quality = "Good",
                    LastReadTime = result.readTime,
                    IsWritable = false
                });
            }

            return tags;
        }

        private async Task<List<TagModel>> LoadAllTagsForDeviceAsync(int deviceId, int deviceTypeId)
        {
            // Bu yavaş ama eksiksiz - sadece istendiğinde kullan
            return await _systemHierarchyService.LoadTagsForDeviceAsync(deviceId, deviceTypeId);
        }

        private string GetDeviceStatusIcon(byte statusCode)
        {
            return statusCode switch
            {
                11 or 31 or 41 or 61 => "🟢", // Active states
                51 => "🟡", // Disabled
                _ => "🔴" // Error or unknown
            };
        }

        private string GetQualityIcon(string quality)
        {
            return quality?.ToLower() switch
            {
                "good" => "🟢",
                "uncertain" => "🟡",
                "bad" => "🔴",
                _ => "❓"
            };
        }

        private void ShowTagDetails(TagNode tag)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"🏷️ Tag: {tag.Name}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Channel type bilgisi varsa göster
            if (tag.Parent is DeviceNode deviceParent && !string.IsNullOrEmpty(deviceParent.ChannelTypeName))
            {
                DetailsPanel.Children.Add(new TextBlock { Text = $"Channel Type: {deviceParent.ChannelTypeName}" });
            }

            DetailsPanel.Children.Add(new TextBlock { Text = $"Address: {tag.TagAddress}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Data Type: {tag.DataType}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Current Value: {tag.CurrentValue ?? "N/A"}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Quality: {tag.Quality} {tag.QualityIcon}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Writable: {(tag.IsWritable ? "Yes" : "No")}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Last Read: {tag.LastReadTime}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Tag ID: {tag.Id}" });
        }

        private void ShowDummyDetails(DummyNode dummy)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = "⏳ Loading Node",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            DetailsPanel.Children.Add(new TextBlock { Text = "This is a placeholder node indicating that child items are being loaded." });
            DetailsPanel.Children.Add(new TextBlock { Text = "Please expand the parent node to load actual children." });
        }

        private async Task LoadHierarchy()
        {
            try
            {
                if (_hierarchyService != null)
                {
                    StatusText.Text = "Loading complete hierarchy...";
                    Console.WriteLine("=== LOADING COMPLETE HIERARCHY ===");

                    var startTime = DateTime.Now;
                    var hierarchy = await _hierarchyService.BuildHierarchyAsync();
                    var loadTime = DateTime.Now - startTime;

                    Console.WriteLine($"=== HIERARCHY LOADED in {loadTime.TotalMilliseconds:F0}ms: {hierarchy?.Count ?? 0} root items ===");

                    // UI Thread'de güncelleyelim
                    Dispatcher.Invoke(() =>
                    {
                        if (hierarchy != null && hierarchy.Any())
                        {
                            HierarchyTreeView.ItemsSource = hierarchy;
                            StatusText.Text = $"Complete hierarchy loaded in {loadTime.TotalMilliseconds:F0}ms - {hierarchy.Count} driver types";

                            // İstatistikleri güncelle
                            UpdateHierarchyStats();
                        }
                        else
                        {
                            StatusText.Text = "No hierarchy data found";
                        }
                    });
                }
                else
                {
                    StatusText.Text = "HierarchyService is null!";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load hierarchy: {ex.Message}";
                Console.WriteLine($"HIERARCHY ERROR: {ex.Message}");
                throw;
            }
        }

        // Refresh için cache'i temizle
        private async void RefreshHierarchyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Refreshing hierarchy...";
                _systemHierarchyService?.ClearCache();
                await LoadHierarchy();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hierarchy refresh failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void UpdateHierarchyStats()
        {
            try
            {
                if (HierarchyTreeView.ItemsSource is ObservableCollection<TreeNodeBase> rootNodes)
                {
                    var systemHierarchy = _systemHierarchyService?.CurrentHierarchy;
                    if (systemHierarchy != null)
                    {
                        if (HierarchyStatsText != null)
                        {
                            HierarchyStatsText.Text = $"🌳 System Hierarchy Statistics:\n" +
                                                     $"   • Driver Types: {systemHierarchy.DriverTypes.Count}\n" +
                                                     $"   • Total Drivers: {systemHierarchy.Statistics.GetValueOrDefault("TotalDrivers", 0)}\n" +
                                                     $"   • Total Channels: {systemHierarchy.Statistics.GetValueOrDefault("TotalChannels", 0)}\n" +
                                                     $"   • Total Devices: {systemHierarchy.TotalDevices:N0}\n" +
                                                     $"   • Total Tags: {systemHierarchy.TotalTags:N0}\n" +
                                                     $"   • Loaded At: {systemHierarchy.LoadedAt:HH:mm:ss}\n" +
                                                     $"   • Cache Status: Active";
                        }
                    }
                    else
                    {
                        int totalNodes = CountAllNodes(rootNodes);
                        if (HierarchyStatsText != null)
                        {
                            HierarchyStatsText.Text = $"🌳 Hierarchy Statistics:\n" +
                                                     $"   • Root Nodes: {rootNodes.Count}\n" +
                                                     $"   • Total Visible Nodes: {totalNodes}\n" +
                                                     $"   • Cache Status: Not Available\n" +
                                                     $"   • Last Update: {DateTime.Now:HH:mm:ss}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating hierarchy stats: {ex.Message}");
            }
        }

        private int CountAllNodes(IEnumerable<TreeNodeBase> nodes)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                count++;
                if (node.Children.Any() && !(node.Children.First() is DummyNode))
                {
                    count += CountAllNodes(node.Children);
                }
            }
            return count;
        }

        private int CountLoadedNodes(IEnumerable<TreeNodeBase> nodes)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                if (node.IsChildrenLoaded) count++;
                if (node.Children.Any() && !(node.Children.First() is DummyNode))
                {
                    count += CountLoadedNodes(node.Children);
                }
            }
            return count;
        }

        private void TestHierarchyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("=== MANUAL TEST START ===");

                // Manuel test node'ları oluştur
                var testNodes = new ObservableCollection<TreeNodeBase>();

                var testDriverType = new DriverNode
                {
                    Id = 999,
                    Name = "Test Driver Type",
                    DisplayName = "🔌 Test Driver Type",
                    DriverTypeName = "Test",
                    IsExpanded = false,
                    IsChildrenLoaded = false
                };

                var testDriver = new DriverNode
                {
                    Id = 998,
                    Name = "Test Driver",
                    DisplayName = "🔧 Test Driver",
                    DriverTypeName = null,
                    Parent = testDriverType,
                    IsExpanded = false,
                    IsChildrenLoaded = false
                };

                var testChannel = new ChannelNode
                {
                    Id = 997,
                    Name = "Test Channel",
                    DisplayName = "📂 Test Channel",
                    ChannelTypeId = 1,
                    ChannelTypeName = "TestChannelType",
                    Parent = testDriver,
                    IsExpanded = false,
                    IsChildrenLoaded = false
                };

                var testDevice = new DeviceNode
                {
                    Id = 996,
                    Name = "Test Device",
                    DisplayName = "🔧 Test Device [Active]",
                    DeviceTypeId = 1,
                    DeviceTypeName = "TestDeviceType",
                    StatusCode = 11,
                    StatusDescription = "Active",
                    Parent = testChannel,
                    IsExpanded = false,
                    IsChildrenLoaded = false
                };

                var testTag = new TagNode
                {
                    Id = 995,
                    Name = "Test Tag",
                    DisplayName = "🏷️ Test Tag [Float] = 123.45",
                    TagAddress = "DB1.DBD0",
                    DataType = "Float",
                    CurrentValue = 123.45,
                    Quality = "Good",
                    IsWritable = true,
                    Parent = testDevice
                };

                // Dummy child'lar ekle
                testDriverType.AddDummyChild();
                testDriver.AddDummyChild();
                testChannel.AddDummyChild();
                testDevice.AddDummyChild();

                // Hierarchy'yi oluştur
                testDevice.Children.Add(testTag);
                testChannel.Children.Add(testDevice);
                testDriver.Children.Add(testChannel);
                testDriverType.Children.Add(testDriver);
                testNodes.Add(testDriverType);

                // TreeView'a set et
                HierarchyTreeView.ItemsSource = testNodes;

                Console.WriteLine($"Manual test nodes added: {testNodes.Count}");
                StatusText.Text = "Test hierarchy loaded - expand nodes to see structure";
                MessageBox.Show($"Test hierarchy loaded!\n\nYou can now expand the nodes to see the structure.\nThe dummy loading mechanism is also working.",
                    "Test Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Test error: {ex.Message}");
                MessageBox.Show($"Test error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Bağlantı ve Veri Yükleme
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dbManager == null)
            {
                MessageBox.Show("Önce veritabanı ayarlarını yapılandırın!", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowSettingsWindow();
                return;
            }

            try
            {
                StatusText.Text = "Testing connections...";
                ConnectButton.IsEnabled = false;

                bool exchangerConnected = await _dbManager.TestExchangerConnectionAsync();
                bool kbinConnected = await _dbManager.TestKbinConnectionAsync();

                if (exchangerConnected && kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Green;
                    ConnectionText.Text = "Connected";
                    StatusText.Text = "Successfully connected to databases";
                    await LoadAllData();
                }
                else if (exchangerConnected && !kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Orange;
                    ConnectionText.Text = "Partial";
                    StatusText.Text = "Exchanger connected, Kbin connection failed";
                    await LoadExchangerData();
                }
                else if (!exchangerConnected && kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Orange;
                    ConnectionText.Text = "Partial";
                    StatusText.Text = "Kbin connected, Exchanger connection failed";
                    await LoadKbinData();
                }
                else
                {
                    ConnectionStatus.Fill = Brushes.Red;
                    ConnectionText.Text = "Disconnected";
                    StatusText.Text = "All database connections failed";
                }
            }
            catch (Exception ex)
            {
                ConnectionStatus.Fill = Brushes.Red;
                ConnectionText.Text = "Disconnected";
                MessageBox.Show($"Connection error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Connection error";
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dbManager == null)
            {
                MessageBox.Show("Please establish database connection first!", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadAllData();
        }

        private async void RefreshTagsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadTagData();
        }

        private async Task LoadAllData()
        {
            try
            {
                StatusText.Text = "Loading all data...";
                RefreshButton.IsEnabled = false;

                var tasks = new List<Task>
                {
                    LoadExchangerData(),
                    LoadKbinData(),
                    LoadDashboardStats(),
                    LoadHierarchy()
                };

                await Task.WhenAll(tasks);

                StatusText.Text = "All data loaded successfully";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Data loading error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Data loading failed";
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private async Task LoadExchangerData()
        {
            try
            {
                if (_channelDeviceRepo != null && ChannelDeviceGrid != null)
                {
                    var devices = await _channelDeviceRepo.GetAllAsync();
                    ChannelDeviceGrid.ItemsSource = devices;
                }

                if (_channelTypesRepo != null && ChannelTypesGrid != null)
                {
                    var channelTypes = await _channelTypesRepo.GetAllAsync();
                    ChannelTypesGrid.ItemsSource = channelTypes;
                }

                if (_deviceTypeRepo != null && DeviceTypeGrid != null)
                {
                    var deviceTypes = await _deviceTypeRepo.GetAllAsync();
                    DeviceTypeGrid.ItemsSource = deviceTypes;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Exchanger data loading failed: {ex.Message}";
            }
        }

        private async Task LoadKbinData()
        {
            await LoadTagData();
        }

        private async Task LoadTagData()
        {
            try
            {
                int limit = 500;
                if (TagLimitComboBox?.SelectedItem is ComboBoxItem selectedItem)
                {
                    int.TryParse(selectedItem.Content.ToString(), out limit);
                }

                if (_tagRepo != null)
                {
                    var tagReadData = await _tagRepo.GetLatestTagValuesAsync(null, limit);
                    if (TagOkuGrid != null)
                        TagOkuGrid.ItemsSource = tagReadData;

                    var tagWriteData = await _tagRepo.GetPendingWriteTagsAsync(null, limit);
                    if (TagYazGrid != null)
                        TagYazGrid.ItemsSource = tagWriteData;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Tag data loading failed: {ex.Message}";
            }
        }

        private async Task LoadDashboardStats()
        {
            try
            {
                var deviceCount = await _channelDeviceRepo.GetCountAsync();
                var channelTypeCount = await _channelTypesRepo.GetCountAsync();
                var deviceTypeCount = await _deviceTypeRepo.GetCountAsync();
                var tagOkuCount = await _tagRepo.GetTagOkuCountAsync();
                var tagYazCount = await _tagRepo.GetTagYazCountAsync();
                var statusCounts = await _channelDeviceRepo.GetStatusCodeCountsAsync();

                TotalDevicesText.Text = $"📊 Total Device Count: {deviceCount:N0}";

                string statusStats = "📈 Status Code Distribution:\n";
                foreach (var stat in statusCounts)
                {
                    statusStats += $"   • Status {stat.Key}: {stat.Value:N0} devices\n";
                }
                StatusStatsText.Text = statusStats;

                TagStatsText.Text = $"🏷️ Tag Statistics:\n" +
                                   $"   • Read Tags: {tagOkuCount:N0}\n" +
                                   $"   • Write Tags: {tagYazCount:N0}";

                SystemStatsText.Text = $"⚙️ System Statistics:\n" +
                                      $"   • Channel Types: {channelTypeCount:N0}\n" +
                                      $"   • Device Types: {deviceTypeCount:N0}\n" +
                                      $"   • Last Update: {DateTime.Now:HH:mm:ss}";

                // Hierarchy stats'ı da güncelle
                UpdateHierarchyStats();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Statistics loading failed: {ex.Message}";
            }
        }
        #endregion

        #region Window Event'leri
        protected override void OnClosed(EventArgs e)
        {
            _statusTimer?.Stop();
            _tagRefreshTimer?.Dispose();
            base.OnClosed(e);
        }
        #endregion
    }
}