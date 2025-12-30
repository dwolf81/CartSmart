using CartSmart.API.Models.DTOs;

namespace CartSmart.API.Services
{
    public interface INotificationService
    {
        Task<long> CreateAsync(long userId, string typeCode, string message, string? linkUrl = null);
        Task<(IEnumerable<NotificationDTO> items, int unread, long? nextCursor)> GetAsync(long userId, long? cursor, int limit);
        Task MarkReadAsync(long userId, long id);
        Task MarkAllReadAsync(long userId);
    }
}