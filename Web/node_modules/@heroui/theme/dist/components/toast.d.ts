import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

declare const toastRegion: tailwind_variants.TVReturnType<{
    disableAnimation: {
        false: {
            base: string;
        };
        true: {
            base: string[];
        };
    };
}, {
    base: string;
}, undefined, {
    disableAnimation: {
        false: {
            base: string;
        };
        true: {
            base: string[];
        };
    };
}, {
    base: string;
}, tailwind_variants.TVReturnType<{
    disableAnimation: {
        false: {
            base: string;
        };
        true: {
            base: string[];
        };
    };
}, {
    base: string;
}, undefined, unknown, unknown, undefined>>;
declare const toast: tailwind_variants.TVReturnType<{
    size: {
        sm: {
            icon: string;
            loadingComponent: string;
        };
        md: {};
        lg: {};
    };
    variant: {
        flat: string;
        solid: string;
        bordered: string;
    };
    color: {
        default: string;
        foreground: {
            progressIndicator: string;
        };
        primary: {
            progressIndicator: string;
        };
        secondary: {
            progressIndicator: string;
        };
        success: {
            progressIndicator: string;
        };
        warning: {
            progressIndicator: string;
        };
        danger: {
            progressIndicator: string;
        };
    };
    radius: {
        none: {
            base: string;
            progressTrack: string;
        };
        sm: {
            base: string;
            progressTrack: string;
        };
        md: {
            base: string;
            progressTrack: string;
        };
        lg: {
            base: string;
            progressTrack: string;
        };
        full: {
            base: string;
            closeButton: string;
            progressTrack: string;
        };
    };
    disableAnimation: {
        true: {
            closeButton: string;
            base: string;
        };
        false: {
            closeButton: string;
            base: string[];
        };
    };
    shadow: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
    };
}, {
    base: string[];
    wrapper: string[];
    title: string[];
    description: string[];
    icon: string[];
    loadingComponent: string[];
    content: string[];
    progressTrack: string[];
    progressIndicator: string[];
    motionDiv: string[];
    closeButton: string[];
    closeIcon: string[];
}, undefined, {
    size: {
        sm: {
            icon: string;
            loadingComponent: string;
        };
        md: {};
        lg: {};
    };
    variant: {
        flat: string;
        solid: string;
        bordered: string;
    };
    color: {
        default: string;
        foreground: {
            progressIndicator: string;
        };
        primary: {
            progressIndicator: string;
        };
        secondary: {
            progressIndicator: string;
        };
        success: {
            progressIndicator: string;
        };
        warning: {
            progressIndicator: string;
        };
        danger: {
            progressIndicator: string;
        };
    };
    radius: {
        none: {
            base: string;
            progressTrack: string;
        };
        sm: {
            base: string;
            progressTrack: string;
        };
        md: {
            base: string;
            progressTrack: string;
        };
        lg: {
            base: string;
            progressTrack: string;
        };
        full: {
            base: string;
            closeButton: string;
            progressTrack: string;
        };
    };
    disableAnimation: {
        true: {
            closeButton: string;
            base: string;
        };
        false: {
            closeButton: string;
            base: string[];
        };
    };
    shadow: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
    };
}, {
    base: string[];
    wrapper: string[];
    title: string[];
    description: string[];
    icon: string[];
    loadingComponent: string[];
    content: string[];
    progressTrack: string[];
    progressIndicator: string[];
    motionDiv: string[];
    closeButton: string[];
    closeIcon: string[];
}, tailwind_variants.TVReturnType<{
    size: {
        sm: {
            icon: string;
            loadingComponent: string;
        };
        md: {};
        lg: {};
    };
    variant: {
        flat: string;
        solid: string;
        bordered: string;
    };
    color: {
        default: string;
        foreground: {
            progressIndicator: string;
        };
        primary: {
            progressIndicator: string;
        };
        secondary: {
            progressIndicator: string;
        };
        success: {
            progressIndicator: string;
        };
        warning: {
            progressIndicator: string;
        };
        danger: {
            progressIndicator: string;
        };
    };
    radius: {
        none: {
            base: string;
            progressTrack: string;
        };
        sm: {
            base: string;
            progressTrack: string;
        };
        md: {
            base: string;
            progressTrack: string;
        };
        lg: {
            base: string;
            progressTrack: string;
        };
        full: {
            base: string;
            closeButton: string;
            progressTrack: string;
        };
    };
    disableAnimation: {
        true: {
            closeButton: string;
            base: string;
        };
        false: {
            closeButton: string;
            base: string[];
        };
    };
    shadow: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
    };
}, {
    base: string[];
    wrapper: string[];
    title: string[];
    description: string[];
    icon: string[];
    loadingComponent: string[];
    content: string[];
    progressTrack: string[];
    progressIndicator: string[];
    motionDiv: string[];
    closeButton: string[];
    closeIcon: string[];
}, undefined, unknown, unknown, undefined>>;
type ToastVariantProps = VariantProps<typeof toast>;
type ToastSlots = keyof ReturnType<typeof toast>;
type ToastRegionVariantProps = VariantProps<typeof toastRegion>;
type ToastRegionSlots = keyof ReturnType<typeof toastRegion>;

export { type ToastRegionSlots, type ToastRegionVariantProps, type ToastSlots, type ToastVariantProps, toast, toastRegion };
