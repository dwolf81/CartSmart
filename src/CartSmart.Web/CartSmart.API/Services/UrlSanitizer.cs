using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public interface IUrlSanitizer
    {
        string? Clean(string? raw, bool injectAffiliate = true);
        string? CleanForStore(string? raw, Store store, bool injectAffiliate = true);
    }

    public class UrlSanitizer : IUrlSanitizer
    {
        private enum RequiredMode { Default, All, List, None } // NEW

        private readonly IConfiguration _cfg;
        private readonly HashSet<string> _genericRemove = new(StringComparer.OrdinalIgnoreCase)
        {
            "utm_source","utm_medium","utm_campaign","utm_term","utm_content","utm_id",
            "gclid","fbclid","msclkid","mc_eid","mc_cid","ga_session_id","ga_session_number",
            "_hsenc","_hsmi","irgwc","irclickid","clickid","affid","affiliate_id","affiliate",
            "aff","partner","ref","referrer","campaign","cmpid","scid","veaction"
        };

        public UrlSanitizer(IConfiguration cfg) => _cfg = cfg;

        public string? Clean(string? raw, bool injectAffiliate = true)
        {
            return CleanInternal(raw, injectAffiliate,
                affiliateParamOverride: null,
                affiliateValueOverride: null,
                requiredList: null,
                mode: RequiredMode.Default);
        }

        public string? CleanForStore(string? raw, Store store, bool injectAffiliate = true)
        {
            // Determine mode based on required_query_vars
            RequiredMode mode;
            HashSet<string>? list = null;

            if (!string.IsNullOrWhiteSpace(store.RequiredQueryVars) &&
                store.RequiredQueryVars.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                mode = RequiredMode.None;
            }
            else if (string.IsNullOrWhiteSpace(store.RequiredQueryVars))
            {
                mode = RequiredMode.All; // keep all original params (including tracking)
            }
            else
            {
                list = ParseRequired(store.RequiredQueryVars);
                mode = list.Count == 0 ? RequiredMode.All : RequiredMode.List;
            }

            return CleanInternal(
                raw,
                injectAffiliate,
                affiliateParamOverride: store.AffiliateCodeVar,
                affiliateValueOverride: store.AffiliateCode,
                requiredList: list,
                mode: mode);
        }

        private HashSet<string> ParseRequired(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new();
            return new HashSet<string>(
                csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim())
                   .Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        private string? CleanInternal(
            string? raw,
            bool injectAffiliate,
            string? affiliateParamOverride,
            string? affiliateValueOverride,
            HashSet<string>? requiredList,
            RequiredMode mode) // CHANGED
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            {
                if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate("https://" + raw.Trim(), UriKind.Absolute, out var alt))
                    uri = alt;
                else
                    return raw.Trim();
            }

            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];

            var path = uri.AbsolutePath;

            // Canonicalize platform paths
            if (host.EndsWith("amazon.com", StringComparison.OrdinalIgnoreCase))
            {
                var asinMatch = Regex.Match(path, @"/(dp|gp/product)/([A-Z0-9]{10})", RegexOptions.IgnoreCase);
                if (asinMatch.Success)
                    path = $"/dp/{asinMatch.Groups[2].Value.ToUpperInvariant()}";
            }
            else if (host.EndsWith("ebay.com", StringComparison.OrdinalIgnoreCase))
            {
                var itm = Regex.Match(path, @"/itm/(\d+)", RegexOptions.IgnoreCase);
                if (itm.Success)
                    path = $"/itm/{itm.Groups[1].Value}";
            }

            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var result = new List<(string Key, string Value)>();

            foreach (string key in q.AllKeys.Where(k => k != null))
            {
                var val = q[key] ?? "";

                // Skip original affiliate param if we will replace
                if (injectAffiliate && affiliateParamOverride != null &&
                    key.Equals(affiliateParamOverride, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (mode)
                {
                    case RequiredMode.None:
                        // Drop all params (will inject affiliate only below)
                        continue;

                    case RequiredMode.All:
                        // Keep everything as-is (including tracking)
                        result.Add((key, val));
                        continue;

                    case RequiredMode.List:
                        // Keep only listed
                        if (requiredList != null && requiredList.Contains(key))
                            result.Add((key, val));
                        continue;

                    case RequiredMode.Default:
                        // Original behavior: remove generic tracking
                        if (_genericRemove.Contains(key))
                            continue;
                        result.Add((key, val));
                        continue;
                }
            }

            // Inject affiliate param if requested
            if (injectAffiliate &&
                !string.IsNullOrWhiteSpace(affiliateParamOverride) &&
                !string.IsNullOrWhiteSpace(affiliateValueOverride))
            {
                result.Add((affiliateParamOverride, affiliateValueOverride));
            }

            // Sort deterministically
            result = result
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var qs = string.Join("&", result.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            var final = $"{uri.Scheme}://{host}{(path == "/" ? "" : path.TrimEnd('/'))}";
            if (qs.Length > 0) final += "?" + qs;
            return final;
        }
    }
}