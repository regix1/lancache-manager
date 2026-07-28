import type { ClientHostnamesReason } from '@services/api.service';

// One i18n key per reason, or null when nothing should render. A Record keyed by every member of
// ClientHostnamesReason forces this map to stay exhaustive - adding a reason to the union without
// adding it here fails the build instead of silently rendering nothing.
const reasonTranslationKeys: Readonly<Record<ClientHostnamesReason, string | null>> = {
  none: null,
  noClients: null,
  noResolver: 'management.sections.clients.hostnames.reasonNoResolver',
  noRecords: 'management.sections.clients.hostnames.reasonNoRecords',
  resolverTimeout: 'management.sections.clients.hostnames.reasonResolverTimeout',
  someUnnamed: 'management.sections.clients.hostnames.reasonSomeUnnamed',
  stillLooking: 'management.sections.clients.hostnames.reasonStillLooking'
};

export function getClientHostnameReasonKey(reason: ClientHostnamesReason): string | null {
  return reasonTranslationKeys[reason];
}
