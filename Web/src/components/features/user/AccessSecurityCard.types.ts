// Wire-shape mirror of C# `GuestDurationResponse` in Models/Responses/AuthResponses.cs.
// Kept in a separate .types file to satisfy CLAUDE.md Fast Refresh rule
// (the .tsx file may only export React components).
export interface GuestDurationResponse {
  durationHours: number;
  source: 'ui' | 'config';
  canEdit: boolean;
  envVarValue: number;
}
