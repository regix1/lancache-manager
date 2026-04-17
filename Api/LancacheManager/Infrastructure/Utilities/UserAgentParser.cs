using System.Text.RegularExpressions;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Parses user agent strings to extract OS and browser information.
/// </summary>
public static class UserAgentParser
{
    public static (string? Os, string? Browser) Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return (null, null);
        }

        string? os = null;
        string? browser = null;

        // Detect OS
        if (userAgent.Contains("Windows NT 10.0"))
            os = "Windows 10/11";
        else if (userAgent.Contains("Windows NT 6.3"))
            os = "Windows 8.1";
        else if (userAgent.Contains("Windows NT 6.2"))
            os = "Windows 8";
        else if (userAgent.Contains("Windows NT 6.1"))
            os = "Windows 7";
        else if (userAgent.Contains("Windows"))
            os = "Windows";
        else if (userAgent.Contains("Mac OS X"))
        {
            try
            {
                var match = Regex.Match(userAgent, @"Mac OS X (\d+[._]\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
                os = match.Success ? $"macOS {match.Groups[1].Value.Replace('_', '.')}" : "macOS";
            }
            catch (RegexMatchTimeoutException)
            {
                os = "macOS";
            }
        }
        else if (userAgent.Contains("Linux"))
            os = "Linux";
        else if (userAgent.Contains("Android"))
        {
            try
            {
                var match = Regex.Match(userAgent, @"Android (\d+(\.\d+)?)", RegexOptions.None, TimeSpan.FromSeconds(1));
                os = match.Success ? $"Android {match.Groups[1].Value}" : "Android";
            }
            catch (RegexMatchTimeoutException)
            {
                os = "Android";
            }
        }
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad"))
        {
            try
            {
                var match = Regex.Match(userAgent, @"OS (\d+_\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
                os = match.Success ? $"iOS {match.Groups[1].Value.Replace('_', '.')}" : "iOS";
            }
            catch (RegexMatchTimeoutException)
            {
                os = "iOS";
            }
        }

        // Detect Browser (order matters - check specific browsers before generic ones)
        if (userAgent.Contains("Edg/"))
        {
            var match = Regex.Match(userAgent, @"Edg/([\d.]+)");
            browser = match.Success ? $"Edge {match.Groups[1].Value}" : "Edge";
        }
        else if (userAgent.Contains("OPR/") || userAgent.Contains("Opera/"))
        {
            var match = Regex.Match(userAgent, @"(?:OPR|Opera)/([\d.]+)");
            browser = match.Success ? $"Opera {match.Groups[1].Value}" : "Opera";
        }
        else if (userAgent.Contains("Chrome/"))
        {
            var match = Regex.Match(userAgent, @"Chrome/([\d.]+)");
            browser = match.Success ? $"Chrome {match.Groups[1].Value}" : "Chrome";
        }
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome"))
        {
            var match = Regex.Match(userAgent, @"Version/([\d.]+)");
            browser = match.Success ? $"Safari {match.Groups[1].Value}" : "Safari";
        }
        else if (userAgent.Contains("Firefox/"))
        {
            var match = Regex.Match(userAgent, @"Firefox/([\d.]+)");
            browser = match.Success ? $"Firefox {match.Groups[1].Value}" : "Firefox";
        }

        return (os, browser);
    }
}
