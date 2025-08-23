"use client";
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

// src/spinner.tsx
var spinner_exports = {};
__export(spinner_exports, {
  default: () => spinner_default
});
module.exports = __toCommonJS(spinner_exports);
var import_system_rsc2 = require("@heroui/system-rsc");

// src/use-spinner.ts
var import_system_rsc = require("@heroui/system-rsc");
var import_theme = require("@heroui/theme");
var import_shared_utils = require("@heroui/shared-utils");
var import_react = require("react");
var import_system = require("@heroui/system");
function useSpinner(originalProps) {
  var _a, _b;
  const [props, variantProps] = (0, import_system_rsc.mapPropsVariants)(originalProps, import_theme.spinner.variantKeys);
  const globalContext = (0, import_system.useProviderContext)();
  const variant = (_b = (_a = originalProps == null ? void 0 : originalProps.variant) != null ? _a : globalContext == null ? void 0 : globalContext.spinnerVariant) != null ? _b : "default";
  const { children, className, classNames, label: labelProp, ...otherProps } = props;
  const slots = (0, import_react.useMemo)(() => (0, import_theme.spinner)({ ...variantProps }), [(0, import_shared_utils.objectToDeps)(variantProps)]);
  const baseStyles = (0, import_shared_utils.clsx)(classNames == null ? void 0 : classNames.base, className);
  const label = labelProp || children;
  const ariaLabel = (0, import_react.useMemo)(() => {
    if (label && typeof label === "string") {
      return label;
    }
    return !otherProps["aria-label"] ? "Loading" : "";
  }, [children, label, otherProps["aria-label"]]);
  const getSpinnerProps = (0, import_react.useCallback)(
    () => ({
      "aria-label": ariaLabel,
      className: slots.base({
        class: baseStyles
      }),
      ...otherProps
    }),
    [ariaLabel, slots, baseStyles, otherProps]
  );
  return { label, slots, classNames, variant, getSpinnerProps };
}

// src/spinner.tsx
var import_jsx_runtime = require("react/jsx-runtime");
var Spinner = (0, import_system_rsc2.forwardRef)((props, ref) => {
  const { slots, classNames, label, variant, getSpinnerProps } = useSpinner({ ...props });
  if (variant === "wave" || variant === "dots") {
    return /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [...new Array(3)].map((_, index) => /* @__PURE__ */ (0, import_jsx_runtime.jsx)(
        "i",
        {
          className: slots.dots({ class: classNames == null ? void 0 : classNames.dots }),
          style: {
            "--dot-index": index
          }
        },
        `dot-${index}`
      )) }),
      label && /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  if (variant === "simple") {
    return /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ (0, import_jsx_runtime.jsxs)(
        "svg",
        {
          className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }),
          fill: "none",
          viewBox: "0 0 24 24",
          children: [
            /* @__PURE__ */ (0, import_jsx_runtime.jsx)(
              "circle",
              {
                className: slots.circle1({ class: classNames == null ? void 0 : classNames.circle1 }),
                cx: "12",
                cy: "12",
                r: "10",
                stroke: "currentColor",
                strokeWidth: "4"
              }
            ),
            /* @__PURE__ */ (0, import_jsx_runtime.jsx)(
              "path",
              {
                className: slots.circle2({ class: classNames == null ? void 0 : classNames.circle2 }),
                d: "M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z",
                fill: "currentColor"
              }
            )
          ]
        }
      ),
      label && /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  if (variant === "spinner") {
    return /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ (0, import_jsx_runtime.jsx)("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [...new Array(12)].map((_, index) => /* @__PURE__ */ (0, import_jsx_runtime.jsx)(
        "i",
        {
          className: slots.spinnerBars({ class: classNames == null ? void 0 : classNames.spinnerBars }),
          style: {
            "--bar-index": index
          }
        },
        `star-${index}`
      )) }),
      label && /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  return /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", { ref, ...getSpinnerProps(), children: [
    /* @__PURE__ */ (0, import_jsx_runtime.jsxs)("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [
      /* @__PURE__ */ (0, import_jsx_runtime.jsx)("i", { className: slots.circle1({ class: classNames == null ? void 0 : classNames.circle1 }) }),
      /* @__PURE__ */ (0, import_jsx_runtime.jsx)("i", { className: slots.circle2({ class: classNames == null ? void 0 : classNames.circle2 }) })
    ] }),
    label && /* @__PURE__ */ (0, import_jsx_runtime.jsx)("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
  ] });
});
Spinner.displayName = "HeroUI.Spinner";
var spinner_default = Spinner;
