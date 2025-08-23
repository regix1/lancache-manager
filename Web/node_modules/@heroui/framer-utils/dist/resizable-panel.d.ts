import * as react from 'react';
import { HTMLHeroUIProps } from '@heroui/system';

/**
 * Props for the ResizablePanel component.
 */
interface ResizablePanelProps extends HTMLHeroUIProps<"div"> {
}
declare const ResizablePanel: react.ForwardRefExoticComponent<ResizablePanelProps & react.RefAttributes<HTMLDivElement>>;

export { ResizablePanel, type ResizablePanelProps };
