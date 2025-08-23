"use client";
import {
  useButtonGroup
} from "./chunk-6XRBX2TW.mjs";
import {
  ButtonGroupProvider
} from "./chunk-3SAWKTTV.mjs";

// src/button-group.tsx
import { forwardRef } from "@heroui/system";
import { jsx } from "react/jsx-runtime";
var ButtonGroup = forwardRef((props, ref) => {
  const { Component, domRef, context, children, classNames, getButtonGroupProps } = useButtonGroup({
    ...props,
    ref
  });
  return /* @__PURE__ */ jsx(ButtonGroupProvider, { value: context, children: /* @__PURE__ */ jsx(Component, { ref: domRef, className: classNames, ...getButtonGroupProps(), children }) });
});
ButtonGroup.displayName = "HeroUI.ButtonGroup";
var button_group_default = ButtonGroup;

export {
  button_group_default
};
