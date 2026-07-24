import { createContext } from 'react';

/**
 * Live presence of the person using THIS browser tab, as opposed to the server-side view of every
 * session (that one comes from the activity registry via useActivityStatus). Produced by the single
 * app-wide useActivityTracker mount inside UserPresenceProvider.
 */
export interface UserPresence {
  /** True while the tab is visible and real interaction has been seen within the idle timeout. */
  isActive: boolean;
  /** Epoch ms of the most recent interaction observed in this tab. */
  lastActivityTime: number;
}

export const UserPresenceContext = createContext<UserPresence | null>(null);
