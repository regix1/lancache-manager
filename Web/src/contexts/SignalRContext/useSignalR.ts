import { createContextHook } from '../createContextHook';
import { SignalRContext } from './SignalRContext.types';

export const useSignalR = createContextHook(SignalRContext, 'useSignalR');
