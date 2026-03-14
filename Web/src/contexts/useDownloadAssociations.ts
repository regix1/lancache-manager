import { useContext } from 'react';
import {
  DownloadAssociationsContext,
  type DownloadAssociationsContextType
} from './DownloadAssociationsContext.types';

export const useDownloadAssociations = (): DownloadAssociationsContextType => {
  const context = useContext(DownloadAssociationsContext);
  if (context === undefined) {
    throw new Error('useDownloadAssociations must be used within a DownloadAssociationsProvider');
  }
  return context;
};
