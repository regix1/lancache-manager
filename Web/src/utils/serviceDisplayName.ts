const SERVICE_DISPLAY_NAME_ALIASES: Record<string, string> = {
  xboxlive: 'Xbox',
  microsoft: 'Xbox'
};

/**
 * Returns the display label for a raw service name, folding known Xbox
 * aliases (xboxlive, microsoft) to "Xbox". Mirrors the alias set used by
 * the backend's ServiceBreakdownMerger._xboxAliases. Any other service
 * name is returned unchanged so existing display styling (e.g. the
 * `capitalize` CSS class) continues to apply.
 */
export function getServiceDisplayName(serviceName: string): string {
  const alias = SERVICE_DISPLAY_NAME_ALIASES[serviceName.toLowerCase()];
  return alias ?? serviceName;
}
