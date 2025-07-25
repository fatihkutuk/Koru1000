using System.Text.Json.Serialization;

namespace Koru1000.KepServerService.Models;

public class DriverCustomSettings
{
    [JsonPropertyName("security")]
    public DriverSecurity Security { get; set; } = new();

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "2";

    [JsonPropertyName("EndpointUrl")]
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";

    [JsonPropertyName("credentials")]
    public DriverCredentials Credentials { get; set; } = new();

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("protocolType")]
    public string ProtocolType { get; set; } = "OPC";

    [JsonPropertyName("addressFormat")]
    public string AddressFormat { get; set; } = "ns={namespace};s={channelName}.{deviceName}.{tagName}";

    [JsonPropertyName("tagsPerClient")]
    public int TagsPerClient { get; set; } = 20000;

    [JsonPropertyName("connectionSettings")]
    public DriverConnectionSettings ConnectionSettings { get; set; } = new();
}

public class DriverSecurity
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "SignAndEncrypt";

    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "Basic256Sha256";

    [JsonPropertyName("userTokenType")]
    public string UserTokenType { get; set; } = "UserName";
}

public class DriverCredentials
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class DriverConnectionSettings
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

    [JsonPropertyName("publishingInterval")]
    public int PublishingInterval { get; set; } = 2000;

    [JsonPropertyName("maxTagsPerSubscription")]
    public int MaxTagsPerSubscription { get; set; } = 15000;
}

public class DriverInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DriverTypeName { get; set; } = "";
    public DriverCustomSettings CustomSettings { get; set; } = new();
}