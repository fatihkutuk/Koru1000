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

namespace Koru1000.ManagerUI
{
    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

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

                    _channelDeviceRepo = new ChannelDeviceRepository(_dbManager);
                    _channelTypesRepo = new ChannelTypesRepository(_dbManager);
                    _deviceTypeRepo = new DeviceTypeRepository(_dbManager);
                    _tagRepo = new TagRepository(_dbManager);
                    _hierarchyService = new HierarchyService(_dbManager);

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

        private async void RefreshHierarchyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await LoadHierarchy();
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

        private void ShowDeviceDetails(DeviceNode device)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"🔧 Device: {device.Name}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Status: {device.StatusDescription} {device.StatusIcon}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {device.DeviceTypeName}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Device ID: {device.Id}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Device Type ID: {device.DeviceTypeId}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Status Code: {device.StatusCode}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Tags: {device.Children.Count}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Tags Loaded: {(device.IsChildrenLoaded ? "Yes" : "No")}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Last Update: {device.LastUpdateTime}" });
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
                    StatusText.Text = "Loading hierarchy (lazy loading)...";
                    Console.WriteLine("=== LAZY HIERARCHY LOADING START ===");

                    var hierarchy = await _hierarchyService.BuildHierarchyAsync();
                    Console.WriteLine($"=== HIERARCHY LOADED: {hierarchy?.Count ?? 0} root items ===");

                    // UI Thread'de güncelleyelim
                    Dispatcher.Invoke(() =>
                    {
                        if (hierarchy != null && hierarchy.Any())
                        {
                            // Önce temizle
                            HierarchyTreeView.ItemsSource = null;

                            // Sonra set et
                            HierarchyTreeView.ItemsSource = hierarchy;

                            Console.WriteLine($"TreeView ItemsSource set with {hierarchy.Count} items");

                            StatusText.Text = $"Hierarchy loaded - {hierarchy.Count} root items (expand nodes to load children)";

                            // TreeView'ı refresh et
                            HierarchyTreeView.UpdateLayout();

                            // Hierarchy stats güncelle
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

        private void UpdateHierarchyStats()
        {
            try
            {
                if (HierarchyTreeView.ItemsSource is ObservableCollection<TreeNodeBase> rootNodes)
                {
                    int totalNodes = CountAllNodes(rootNodes);
                    int loadedNodes = CountLoadedNodes(rootNodes);

                    if (HierarchyStatsText != null)
                    {
                        HierarchyStatsText.Text = $"🌳 Hierarchy Statistics:\n" +
                                                 $"   • Root Nodes: {rootNodes.Count}\n" +
                                                 $"   • Total Visible Nodes: {totalNodes}\n" +
                                                 $"   • Loaded Node Types: {loadedNodes}\n" +
                                                 $"   • Lazy Loading: Active\n" +
                                                 $"   • Last Update: {DateTime.Now:HH:mm:ss}";
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
            base.OnClosed(e);
        }
        #endregion
    }
}