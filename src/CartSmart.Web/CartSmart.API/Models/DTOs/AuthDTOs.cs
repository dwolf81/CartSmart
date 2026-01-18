namespace CartSmart.API.Models.DTOs
{
    public class RegisterRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public bool EmailOptIn { get; set; } = false;
        public string RecaptchaToken { get; set; } // Added property for reCAPTCHA token
    }

    public class LoginRequest
    {
        public string EmailAddress { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class SocialLoginRequest
    {
        public string Provider { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public bool? OptedIntoEmails { get; set; } // nullable to distinguish "not provided"
    }

    public class AuthResponse
    {
        public bool Success { get; set; }
        public string Token { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public UserDTO User { get; set; } = new();

        public bool ActivationRequired { get; set; } // set true on inactive login attempt
        public bool LockedOut { get; set; }  
        
    }

    public class UserDTO
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Level { get; set; }

        public int DealsPosted { get; set; }
        public string ReferralCode { get; set; } = string.Empty;

        public string? Bio { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public bool EmailOptIn { get; set; } = false;

        public bool AllowReview { get; set; } = false;

        public bool Active { get; set; } = false;

        public bool Admin { get; set; } = false;

    }

    public class UserResponse
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }


    public class AuthUserDTO
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string? DisplayName { get; set; }
        public bool EmailConfirmed { get; set; }
        public string? SsoProvider { get; set; }
        public bool HasPassword { get; set; }   // NEW

        public bool Active { get; set; } = false;
    }
}