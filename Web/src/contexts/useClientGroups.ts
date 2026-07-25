import { ClientGroupContext } from './ClientGroupContext.types';
import { createContextHook } from './createContextHook';

export const useClientGroups = createContextHook(ClientGroupContext, 'useClientGroups');
