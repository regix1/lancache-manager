import { SetupStatusContext } from './SetupStatusContext.types';
import { createContextHook } from './createContextHook';

export const useSetupStatus = createContextHook(SetupStatusContext, 'useSetupStatus');
