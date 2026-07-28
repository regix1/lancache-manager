/**
 * Row-shape predicates shared by the two client stats surfaces (the Clients tab
 * and the dashboard's Top Clients table). Both render the same server rows in
 * different layouts, so the shape test lives here rather than being spelled out
 * twice and drifting.
 */

/** The subset of a client stats row that identifies which of the three row kinds it is. */
interface ClientRowIdentity {
  displayName?: string;
  groupId?: number;
  isGrouped: boolean;
}

/** A row that speaks for one member IP of a nickname that reports its members separately:
 *  it carries the nickname and the group id but is not itself the combined group row. */
export const isSeparatedMemberRow = (client: ClientRowIdentity): boolean =>
  !!client.displayName && client.groupId != null && !client.isGrouped;
