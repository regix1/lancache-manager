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
        // An iPhone or iPad agent also says "like Mac OS X", so it is checked before the Mac.
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
        // An Android agent also says "Linux", so it is checked before Linux.
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
        else if (userAgent.Contains("Linux"))
            os = "Linux";

        // Detect Browser (order matters - check specific browsers before generic ones). Every iOS browser
        // sends "Safari/" and names itself with its own iOS token (EdgiOS, CriOS, FxiOS) instead of
        // Edg, Chrome or Firefox.
        if (userAgent.Contains("Edg/") || userAgent.Contains("EdgiOS/"))
        {
            var match = Regex.Match(userAgent, @"Edg(?:iOS)?/([\d.]+)");
            browser = match.Success ? $"Edge {match.Groups[1].Value}" : "Edge";
        }
        else if (userAgent.Contains("OPR/") || userAgent.Contains("Opera/"))
        {
            var match = Regex.Match(userAgent, @"(?:OPR|Opera)/([\d.]+)");
            browser = match.Success ? $"Opera {match.Groups[1].Value}" : "Opera";
        }
        else if (userAgent.Contains("Chrome/") || userAgent.Contains("CriOS/"))
        {
            var match = Regex.Match(userAgent, @"(?:Chrome|CriOS)/([\d.]+)");
            browser = match.Success ? $"Chrome {match.Groups[1].Value}" : "Chrome";
        }
        else if (userAgent.Contains("Firefox/") || userAgent.Contains("FxiOS/"))
        {
            var match = Regex.Match(userAgent, @"(?:Firefox|FxiOS)/([\d.]+)");
            browser = match.Success ? $"Firefox {match.Groups[1].Value}" : "Firefox";
        }
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome"))
        {
            var match = Regex.Match(userAgent, @"Version/([\d.]+)");
            browser = match.Success ? $"Safari {match.Groups[1].Value}" : "Safari";
        }

        return (os, browser);
    }
}
