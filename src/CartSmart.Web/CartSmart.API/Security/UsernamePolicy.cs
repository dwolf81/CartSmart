using System.Text.RegularExpressions;

namespace CartSmart.API.Security
{
    public static class UsernamePolicy
    {
        private static readonly Regex Allowed =
            new(@"^[A-Za-z][A-Za-z0-9._-]{2,29}$", RegexOptions.Compiled);

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "admin","administrator","root","support","help","contact","api","v1",
            "system","null","undefined","owner","moderator","me","about",
            "settings","profile","login","logout","signup"
        };

        public static bool TryValidate(string? username, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(username)) { message = "Username is required."; return false; }

            var u = username.Trim();

            if (!Allowed.IsMatch(u))
            {
                // Specific hints
                if (u.Length < 3) { message = "Username must be at least 3 characters."; return false; }
                if (u.Length > 30) { message = "Username must be 30 characters or fewer."; return false; }
                if (!char.IsLetter(u[0])) { message = "Username must start with a letter."; return false; }
                if (u.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '-')))
                { message = "Only letters, numbers, underscores, dots, and hyphens are allowed."; return false; }
            }

            // No trailing separator
            if (u.EndsWith('.') || u.EndsWith('_') || u.EndsWith('-'))
            { message = "Username cannot end with a separator."; return false; }

            // No consecutive separators
            if (u.Contains("..") || u.Contains("__") || u.Contains("--") ||
                u.Contains("._") || u.Contains("_.") || u.Contains(".-") ||
                u.Contains("-.") || u.Contains("_-") || u.Contains("-_"))
            { message = "Username cannot contain consecutive separators."; return false; }

            // Not all digits
            if (u.All(char.IsDigit)) { message = "Username cannot be only numbers."; return false; }

            // Reserved words
            if (Reserved.Contains(u)) { message = "Please choose a different username."; return false; }

            return true;
        }
    }
}