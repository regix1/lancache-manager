import { DownloadAssociationsContext } from './DownloadAssociationsContext.types';
import { createContextHook } from './createContextHook';

export const useDownloadAssociations = createContextHook(
  DownloadAssociationsContext,
  'useDownloadAssociations'
);
