import * as react from 'react';
import { ContextType } from './use-card.js';
import '@heroui/system';
import '@react-types/shared';
import '@heroui/theme';
import '@heroui/ripple';
import '@react-aria/interactions';
import '@heroui/react-utils';

declare const CardProvider: react.Provider<ContextType>;
declare const useCardContext: () => ContextType;

export { CardProvider, useCardContext };
