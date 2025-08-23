import {
  tv
} from "./chunk-TX3FPB7D.mjs";

// src/components/autocomplete.ts
var autocomplete = tv({
  slots: {
    base: "group inline-flex flex-column w-full",
    listboxWrapper: "scroll-py-6 w-full",
    listbox: "",
    popoverContent: "w-full p-1 overflow-hidden",
    endContentWrapper: "relative flex h-full items-center -mr-2",
    clearButton: [
      "text-medium",
      "translate-x-1",
      "cursor-text",
      "opacity-0",
      "pointer-events-none",
      "text-default-500",
      "group-data-[invalid=true]:text-danger",
      "data-[visible=true]:opacity-100",
      // on mobile is always visible when there is a value
      "data-[visible=true]:pointer-events-auto",
      "data-[visible=true]:cursor-pointer",
      "sm:data-[visible=true]:opacity-0",
      // only visible on hover
      "sm:data-[visible=true]:pointer-events-none",
      "sm:group-data-[hover=true]:data-[visible=true]:opacity-100",
      "sm:group-data-[hover=true]:data-[visible=true]:pointer-events-auto"
    ],
    selectorButton: "text-medium"
  },
  variants: {
    isClearable: {
      true: {},
      false: {
        clearButton: "hidden"
      }
    },
    disableAnimation: {
      true: {
        selectorButton: "transition-none"
      },
      false: {
        selectorButton: "transition-transform duration-150 ease motion-reduce:transition-none"
      }
    },
    disableSelectorIconRotation: {
      true: {},
      false: {
        selectorButton: "data-[open=true]:rotate-180"
      }
    }
  },
  defaultVariants: {
    isClearable: true,
    disableSelectorIconRotation: false
  }
});

export {
  autocomplete
};
