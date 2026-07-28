import { ClientHostnameContext } from './ClientHostnameContext.types';
import { createContextHook } from './createContextHook';

export const useClientHostnames = createContextHook(ClientHostnameContext, 'useClientHostnames');
