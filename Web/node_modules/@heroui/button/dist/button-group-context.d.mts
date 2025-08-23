import * as react from 'react';
import { ContextType } from './use-button-group.mjs';
import '@heroui/system';
import './button.mjs';
import './use-button.mjs';
import '@heroui/theme';
import '@heroui/use-aria-button';
import '@heroui/ripple';
import '@heroui/react-utils';

declare const ButtonGroupProvider: react.Provider<ContextType>;
declare const useButtonGroupContext: () => ContextType;

export { ButtonGroupProvider, useButtonGroupContext };
