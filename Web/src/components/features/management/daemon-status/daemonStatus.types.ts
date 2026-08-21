import type { ComponentProps } from 'react';
import type { AccordionSection } from '@components/ui/AccordionSection';
import type { ColorToken } from '@utils/eventColors';
import type { DaemonStatusDto } from '../../../../types';

/**
 * Icon component accepted by the card header. Derived from AccordionSection rather than restated so
 * the brand SVGs and lucide icons it already accepts stay accepted here without a second union to
 * keep in step.
 */
export type DaemonStatusIcon = NonNullable<ComponentProps<typeof AccordionSection>['icon']>;

interface DaemonHelpDefinition {
  term: string;
  description: string;
}

/** Contents of the help popover shown beside a daemon card title. */
export interface DaemonStatusHelpContent {
  title: string;
  definitions: DaemonHelpDefinition[];
  note: string;
}

/**
 * Brand identity and hub wiring for a login-free daemon card. Anonymous services report only Docker
 * availability and a session count, so a service is fully described by this table instead of by its
 * own component.
 */
export interface AnonymousDaemonService {
  /** Activity-registry key under the `integration` domain, e.g. `battlenet`. */
  integrationKey: string;
  /** Accordion group member id, e.g. `integrations-battlenet`. */
  accordionId: string;
  icon: DaemonStatusIcon;
  /** Theme custom property carrying the brand accent, e.g. `--theme-blizzard`. */
  iconColor: ColorToken;
  /** Reads the daemon status document for this service. */
  loadStatus: () => Promise<DaemonStatusDto>;
  /** Hub events that mean this daemon's status changed. */
  refreshEvents: readonly string[];
}

/**
 * Every string a login-free daemon card renders. These are the real per-service difference: each
 * sentence names its own brand and CDN, so they are resolved from full literal i18n keys in the
 * per-service file rather than built from a prefix, which keeps every key greppable.
 */
export interface AnonymousDaemonCopy {
  title: string;
  summary: string;
  connected: string;
  notConnected: string;
  loadingStatus: string;
  loadError: string;
  /** Headline shown while the daemon is reachable, e.g. "Docker Service". */
  availableHeadline: string;
  availableDetail: string;
  unavailableDetail: string;
  /** Right-hand session readout. Takes the count so the caller owns its own plural rules. */
  sessionCount: (count: number) => string;
  help: DaemonStatusHelpContent;
}
