"use strict";
var __defProp = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __hasOwnProp = Object.prototype.hasOwnProperty;
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};
var __copyProps = (to, from, except, desc) => {
  if (from && typeof from === "object" || typeof from === "function") {
    for (let key of __getOwnPropNames(from))
      if (!__hasOwnProp.call(to, key) && key !== except)
        __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
  }
  return to;
};
var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

// src/components/progress.ts
var progress_exports = {};
__export(progress_exports, {
  circularProgress: () => circularProgress,
  progress: () => progress
});
module.exports = __toCommonJS(progress_exports);

// src/utils/tv.ts
var import_tailwind_variants = require("tailwind-variants");

// src/utilities/transition.ts
var DEFAULT_TRANSITION_DURATION = "250ms";
var transition_default = {
  /**
   * Transition utilities
   */
  ".transition-background": {
    "transition-property": "background",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-colors-opacity": {
    "transition-property": "color, background-color, border-color, text-decoration-color, fill, stroke, opacity",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-width": {
    "transition-property": "width",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-height": {
    "transition-property": "height",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-size": {
    "transition-property": "width, height",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-left": {
    "transition-property": "left",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-transform-opacity": {
    "transition-property": "transform, scale, opacity rotate",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-transform-background": {
    "transition-property": "transform, scale, background",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-transform-colors": {
    "transition-property": "transform, scale, color, background, background-color, border-color, text-decoration-color, fill, stroke",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  },
  ".transition-transform-colors-opacity": {
    "transition-property": "transform, scale, color, background, background-color, border-color, text-decoration-color, fill, stroke, opacity",
    "transition-timing-function": "ease",
    "transition-duration": DEFAULT_TRANSITION_DURATION
  }
};

// src/utilities/custom.ts
var custom_default = {
  /**
   * Custom utilities
   */
  ".leading-inherit": {
    "line-height": "inherit"
  },
  ".bg-img-inherit": {
    "background-image": "inherit"
  },
  ".bg-clip-inherit": {
    "background-clip": "inherit"
  },
  ".text-fill-inherit": {
    "-webkit-text-fill-color": "inherit"
  },
  ".tap-highlight-transparent": {
    "-webkit-tap-highlight-color": "transparent"
  },
  ".input-search-cancel-button-none": {
    "&::-webkit-search-cancel-button": {
      "-webkit-appearance": "none"
    }
  }
};

// src/utilities/scrollbar-hide.ts
var scrollbar_hide_default = {
  /**
   * Scroll Hide
   */
  ".scrollbar-hide": {
    /* IE and Edge */
    "-ms-overflow-style": "none",
    /* Firefox */
    "scrollbar-width": "none",
    /* Safari and Chrome */
    "&::-webkit-scrollbar": {
      display: "none"
    }
  },
  ".scrollbar-default": {
    /* IE and Edge */
    "-ms-overflow-style": "auto",
    /* Firefox */
    "scrollbar-width": "auto",
    /* Safari and Chrome */
    "&::-webkit-scrollbar": {
      display: "block"
    }
  }
};

// src/utilities/text.ts
var text_default = {
  /**
   * Text utilities
   */
  ".text-tiny": {
    "font-size": "var(--heroui-font-size-tiny)",
    "line-height": "var(--heroui-line-height-tiny)"
  },
  ".text-small": {
    "font-size": "var(--heroui-font-size-small)",
    "line-height": "var(--heroui-line-height-small)"
  },
  ".text-medium": {
    "font-size": "var(--heroui-font-size-medium)",
    "line-height": "var(--heroui-line-height-medium)"
  },
  ".text-large": {
    "font-size": "var(--heroui-font-size-large)",
    "line-height": "var(--heroui-line-height-large)"
  }
};

// src/utilities/animation.ts
var animation_default = {
  /** Animation Utilities */
  ".spinner-bar-animation": {
    "animation-delay": "calc(-1.2s + (0.1s * var(--bar-index)))",
    transform: "rotate(calc(30deg * var(--bar-index)))translate(140%)"
  },
  ".spinner-dot-animation": {
    "animation-delay": "calc(250ms * var(--dot-index))"
  },
  ".spinner-dot-blink-animation": {
    "animation-delay": "calc(200ms * var(--dot-index))"
  }
};

// src/utilities/index.ts
var utilities = {
  ...custom_default,
  ...transition_default,
  ...scrollbar_hide_default,
  ...text_default,
  ...animation_default
};

// src/utils/tw-merge-config.ts
var COMMON_UNITS = ["small", "medium", "large"];
var twMergeConfig = {
  theme: {
    spacing: ["divider"],
    radius: COMMON_UNITS
  },
  classGroups: {
    shadow: [{ shadow: COMMON_UNITS }],
    opacity: [{ opacity: ["disabled"] }],
    "font-size": [{ text: ["tiny", ...COMMON_UNITS] }],
    "border-w": [{ border: COMMON_UNITS }],
    "bg-image": [
      "bg-stripe-gradient-default",
      "bg-stripe-gradient-primary",
      "bg-stripe-gradient-secondary",
      "bg-stripe-gradient-success",
      "bg-stripe-gradient-warning",
      "bg-stripe-gradient-danger"
    ],
    transition: Object.keys(utilities).filter((key) => key.includes(".transition")).map((key) => key.replace(".", ""))
    // remove the dot from the key, .transition-background -> transition-background
  }
};

