namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Authentication result
    /// </summary>
    public class AuthenticationResult
    {
        public Guid? AttemptId { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public bool Success { get; set; }
        public bool RequiresTwoFactor { get; set; }
        public bool RequiresEmailCode { get; set; }
        public bool RequiresMobileConfirmation { get; set; }
        public bool SessionExpired { get; set; }
        public string? Message { get; set; }

        /// <summary>
        /// i18n key naming the same reason as <see cref="Message"/>, written onto the refusal body
        /// so the browser can show it in the reader's language. Null where the text came from Steam
        /// itself, which no key can translate.
        /// </summary>
        public string? StageKey { get; set; }
        public string? AccountName { get; set; }
        public string? RefreshToken { get; set; }

        /// <summary>
        /// The tracked operation the sign-in ran under. Null only when the service was shutting down
        /// and no operation was ever registered.
        /// </summary>
        public Guid? OperationId { get; set; }
    }
}
