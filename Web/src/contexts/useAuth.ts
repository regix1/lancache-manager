import { AuthContext } from './AuthContext.types';
import { createContextHook } from './createContextHook';

export const useAuth = createContextHook(AuthContext, 'useAuth');
