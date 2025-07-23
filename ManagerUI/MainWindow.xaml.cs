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

                    StatusText.Text = "Veritabanı yapılandırıldı - Bağlanmak için 'Bağlan' butonuna tıklayın";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Veritabanı yapılandırma hatası: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Veritabanı yapılandırma hatası";
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
            MessageBox.Show("Koru1000 Database Manager\nVersion 1.0\n\nEndüstriyel veri toplama ve yönetim sistemi",
                "Hakkında", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Hiyerarşi Event'leri
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
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {driver.DriverTypeName}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Channels: {driver.Children.Count}" });
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
            DetailsPanel.Children.Add(new TextBlock { Text = $"Type: {channel.ChannelTypeName}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Devices: {channel.Children.Count}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Channel Type ID: {channel.ChannelTypeId}" });
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
            DetailsPanel.Children.Add(new TextBlock { Text = $"Tags: {device.Children.Count}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Last Update: {device.LastUpdateTime}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Device ID: {device.Id}" });
            DetailsPanel.Children.Add(new TextBlock { Text = $"Status Code: {device.StatusCode}" });
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
        }

        private async Task LoadHierarchy()
        {
            try
            {
                if (_hierarchyService != null)
                {
                    StatusText.Text = "Hiyerarşi yükleniyor...";

                    Console.WriteLine("=== HIERARCHY LOADING START ===");
                    var hierarchy = await _hierarchyService.BuildHierarchyAsync();
                    Console.WriteLine($"=== HIERARCHY LOADED: {hierarchy?.Count ?? 0} items ===");

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

                            StatusText.Text = $"Hiyerarşi yüklendi - {hierarchy.Count} öğe";

                            // TreeView'ı refresh et
                            HierarchyTreeView.UpdateLayout();
                        }
                        else
                        {
                            StatusText.Text = "Hiyerarşi boş - veri bulunamadı";
                        }
                    });
                }
                else
                {
                    StatusText.Text = "HierarchyService null!";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Hiyerarşi yüklenemedi: {ex.Message}";
                Console.WriteLine($"HIERARCHY ERROR: {ex.Message}");

                MessageBox.Show($"Hiyerarşi yükleme hatası: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void TestHierarchyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("=== MANUAL TEST START ===");

                // Manuel test node'ları oluştur
                var testNodes = new ObservableCollection<TreeNodeBase>();

                var testChannel = new ChannelNode
                {
                    Id = 1,
                    Name = "Test Channel",
                    DisplayName = "Test Channel Manual",
                    Icon = "📂",
                    IsExpanded = true
                };

                var testDevice = new DeviceNode
                {
                    Id = 1,
                    Name = "Test Device",
                    DisplayName = "Test Device Manual",
                    Icon = "🔧",
                    Parent = testChannel,
                    IsExpanded = true
                };

                var testTag = new TagNode
                {
                    Id = 1,
                    Name = "Test Tag",
                    DisplayName = "Test Tag Manual",
                    Icon = "🏷️",
                    Parent = testDevice
                };

                testDevice.Children.Add(testTag);
                testChannel.Children.Add(testDevice);
                testNodes.Add(testChannel);

                HierarchyTreeView.ItemsSource = testNodes;

                Console.WriteLine($"Manual test nodes added: {testNodes.Count}");
                MessageBox.Show($"Test node'ları eklendi! TreeView'da görünüyor mu?", "Test",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Test error: {ex.Message}");
                MessageBox.Show($"Test hatası: {ex.Message}", "Hata",
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
                StatusText.Text = "Bağlantı test ediliyor...";
                ConnectButton.IsEnabled = false;

                bool exchangerConnected = await _dbManager.TestExchangerConnectionAsync();
                bool kbinConnected = await _dbManager.TestKbinConnectionAsync();

                if (exchangerConnected && kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Green;
                    ConnectionText.Text = "Bağlı";
                    StatusText.Text = "Veritabanlarına başarıyla bağlanıldı";
                    await LoadAllData();
                }
                else if (exchangerConnected && !kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Orange;
                    ConnectionText.Text = "Kısmi Bağlı";
                    StatusText.Text = "Exchanger bağlı, Kbin bağlantısı başarısız";
                    await LoadExchangerData();
                }
                else if (!exchangerConnected && kbinConnected)
                {
                    ConnectionStatus.Fill = Brushes.Orange;
                    ConnectionText.Text = "Kısmi Bağlı";
                    StatusText.Text = "Kbin bağlı, Exchanger bağlantısı başarısız";
                    await LoadKbinData();
                }
                else
                {
                    ConnectionStatus.Fill = Brushes.Red;
                    ConnectionText.Text = "Bağlı Değil";
                    StatusText.Text = "Tüm veritabanı bağlantıları başarısız";
                }
            }
            catch (Exception ex)
            {
                ConnectionStatus.Fill = Brushes.Red;
                ConnectionText.Text = "Bağlı Değil";
                MessageBox.Show($"Bağlantı hatası: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Bağlantı hatası";
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
                MessageBox.Show("Önce veritabanı bağlantısını kurun!", "Uyarı",
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
                StatusText.Text = "Tüm veriler yükleniyor...";
                RefreshButton.IsEnabled = false;

                var tasks = new List<Task>
                {
                    LoadExchangerData(),
                    LoadKbinData(),
                    LoadDashboardStats(),
                    LoadHierarchy()
                };

                await Task.WhenAll(tasks);

                StatusText.Text = "Tüm veriler başarıyla yüklendi";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Veri yükleme hatası: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Veri yükleme başarısız";
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
                StatusText.Text = $"Exchanger verileri yüklenemedi: {ex.Message}";
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
                StatusText.Text = $"Tag verileri yüklenemedi: {ex.Message}";
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

                TotalDevicesText.Text = $"📊 Toplam Cihaz Sayısı: {deviceCount}";

                string statusStats = "📈 Status Kod Dağılımı:\n";
                foreach (var stat in statusCounts)
                {
                    statusStats += $"   • Status {stat.Key}: {stat.Value} cihaz\n";
                }
                StatusStatsText.Text = statusStats;

                TagStatsText.Text = $"🏷️ Tag İstatistikleri:\n" +
                                   $"   • Okunan Tag Sayısı: {tagOkuCount:N0}\n" +
                                   $"   • Yazılacak Tag Sayısı: {tagYazCount:N0}";

                SystemStatsText.Text = $"⚙️ Sistem İstatistikleri:\n" +
                                      $"   • Channel Type Sayısı: {channelTypeCount}\n" +
                                      $"   • Device Type Sayısı: {deviceTypeCount}\n" +
                                      $"   • Son Güncelleme: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"İstatistikler yüklenemedi: {ex.Message}";
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