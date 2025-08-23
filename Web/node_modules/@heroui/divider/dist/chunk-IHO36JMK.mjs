import {
  useDivider
} from "./chunk-TS7K35D7.mjs";

// src/divider.tsx
import { forwardRef } from "@heroui/system-rsc";
import { jsx } from "react/jsx-runtime";
var Divider = forwardRef((props, ref) => {
  const { Component, getDividerProps } = useDivider({ ...props });
  return /* @__PURE__ */ jsx(Component, { ref, ...getDividerProps() });
});
Divider.displayName = "HeroUI.Divider";
var divider_default = Divider;

export {
  divider_default
};
