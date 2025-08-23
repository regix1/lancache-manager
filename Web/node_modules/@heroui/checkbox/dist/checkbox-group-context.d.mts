import * as react from 'react';
import { ContextType } from './use-checkbox-group.mjs';
import '@heroui/system';
import '@heroui/theme';
import '@react-types/checkbox';
import '@react-types/shared';
import '@heroui/react-utils';
import '@react-stately/checkbox';
import './checkbox.mjs';
import './use-checkbox.mjs';

declare const CheckboxGroupProvider: react.Provider<ContextType>;
declare const useCheckboxGroupContext: () => ContextType;

export { CheckboxGroupProvider, useCheckboxGroupContext };
