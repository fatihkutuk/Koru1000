using Koru1000.Core.Models.DomainModels;
using Koru1000.Core.Models.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Koru1000.DatabaseManager.Services
{
    public class HierarchyService
    {
        private readonly SystemHierarchyService _systemHierarchyService;
        private readonly ILogger<HierarchyService>? _logger;

        public HierarchyService(SystemHierarchyService systemHierarchyService, ILogger<HierarchyService>? logger = null)
        {
            _systemHierarchyService = systemHierarchyService;
            _logger = logger;
        }

        public async Task<ObservableCollection<TreeNodeBase>> BuildHierarchyAsync(bool forceReload = false)
        {
            try
            {
                _logger?.LogInformation("🌳 Building UI hierarchy from domain models...");

                // Domain modellerini yükle
                var systemHierarchy = await _systemHierarchyService.LoadCompleteHierarchyAsync(forceReload);

                // UI modellerine dönüştür
                var rootNodes = new ObservableCollection<TreeNodeBase>();

                foreach (var driverType in systemHierarchy.DriverTypes)
                {
                    var driverTypeNode = MapDriverTypeToNode(driverType);
                    rootNodes.Add(driverTypeNode);
                }

                _logger?.LogInformation($"✅ UI hierarchy built with {rootNodes.Count} driver types");
                return rootNodes;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to build UI hierarchy");
                throw;
            }
        }

        private DriverNode MapDriverTypeToNode(DriverTypeModel driverType)
        {
            var driverTypeNode = new DriverNode
            {
                Id = driverType.Id,
                Name = driverType.Name,
                DisplayName = $"🔌 {driverType.Name}",
                DriverTypeName = driverType.Name,
                IsExpanded = false,
                IsChildrenLoaded = true // Artık hep loaded
            };

            // Drivers ekle
            foreach (var driver in driverType.Drivers)
            {
                var driverNode = MapDriverToNode(driver);
                driverNode.Parent = driverTypeNode;
                driverTypeNode.Children.Add(driverNode);
            }

            return driverTypeNode;
        }

        private DriverNode MapDriverToNode(DriverModel driver)
        {
            var statusIcon = GetDriverStatusIcon(driver.Status);
            var driverNode = new DriverNode
            {
                Id = driver.Id,
                Name = driver.Name,
                DisplayName = $"🔧 {driver.Name} {statusIcon}",
                DriverTypeName = null, // Bu konkret driver
                IsExpanded = false,
                IsChildrenLoaded = true
            };

            // Channels ekle
            foreach (var channel in driver.Channels)
            {
                var channelNode = MapChannelToNode(channel);
                channelNode.Parent = driverNode;
                driverNode.Children.Add(channelNode);
            }

            return driverNode;
        }

        private ChannelNode MapChannelToNode(ChannelModel channel)
        {
            var channelNode = new ChannelNode
            {
                Id = 0, // Channel'ın ID'si yok
                Name = channel.Name,
                DisplayName = $"📡 {channel.Name} ({channel.Devices.Count} devices)",
                ChannelTypeId = channel.ChannelTypeId,
                ChannelTypeName = channel.ChannelTypeName,
                IsExpanded = false,
                IsChildrenLoaded = true
            };

            // Devices ekle
            foreach (var device in channel.Devices)
            {
                var deviceNode = MapDeviceToNode(device);
                deviceNode.Parent = channelNode;
                channelNode.Children.Add(deviceNode);
            }

            return channelNode;
        }

        private DeviceNode MapDeviceToNode(DeviceModel device)
        {
            var statusIcon = GetDeviceStatusIcon(device.StatusCode);
            var deviceNode = new DeviceNode
            {
                Id = device.Id,
                Name = device.Name,
                DisplayName = $"🔧 {device.Name} [{device.StatusDescription}] {statusIcon}",
                DeviceTypeId = device.DeviceTypeId,
                DeviceTypeName = device.DeviceTypeName,
                StatusCode = device.StatusCode,
                StatusDescription = device.StatusDescription,
                LastUpdateTime = device.LastUpdateTime,
                IsExpanded = false,
                IsChildrenLoaded = true, // Artık child yok
                HasTagsLoaded = false
            };

            // HİÇ TAG NODE EKLEME!
            // Children boş kalacak

            return deviceNode;
        }

        private string GetDriverStatusIcon(DriverStatus status)
        {
            return status switch
            {
                DriverStatus.Running => "🟢",
                DriverStatus.Starting => "🟡",
                DriverStatus.Stopping => "🟡",
                DriverStatus.Error => "🔴",
                DriverStatus.Stopped => "⚫",
                _ => "❓"
            };
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

        // Eski lazy loading metodu artık kullanılmayacak
        [Obsolete("Use BuildHierarchyAsync instead")]
        public Task LoadChildrenAsync(TreeNodeBase node)
        {
            // Artık tüm data loaded olduğu için bu metoda gerek yok
            return Task.CompletedTask;
        }
    }
}