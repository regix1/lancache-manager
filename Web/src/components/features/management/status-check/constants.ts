import type { StatusCheckResolverMode } from '@services/api.service';

/**
 * Domain probed from the device viewing the page. Steam's trigger domain is the
 * canonical always-present cache domain, and the /lancache-heartbeat endpoint
 * answers 204 with an X-LanCache-Processed-By header on a real cache node.
 */
export const CLIENT_PROBE_HOST = 'lancache.steamcontent.com';
export const CLIENT_PROBE_URL = `http://${CLIENT_PROBE_HOST}/lancache-heartbeat`;
export const CLIENT_PROBE_TIMEOUT_MS = 4000;

/**
 * DNS-resolver strategy the next sweep should use. The value strings are the
 * frozen wire contract (camelCase) shared with the backend; the *Key entries map
 * to the management.sections.statusCheck.resolverMode i18n block.
 */
export const RESOLVER_MODE_OPTIONS: readonly {
  value: StatusCheckResolverMode;
  labelKey: string;
  tooltipKey: string;
}[] = [
  { value: 'auto', labelKey: 'autoLabel', tooltipKey: 'autoTooltip' },
  { value: 'bridge', labelKey: 'bridgeLabel', tooltipKey: 'bridgeTooltip' },
  { value: 'host', labelKey: 'hostLabel', tooltipKey: 'hostTooltip' }
];
