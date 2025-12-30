using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CartSmart.API.Services;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationService _notifications;
        private readonly IAuthService _auth;

        public NotificationsController(INotificationService notifications, IAuthService auth)
        {
            _notifications = notifications;
            _auth = auth;
        }

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] long? cursor, [FromQuery] int limit = 20)
        {
            var user = await _auth.GetCurrentUser();
            if (user == null) return Unauthorized();
            var (items, unread, nextCursor) = await _notifications.GetAsync(user.Id, cursor, limit);
            return Ok(new { notifications = items, unread, nextCursor });
        }

        [HttpPatch("{id:long}/read")]
        public async Task<IActionResult> MarkRead(long id)
        {
            var user = await _auth.GetCurrentUser();
            if (user == null) return Unauthorized();
            await _notifications.MarkReadAsync(user.Id, id);
            return Ok(new { ok = true });
        }

        [HttpPatch("read-all")]
        public async Task<IActionResult> MarkAll()
        {
            var user = await _auth.GetCurrentUser();
            if (user == null) return Unauthorized();
            await _notifications.MarkAllReadAsync(user.Id);
            return Ok(new { ok = true });
        }
    }
}