// src/utils/tv.ts
var tv = (options, config) => {
  var _a, _b, _c;
  return (0, import_tailwind_variants.tv)(options, {
    ...config,
    twMerge: (_a = config == null ? void 0 : config.twMerge) != null ? _a : true,
    twMergeConfig: {
      ...config == null ? void 0 : config.twMergeConfig,
      theme: {
        ...(_b = config == null ? void 0 : config.twMergeConfig) == null ? void 0 : _b.theme,
        ...twMergeConfig.theme
      },
      classGroups: {
        ...(_c = config == null ? void 0 : config.twMergeConfig) == null ? void 0 : _c.classGroups,
        ...twMergeConfig.classGroups
      }
    }
  });
};

// src/components/progress.ts
var progress = tv(
  {
    slots: {
      base: "flex flex-col gap-2 w-full",
      label: "",
      labelWrapper: "flex justify-between",
      value: "",
      track: "z-0 relative bg-default-300/50 overflow-hidden rtl:rotate-180",
      indicator: "h-full"
    },
    variants: {
      color: {
        default: {
          indicator: "bg-default-400"
        },
        primary: {
          indicator: "bg-primary"
        },
        secondary: {
          indicator: "bg-secondary"
        },
        success: {
          indicator: "bg-success"
        },
        warning: {
          indicator: "bg-warning"
        },
        danger: {
          indicator: "bg-danger"
        }
      },
      size: {
        sm: {
          label: "text-small",
          value: "text-small",
          track: "h-1"
        },
        md: {
          label: "text-medium",
          value: "text-medium",
          track: "h-3"
        },
        lg: {
          label: "text-large",
          value: "text-large",
          track: "h-5"
        }
      },
      radius: {
        none: {
          track: "rounded-none",
          indicator: "rounded-none"
        },
        sm: {
          track: "rounded-small",
          indicator: "rounded-small"
        },
        md: {
          track: "rounded-medium",
          indicator: "rounded-medium"
        },
        lg: {
          track: "rounded-large",
          indicator: "rounded-large"
        },
        full: {
          track: "rounded-full",
          indicator: "rounded-full"
        }
      },
      isStriped: {
        true: {
          indicator: "bg-stripe-gradient-default bg-stripe-size"
        }
      },
      isIndeterminate: {
        true: {
          indicator: ["absolute", "w-full", "origin-left", "animate-indeterminate-bar"]
        }
      },
      isDisabled: {
        true: {
          base: "opacity-disabled cursor-not-allowed"
        }
      },
      disableAnimation: {
        true: {},
        false: {
          indicator: "transition-transform !duration-500"
        }
      }
    },
    defaultVariants: {
      color: "primary",
      size: "md",
      radius: "full",
      isStriped: false,
      isIndeterminate: false,
      isDisabled: false
    },
    compoundVariants: [
      // disableAnimation && !isIndeterminate
      {
        disableAnimation: true,
        isIndeterminate: false,
        class: {
          indicator: "!transition-none motion-reduce:transition-none"
        }
      },
      {
        color: "primary",
        isStriped: true,
        class: {
          indicator: "bg-stripe-gradient-primary bg-stripe-size"
        }
      },
      {
        color: "secondary",
        isStriped: true,
        class: {
          indicator: "bg-stripe-gradient-secondary bg-stripe-size"
        }
      },
      {
        color: "success",
        isStriped: true,
        class: {
          indicator: "bg-stripe-gradient-success bg-stripe-size"
        }
      },
      {
        color: "warning",
        isStriped: true,
        class: {
          indicator: "bg-stripe-gradient-warning bg-stripe-size"
        }
      },
      {
        color: "danger",
        isStriped: true,
        class: {
          indicator: "bg-stripe-gradient-danger bg-stripe-size"
        }
      }
    ]
  },
  {
    twMerge: true
  }
);
var circularProgress = tv({
  slots: {
    base: "flex flex-col justify-center gap-1 max-w-fit items-center",
    label: "",
    svgWrapper: "relative block",
    svg: "z-0 relative overflow-hidden",
    track: "h-full stroke-default-300/50",
    indicator: "h-full stroke-current",
    value: "absolute font-normal inset-0 flex items-center justify-center"
  },
  variants: {
    color: {
      default: {
        svg: "text-default-400"
      },
      primary: {
        svg: "text-primary"
      },
      secondary: {
        svg: "text-secondary"
      },
      success: {
        svg: "text-success"
      },
      warning: {
        svg: "text-warning"
      },
      danger: {
        svg: "text-danger"
      }
    },
    size: {
      sm: {
        svg: "w-8 h-8",
        label: "text-small",
        value: "text-[0.5rem]"
      },
      md: {
        svg: "w-10 h-10",
        label: "text-small",
        value: "text-[0.55rem]"
      },
      lg: {
        svg: "w-12 h-12",
        label: "text-medium",
        value: "text-[0.6rem]"
      }
    },
    isIndeterminate: {
      true: {
        svg: "animate-spinner-ease-spin"
      }
    },
    isDisabled: {
      true: {
        base: "opacity-disabled cursor-not-allowed"
      }
    },
    disableAnimation: {
      true: {},
      false: {
        indicator: "transition-all !duration-500"
      }
    }
  },
  defaultVariants: {
    color: "primary",
    size: "md",
    isDisabled: false
  },
  compoundVariants: [
    // disableAnimation && !isIndeterminate
    {
      disableAnimation: true,
      isIndeterminate: false,
      class: {
        svg: "!transition-none motion-reduce:transition-none"
      }
    }
  ]
});
// Annotate the CommonJS export names for ESM import in node:
0 && (module.exports = {
  circularProgress,
  progress
});
