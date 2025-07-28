using System.Text.Json.Serialization;

namespace Koru1000.KepServerService.Models;

public class DriverInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DriverTypeName { get; set; } = "";
    public DriverCustomSettings CustomSettings { get; set; } = new();
}

public class DriverCustomSettings
{
    [JsonPropertyName("security")]
    public SecuritySettings? Security { get; set; }

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "2";

    [JsonPropertyName("EndpointUrl")]
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";

    [JsonPropertyName("credentials")]
    public CredentialSettings? Credentials { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("protocolType")]
    public string ProtocolType { get; set; } = "OPC";

    [JsonPropertyName("addressFormat")]
    public string AddressFormat { get; set; } = "ns={namespace};s={channelName}.{deviceName}.{tagName}";

    [JsonPropertyName("connectionSettings")]
    public ConnectionSettings? ConnectionSettings { get; set; }

    // YENİ: REST API ayarları
    [JsonPropertyName("restApiSettings")]
    public RestApiSettings? RestApiSettings { get; set; }
}

public class SecuritySettings
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "None";

    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "None";

    [JsonPropertyName("userTokenType")]
    public string UserTokenType { get; set; } = "Anonymous";
}

public class CredentialSettings
{
    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
}

public class ConnectionSettings
{
    [JsonPropertyName("updateRate")]
    public int UpdateRate { get; set; } = 1000;

    [JsonPropertyName("waitForData")]
    public bool WaitForData { get; set; } = true;

    [JsonPropertyName("groupDeadband")]
    public int GroupDeadband { get; set; } = 0;

    [JsonPropertyName("sessionTimeout")]
    public int SessionTimeout { get; set; } = 600000;

    [JsonPropertyName("startupStrategy")]
    public string StartupStrategy { get; set; } = "sequential";

    [JsonPropertyName("clientStartDelay")]
    public int ClientStartDelay { get; set; } = 3000;

    [JsonPropertyName("maxTagsPerClient")]
    public int MaxTagsPerClient { get; set; } = 30000;

    [JsonPropertyName("publishingInterval")]
    public int PublishingInterval { get; set; } = 2000;
}

// YENİ: REST API ayarları
public class RestApiSettings
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:57412/config/v1/project";

    [JsonPropertyName("timeout")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("authentication")]
    public RestApiAuthentication? Authentication { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 3;

    [JsonPropertyName("retryDelayMs")]
    public int RetryDelayMs { get; set; } = 1000;
}

public class RestApiAuthentication
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Basic"; // Basic, Bearer, ApiKey

    [JsonPropertyName("username")]
    public string Username { get; set; } = "Administrator";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
}