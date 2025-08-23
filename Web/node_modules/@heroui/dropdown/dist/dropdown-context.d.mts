import * as react from 'react';
import { UseDropdownReturn } from './use-dropdown.mjs';
import '@heroui/popover';
import '@react-types/menu';
import '@heroui/system';
import '@heroui/react-utils';
import '@heroui/menu';

declare const DropdownProvider: react.Provider<UseDropdownReturn>;
declare const useDropdownContext: () => UseDropdownReturn;

export { DropdownProvider, useDropdownContext };
