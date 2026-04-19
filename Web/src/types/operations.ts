/**
 * Canonical operation lifecycle status used across SignalR progress events and
 * the notification system. Mirrors the backend `OperationStatus` enum.
 */
export type OperationStatus =
  | 'pending'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'failed'
  | 'cancelled';

/**
 * Variant used by transient system toast/banner events (ShowToast).
 * Keep in sync with backend `NotificationVariant` / toast type literals.
 */
export type NotificationVariant = 'success' | 'error' | 'info' | 'warning';

/**
 * Lifecycle status for a persisted `PrefillSession`. Mirrors the backend
 * `PrefillSessionStatus` enum which is explicitly serialized as PascalCase
 * (see comment in `Api/LancacheManager/Models/PrefillSessionStatus.cs`).
 */
export type PrefillSessionStatus = 'Active' | 'Terminated' | 'Orphaned' | 'Cleaned' | 'Cancelled';

/**
 * In-flight status of the short-lived daemon container (not the persisted
 * PrefillSession). Mirrors backend `DaemonSessionStatus` enum.
 * (See `Api/LancacheManager/Core/Services/SteamPrefill/Models/DaemonSessionTypes.cs`.)
 */
export type DaemonSessionStatus = 'Active' | 'Terminated' | 'Error';

/**
 * Auth state of the daemon's Steam connection. Mirrors backend `DaemonAuthState` enum
 * verbatim — PascalCase wire format via the default `JsonStringEnumConverter`.
 * (See `Api/LancacheManager/Core/Services/SteamPrefill/Models/DaemonSessionTypes.cs`.)
 */
export type DaemonAuthState =
  | 'NotAuthenticated'
  | 'LoggingIn'
  | 'UsernameRequired'
  | 'PasswordRequired'
  | 'TwoFactorRequired'
  | 'SteamGuardRequired'
  | 'DeviceConfirmationRequired'
  | 'AuthorizationUrlRequired'
  | 'Authenticated';
