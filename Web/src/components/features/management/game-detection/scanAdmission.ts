/**
 * Whether the server answered at all. A 400 is the download or capability refusal these
 * routes return before admitting anything; a timeout or dropped connection may mean the
 * scan was already accepted. Distinct from `isRefusal`, which may only be used where a
 * route's single 400 meaning is the decline: here both 400 bodies are refusals, and this
 * decides guard release rather than how the message reads.
 */
export function isConfirmedScanRefusalStatus(status: number | undefined): boolean {
  return status === 400;
}
