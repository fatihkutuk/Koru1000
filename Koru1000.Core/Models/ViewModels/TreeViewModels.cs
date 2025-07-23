using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Koru1000.Core.Models.ViewModels
{
    public abstract class TreeNodeBase : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;

        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Icon { get; set; }
        public int Id { get; set; }
        public TreeNodeBase Parent { get; set; }
        public ObservableCollection<TreeNodeBase> Children { get; set; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public abstract string NodeType { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class DriverNode : TreeNodeBase
    {
        public override string NodeType => "Driver";
        public string DriverTypeName { get; set; }
        public string ConnectionString { get; set; }
        public bool IsConnected { get; set; }

        public DriverNode()
        {
            Icon = "🔌";
        }
    }

    public class ChannelNode : TreeNodeBase
    {
        public override string NodeType => "Channel";
        public int ChannelTypeId { get; set; }
        public string ChannelTypeName { get; set; }
        public string ChannelJson { get; set; }

        public ChannelNode()
        {
            Icon = "📂";
        }
    }

    public class DeviceNode : TreeNodeBase
    {
        public override string NodeType => "Device";
        public int DeviceTypeId { get; set; }
        public string DeviceTypeName { get; set; }
        public byte StatusCode { get; set; }
        public string StatusDescription { get; set; }
        public string DeviceJson { get; set; }
        public DateTime LastUpdateTime { get; set; }

        public DeviceNode()
        {
            Icon = "🔧";
        }

        public string StatusIcon => StatusCode switch
        {
            11 or 31 or 41 or 61 => "🟢", // Active statuses
            51 => "🟡", // Warning
            _ => "🔴" // Error or offline
        };
    }

    public class TagNode : TreeNodeBase
    {
        public override string NodeType => "Tag";
        public string TagAddress { get; set; }
        public string DataType { get; set; }
        public object CurrentValue { get; set; }
        public DateTime LastReadTime { get; set; }
        public string Quality { get; set; }
        public bool IsWritable { get; set; }

        public TagNode()
        {
            Icon = "🏷️";
        }

        public string QualityIcon => Quality switch
        {
            "Good" => "🟢",
            "Uncertain" => "🟡",
            _ => "🔴"
        };
    }
}