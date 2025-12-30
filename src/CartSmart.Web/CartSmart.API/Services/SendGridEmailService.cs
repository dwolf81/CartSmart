using SendGrid;
using SendGrid.Helpers.Mail;

namespace CartSmart.API.Services
{
    public class SendGridEmailService : IEmailService
    {
        private readonly ILogger<SendGridEmailService> _logger;
        private readonly string _apiKey;
        private readonly string _defaultFromEmail;
        private readonly string _defaultFromName;

        public SendGridEmailService(IConfiguration config, ILogger<SendGridEmailService> logger)
        {
            _logger = logger;
            _apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
                      ?? config["SendGrid:ApiKey"]
                      ?? throw new InvalidOperationException("SendGrid API key not configured.");

            _defaultFromEmail = config["SendGrid:DefaultFromEmail"] ?? "no-reply@localhost";
            _defaultFromName = config["SendGrid:DefaultFromName"] ?? "CartSmart";
        }

        public async Task SendAsync(
            string toEmail,
            string subject,
            string plainTextContent,
            string? htmlContent = null,
            string? fromEmail = null,
            string? fromName = null,
            string? replyTo = null,
            CancellationToken ct = default)
        {
            var client = new SendGridClient(_apiKey);

            var from = new EmailAddress(fromEmail ?? _defaultFromEmail, fromName ?? _defaultFromName);
            var to = new EmailAddress(toEmail);

            var msg = new SendGridMessage();
            msg.SetFrom(from);
            msg.AddTo(to);
            msg.SetSubject(subject);
            msg.AddContent(MimeType.Text, plainTextContent);
            msg.AddContent(MimeType.Html, htmlContent ?? plainTextContent);

            // Disable click tracking (and HTML/text rewriting)
            msg.SetClickTracking(false, false);

            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                msg.SetReplyTo(new EmailAddress(replyTo));
            }

            // Optionally still keep open tracking:
            // msg.SetOpenTracking(true);

            try
            {
                var response = await client.SendEmailAsync(msg, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Body.ReadAsStringAsync(ct);
                    _logger.LogWarning("SendGrid failed: {Status} {Body}", response.StatusCode, body);
                    throw new InvalidOperationException($"Email not sent (status {(int)response.StatusCode}).");
                }
                _logger.LogInformation("Email sent to {Recipient}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email to {Recipient}", toEmail);
                throw;
            }
        }
    }
}