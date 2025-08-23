import {
  useSpacer
} from "./chunk-BRFD4NXM.mjs";

// src/spacer.tsx
import { forwardRef } from "@heroui/system-rsc";
import { jsx } from "react/jsx-runtime";
var Spacer = forwardRef((props, ref) => {
  const { Component, getSpacerProps } = useSpacer({ ...props });
  return /* @__PURE__ */ jsx(Component, { ref, ...getSpacerProps() });
});
Spacer.displayName = "HeroUI.Spacer";
var spacer_default = Spacer;

export {
  spacer_default
};
