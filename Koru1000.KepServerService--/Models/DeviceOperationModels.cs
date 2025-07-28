// Koru1000.KepServerService/Models/DeviceOperationModels.cs
namespace Koru1000.KepServerService.Models
{
    public static class DeviceStatusCodes
    {
        // ADD Operations
        public const byte ADD_PENDING = 10;
        public const byte ADD_SUCCESS = 11;
        public const byte ADD_FAILED = 12;

        // DELETE Operations  
        public const byte DELETE_PENDING = 20;
        public const byte DELETE_SUCCESS = 21;
        public const byte DELETE_FAILED = 22;

        // UPDATE Operations
        public const byte UPDATE_PENDING = 30;
        public const byte UPDATE_SUCCESS = 31;
        public const byte UPDATE_FAILED = 32;

        // ACTIVATE Operations
        public const byte ACTIVATE_PENDING = 40;
        public const byte ACTIVATE_SUCCESS = 41;
        public const byte ACTIVATE_FAILED = 42;

        // DEACTIVATE Operations
        public const byte DEACTIVATE_PENDING = 50;
        public const byte DEACTIVATE_SUCCESS = 51;
        public const byte DEACTIVATE_FAILED = 52;

        // TAG Operations
        public const byte TAG_UPDATE_PENDING = 60;
        public const byte TAG_UPDATE_SUCCESS = 61;
        public const byte TAG_UPDATE_FAILED = 62;

        public static readonly byte[] PendingStatuses = { 10, 20, 30, 40, 50, 60 };

        public static byte GetSuccessCode(byte pendingCode) => (byte)(pendingCode + 1);
        public static byte GetFailedCode(byte pendingCode) => (byte)(pendingCode + 2);
        public static bool IsPendingStatus(byte statusCode) => statusCode % 10 == 0 && statusCode >= 10 && statusCode <= 60;
    }

    public class DeviceOperation
    {
        public int DeviceId { get; set; }
        public string ChannelName { get; set; } = "";
        public byte StatusCode { get; set; }
        public string DeviceJson { get; set; } = "{}";
        public string ChannelJson { get; set; } = "{}";
        public int DeviceTypeId { get; set; }
        public DateTime UpdateTime { get; set; }

        public string DeviceName => DeviceId.ToString();
    }

    public class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public byte ResultStatusCode { get; set; }
        public List<string> Steps { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public TimeSpan Duration { get; set; }
        public bool RequiresClientRestart { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();

        public static OperationResult CreateSuccess(byte successCode, string message = "", bool requiresRestart = false)
        {
            return new OperationResult
            {
                Success = true,
                Message = message,
                ResultStatusCode = successCode,
                RequiresClientRestart = requiresRestart
            };
        }

        public static OperationResult CreateFailure(byte failureCode, string message)
        {
            return new OperationResult
            {
                Success = false,
                Message = message,
                ResultStatusCode = failureCode
            };
        }
    }

    public class DeviceOperationEventArgs : EventArgs
    {
        public int DeviceId { get; set; }
        public byte StatusCode { get; set; }
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class DeviceTagInfo
    {
        public int DeviceId { get; set; }
        public string ChannelName { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceTypeTagsJson { get; set; } = "[]";
        public string IndividualTagsJson { get; set; } = "[]";
        public int TotalTagCount => GetTagCount(DeviceTypeTagsJson) + GetTagCount(IndividualTagsJson);

        private int GetTagCount(string tagsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(tagsJson) || tagsJson == "[]") return 0;
                using var doc = System.Text.Json.JsonDocument.Parse(tagsJson);
                return doc.RootElement.GetArrayLength();
            }
            catch
            {
                return 0;
            }
        }
    }
}