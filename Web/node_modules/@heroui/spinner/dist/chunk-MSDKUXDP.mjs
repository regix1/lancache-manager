"use client";
import {
  useSpinner
} from "./chunk-IKKYW34A.mjs";

// src/spinner.tsx
import { forwardRef } from "@heroui/system-rsc";
import { jsx, jsxs } from "react/jsx-runtime";
var Spinner = forwardRef((props, ref) => {
  const { slots, classNames, label, variant, getSpinnerProps } = useSpinner({ ...props });
  if (variant === "wave" || variant === "dots") {
    return /* @__PURE__ */ jsxs("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ jsx("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [...new Array(3)].map((_, index) => /* @__PURE__ */ jsx(
        "i",
        {
          className: slots.dots({ class: classNames == null ? void 0 : classNames.dots }),
          style: {
            "--dot-index": index
          }
        },
        `dot-${index}`
      )) }),
      label && /* @__PURE__ */ jsx("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  if (variant === "simple") {
    return /* @__PURE__ */ jsxs("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ jsxs(
        "svg",
        {
          className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }),
          fill: "none",
          viewBox: "0 0 24 24",
          children: [
            /* @__PURE__ */ jsx(
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
            /* @__PURE__ */ jsx(
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
      label && /* @__PURE__ */ jsx("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  if (variant === "spinner") {
    return /* @__PURE__ */ jsxs("div", { ref, ...getSpinnerProps(), children: [
      /* @__PURE__ */ jsx("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [...new Array(12)].map((_, index) => /* @__PURE__ */ jsx(
        "i",
        {
          className: slots.spinnerBars({ class: classNames == null ? void 0 : classNames.spinnerBars }),
          style: {
            "--bar-index": index
          }
        },
        `star-${index}`
      )) }),
      label && /* @__PURE__ */ jsx("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
    ] });
  }
  return /* @__PURE__ */ jsxs("div", { ref, ...getSpinnerProps(), children: [
    /* @__PURE__ */ jsxs("div", { className: slots.wrapper({ class: classNames == null ? void 0 : classNames.wrapper }), children: [
      /* @__PURE__ */ jsx("i", { className: slots.circle1({ class: classNames == null ? void 0 : classNames.circle1 }) }),
      /* @__PURE__ */ jsx("i", { className: slots.circle2({ class: classNames == null ? void 0 : classNames.circle2 }) })
    ] }),
    label && /* @__PURE__ */ jsx("span", { className: slots.label({ class: classNames == null ? void 0 : classNames.label }), children: label })
  ] });
});
Spinner.displayName = "HeroUI.Spinner";
var spinner_default = Spinner;

export {
  spinner_default
};
