using System.Text.RegularExpressions;

namespace CartSmart.API.Security
{
    public static class PasswordPolicy
    {
        private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        {
            "password","123456","123456789","qwerty","111111","12345678","abc123","1234567",
            "password1","12345","admin","letmein","welcome"
        };

        public static bool TryValidate(string? password, string? email, string? firstName, string? lastName, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(password)) { message = "Password is required."; return false; }

            if (password.Length < 8) { message = "Use at least 8 characters."; return false; }
            if (password.Length > 128) { message = "Password too long."; return false; }
            if (password.Any(char.IsWhiteSpace)) { message = "No spaces allowed in password."; return false; }

            bool hasLower = password.Any(char.IsLower);
            bool hasUpper = password.Any(char.IsUpper);
            bool hasDigit = password.Any(char.IsDigit);
            bool hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));

            var categories = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
            if (categories < 3) { message = "Use a mix of letters, numbers, and symbols."; return false; }

            if (Common.Contains(password)) { message = "Password is too common."; return false; }

            var local = email?.Split('@').FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(local) && local!.Length >= 3 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            { message = "Avoid using your email in the password."; return false; }

            if (!string.IsNullOrWhiteSpace(firstName) && firstName!.Length >= 3 &&
                password.Contains(firstName, StringComparison.OrdinalIgnoreCase))
            { message = "Avoid using your first name in the password."; return false; }

            if (!string.IsNullOrWhiteSpace(lastName) && lastName!.Length >= 3 &&
                password.Contains(lastName, StringComparison.OrdinalIgnoreCase))
            { message = "Avoid using your last name in the password."; return false; }

            return true;
        }
    }
}