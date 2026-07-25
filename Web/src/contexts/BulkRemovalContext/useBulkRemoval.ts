import { createContextHook } from '../createContextHook';
import { BulkRemovalContext } from './BulkRemovalContext.types';

export const useBulkRemoval = createContextHook(BulkRemovalContext, 'useBulkRemoval');
