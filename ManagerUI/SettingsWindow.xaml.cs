using System.Windows;
using Koru1000.Core.Models;
using Koru1000.Shared;
using MySql.Data.MySqlClient;

namespace Koru1000.ManagerUI
{
    public partial class SettingsWindow : Window  
    {
        public AppSettings Settings { get; private set; }
        public bool SettingsSaved { get; private set; }

        public SettingsWindow(AppSettings currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings;
            LoadSettingsToUI();
        }

        private void LoadSettingsToUI()
        {
            // Exchanger Database
            ExchangerServerTextBox.Text = Settings.Database.ExchangerServer;
            ExchangerPortTextBox.Text = Settings.Database.ExchangerPort.ToString();
            ExchangerDatabaseTextBox.Text = Settings.Database.ExchangerDatabase;
            ExchangerUsernameTextBox.Text = Settings.Database.ExchangerUsername;
            ExchangerPasswordBox.Password = Settings.Database.ExchangerPassword;

            // Kbin Database
            KbinServerTextBox.Text = Settings.Database.KbinServer;
            KbinPortTextBox.Text = Settings.Database.KbinPort.ToString();
            KbinDatabaseTextBox.Text = Settings.Database.KbinDatabase;
            KbinUsernameTextBox.Text = Settings.Database.KbinUsername;
            KbinPasswordBox.Password = Settings.Database.KbinPassword;
        }

        private void SaveSettingsFromUI()
        {
            Settings.Database.ExchangerServer = ExchangerServerTextBox.Text;
            Settings.Database.ExchangerPort = int.TryParse(ExchangerPortTextBox.Text, out int exPort) ? exPort : 3306;
            Settings.Database.ExchangerDatabase = ExchangerDatabaseTextBox.Text;
            Settings.Database.ExchangerUsername = ExchangerUsernameTextBox.Text;
            Settings.Database.ExchangerPassword = ExchangerPasswordBox.Password;

            Settings.Database.KbinServer = KbinServerTextBox.Text;
            Settings.Database.KbinPort = int.TryParse(KbinPortTextBox.Text, out int kbPort) ? kbPort : 3306;
            Settings.Database.KbinDatabase = KbinDatabaseTextBox.Text;
            Settings.Database.KbinUsername = KbinUsernameTextBox.Text;
            Settings.Database.KbinPassword = KbinPasswordBox.Password;
        }

        private async void TestExchangerButton_Click(object sender, RoutedEventArgs e)
        {
            await TestConnectionAsync(true);
        }

        private async void TestKbinButton_Click(object sender, RoutedEventArgs e)
        {
            await TestConnectionAsync(false);
        }

        private async Task TestConnectionAsync(bool isExchanger)
        {
            try
            {
                string server = isExchanger ? ExchangerServerTextBox.Text : KbinServerTextBox.Text;
                string port = isExchanger ? ExchangerPortTextBox.Text : KbinPortTextBox.Text;
                string database = isExchanger ? ExchangerDatabaseTextBox.Text : KbinDatabaseTextBox.Text;
                string username = isExchanger ? ExchangerUsernameTextBox.Text : KbinUsernameTextBox.Text;
                string password = isExchanger ? ExchangerPasswordBox.Password : KbinPasswordBox.Password;

                string connectionString = $"Server={server};Port={port};Database={database};Uid={username};Pwd={password};";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                MessageBox.Show($"{(isExchanger ? "Exchanger" : "Kbin")} veritabanına başarıyla bağlanıldı!",
                    "Bağlantı Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bağlantı hatası: {ex.Message}",
                    "Bağlantı Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettingsFromUI();
                SettingsManager.SaveSettings(Settings);
                SettingsSaved = true;
                MessageBox.Show("Ayarlar başarıyla kaydedildi!", "Başarılı",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ayarlar kaydedilemedi: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}