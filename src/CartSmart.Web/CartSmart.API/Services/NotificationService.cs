using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using Supabase;
using Supabase.Postgrest.Responses; 

namespace CartSmart.API.Services
{
    public class NotificationService : INotificationService
    {
        private readonly ISupabaseService _supabase;

        public NotificationService(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        private Client Client => _supabase.GetClient();

        public async Task<long> CreateAsync(long userId, string typeCode, string message, string? linkUrl = null)
        {
            if (string.IsNullOrWhiteSpace(typeCode))
                throw new ArgumentException("typeCode required", nameof(typeCode));
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("message required", nameof(message));

            // Lookup notification type
            var typeResp = await Client
                .From<NotificationType>()
                .Select("id, code")
                .Filter("code", Supabase.Postgrest.Constants.Operator.Equals, typeCode)
                .Get();

            var type = typeResp.Models.FirstOrDefault();
            if (type == null)
                throw new InvalidOperationException($"Unknown notification type: {typeCode}");

            var notification = new Notification
            {
                UserId = userId,
                TypeId = type.Id,
                Message = message,
                LinkUrl = linkUrl,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };

            // Insert
            var insertResp = await Client
                .From<Notification>()
                .Insert(notification);

            var created = insertResp.Models.FirstOrDefault()
                ?? throw new InvalidOperationException("Failed to create notification");

            return created.Id;
        }

        public async Task<(IEnumerable<NotificationDTO> items, int unread, long? nextCursor)> GetAsync(long userId, long? cursor, int limit)
        {
            limit = Math.Clamp(limit, 1, 50);

            var baseQuery = Client
                .From<Notification>()
                .Select("*")
                .Filter("user_id", Supabase.Postgrest.Constants.Operator.Equals, userId.ToString())
                .Order("id", Supabase.Postgrest.Constants.Ordering.Descending);

            if (cursor.HasValue)
                baseQuery = baseQuery.Filter("id", Supabase.Postgrest.Constants.Operator.LessThan, cursor.Value.ToString());

            var pageResp = await baseQuery.Limit(limit).Get();
            var notifications = pageResp.Models;

            var typeIds = notifications.Select(n => n.TypeId).Distinct().ToArray();
            var typeMap = new Dictionary<int, string>();
            if (typeIds.Length > 0)
            {
                ModeledResponse<NotificationType> typeResp;
                if (typeIds.Length == 1)
                {
                    // Single id -> simple equals filter
                    typeResp = await Client
                        .From<NotificationType>()
                        .Select("id, code")
                        .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, typeIds[0])
                        .Get();
                }
                else
                {
                    // Multiple ids -> provide List<int> for IN
                    var idList = typeIds.ToList(); // List<int>
                    typeResp = await Client
                        .From<NotificationType>()
                        .Select("id, code")
                        .Filter("id", Supabase.Postgrest.Constants.Operator.In, idList)
                        .Get();
                }

                typeMap = typeResp.Models.ToDictionary(t => t.Id, t => t.Code);

            }

            var unreadResp = await Client
                .From<Notification>()
                .Select("id")
                .Filter("user_id", Supabase.Postgrest.Constants.Operator.Equals, userId.ToString())
                .Filter("is_read", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

    int unread = unreadResp.Models.Count;

    var dtos = notifications.Select(n => new NotificationDTO
    {
        Id = n.Id,
        TypeCode = typeMap.GetValueOrDefault(n.TypeId, ""),
        Message = n.Message,
        LinkUrl = n.LinkUrl,
        IsRead = n.IsRead,
        CreatedAt = n.CreatedAt
    }).ToList();

    long? nextCursor = dtos.Count == limit ? dtos.Last().Id : null;
    return (dtos, unread, nextCursor);
        }

        public async Task MarkReadAsync(long userId, long id)
        {
            var resp = await Client
                .From<Notification>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
                .Filter("user_id", Supabase.Postgrest.Constants.Operator.Equals, userId.ToString())
                .Single();

            var n = resp;
            if (n == null || n.IsRead) return;

            n.IsRead = true;
            await Client.From<Notification>().Upsert(n);
        }

        public async Task MarkAllReadAsync(long userId)
        {
            var resp = await Client
                .From<Notification>()
                .Filter("user_id", Supabase.Postgrest.Constants.Operator.Equals, userId.ToString())
                .Filter("is_read", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

            if (resp.Models.Count == 0) return;

            foreach (var n in resp.Models)
            {
                n.IsRead = true;
                await Client.From<Notification>().Upsert(n);
            }
        }
    }

}