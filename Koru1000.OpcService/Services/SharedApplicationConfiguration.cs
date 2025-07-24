// Koru1000.OpcService/Services/SharedApplicationConfiguration.cs
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Koru1000.OpcService.Services
{
    public class SharedApplicationConfiguration
    {
        private static readonly object _lock = new object();
        private static ApplicationConfiguration? _instance;
        private static bool _isInitialized = false;

        public static async Task<ApplicationConfiguration> GetInstanceAsync(ILogger logger)
        {
            if (_instance != null && _isInitialized)
                return _instance;

            lock (_lock)
            {
                if (_instance != null && _isInitialized)
                    return _instance;

                return CreateConfigurationAsync(logger).GetAwaiter().GetResult();
            }
        }

        private static async Task<ApplicationConfiguration> CreateConfigurationAsync(ILogger logger)
        {
            try
            {
                logger.LogInformation("Creating shared OPC UA application configuration");

                var certificateSubjectName = $"CN=Koru1000 OPC Service, C=US, S=Arizona, O=OPC Foundation, DC={Utils.GetHostName()}";

                _instance = new ApplicationConfiguration()
                {
                    ApplicationName = "Koru1000 OPC Service",
                    ApplicationUri = $"urn:{Utils.GetHostName()}:Koru1000:OpcService",
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = @"Directory",
                            StorePath = @"%LocalApplicationData%\OPC Foundation\pki\own",
                            SubjectName = certificateSubjectName
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = @"Directory",
                            StorePath = @"%LocalApplicationData%\OPC Foundation\pki\issuer"
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = @"Directory",
                            StorePath = @"%LocalApplicationData%\OPC Foundation\pki\trusted"
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = @"Directory",
                            StorePath = @"%LocalApplicationData%\OPC Foundation\pki\rejected"
                        },
                        AutoAcceptUntrustedCertificates = true,
                        AddAppCertToTrustedStore = true
                    },
                    TransportQuotas = new TransportQuotas
                    {
                        OperationTimeout = 60000,       // 30 saniyeden 60 saniyeye artır
                        MaxStringLength = 1048576,
                        MaxByteStringLength = 1048576,
                        MaxArrayLength = 65535,
                        MaxMessageSize = 16777216,      // 8MB'dan 16MB'a artır
                        MaxBufferSize = 262144,         // 128KB'dan 256KB'a artır
                        ChannelLifetime = 600000,
                        SecurityTokenLifetime = 3600000
                    },
                    ClientConfiguration = new ClientConfiguration
                    {
                        DefaultSessionTimeout = 600000,
                        MinSubscriptionLifetime = 60000
                    },
                    TraceConfiguration = new TraceConfiguration()
                };

                await _instance.Validate(ApplicationType.Client);

                var applicationInstance = new ApplicationInstance(_instance);
                bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 2048);

                if (!certificateValid)
                {
                    throw new Exception("Shared application certificate could not be created or validated!");
                }

                _instance.CertificateValidator.CertificateValidation += (s, e) => {
                    logger.LogDebug("Certificate validation: Subject='{Subject}' - ACCEPTING", e.Certificate?.Subject);
                    e.Accept = true;
                };

                _isInitialized = true;
                logger.LogInformation("Shared OPC UA application configuration created successfully");

                return _instance;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create shared application configuration");
                throw;
            }
        }
    }
}