import { useContext } from 'react';
import { TimezoneContext } from './TimezoneContext.types';

export const useTimezone = () => useContext(TimezoneContext);
