import * as react from 'react';
import { ContextType } from './use-radio-group.mjs';
import '@heroui/system';
import '@react-types/radio';
import '@react-types/shared';
import '@heroui/react-utils';
import '@heroui/theme';
import '@react-stately/radio';
import './radio.mjs';
import './use-radio.mjs';

declare const RadioGroupProvider: react.Provider<ContextType>;
declare const useRadioGroupContext: () => ContextType;

export { RadioGroupProvider, useRadioGroupContext };
