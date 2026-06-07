/** Show read-only/error placeholder only after directory permissions finish loading. */
export function showPermissionBlock(checkingPermissions: boolean, shouldBlock: boolean): boolean {
  return !checkingPermissions && shouldBlock;
}
