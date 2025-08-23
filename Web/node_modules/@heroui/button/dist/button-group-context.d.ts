import * as react from 'react';
import { ContextType } from './use-button-group.js';
import '@heroui/system';
import './button.js';
import './use-button.js';
import '@heroui/theme';
import '@heroui/use-aria-button';
import '@heroui/ripple';
import '@heroui/react-utils';

declare const ButtonGroupProvider: react.Provider<ContextType>;
declare const useButtonGroupContext: () => ContextType;

export { ButtonGroupProvider, useButtonGroupContext };
