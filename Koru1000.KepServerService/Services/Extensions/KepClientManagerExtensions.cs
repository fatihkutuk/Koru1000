// Koru1000.KepServerService/Services/Extensions/KepClientManagerExtensions.cs
using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services.Extensions
{
    public static class KepClientManagerExtensions
    {
        public static async Task UnsubscribeDeviceAsync(this IKepClientManager clientManager,
            int clientId, int deviceId)
        {
            try
            {
                // Bu method KepClientManager'da implement edilecek
                // Specific device'ın tag'larından unsubscribe yapacak
            }
            catch (Exception ex)
            {
                // Log but don't throw - subscription errors shouldn't fail operations
            }
        }

        public static async Task RestartAffectedClientsAsync(this IKepClientManager clientManager,
            int deviceId)
        {
            try
            {
                // Device'ın hangi client'larda olduğunu bul ve onları restart et
                // Bu method KepClientManager'da implement edilecek
            }
            catch (Exception ex)
            {
                // Log but don't throw
            }
        }
    }
}