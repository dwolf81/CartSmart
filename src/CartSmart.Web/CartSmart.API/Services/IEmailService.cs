namespace CartSmart.API.Services
{
    public interface IEmailService
    {
        Task SendAsync(string toEmail, string subject, string plainTextContent, string? htmlContent = null, string? fromEmail = null, string? fromName = null, string? replyTo = null, CancellationToken ct = default);
    }
}