/** Show read-only/error placeholder only after directory permissions finish loading. */
export function showPermissionBlock(checkingPermissions: boolean, shouldBlock: boolean): boolean {
  return !checkingPermissions && shouldBlock;
}

/** Keep actions visible while permissions load; hide only once confirmed blocked. */
export function showActionWhileCheckingPermissions(
  checkingPermissions: boolean,
  allowed: boolean
): boolean {
  return checkingPermissions || allowed;
}
