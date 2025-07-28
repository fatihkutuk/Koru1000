using System.Text.Json.Serialization;

namespace Koru1000.KepServerService.Models;

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