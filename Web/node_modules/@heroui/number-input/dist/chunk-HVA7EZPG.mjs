"use client";

// src/number-input-stepper.tsx
import { Button } from "@heroui/button";
import { ChevronUpIcon, ChevronDownIcon } from "@heroui/shared-icons";
import { jsx } from "react/jsx-runtime";
var NumberInputStepper = ({ direction, ...otherProps }) => {
  return /* @__PURE__ */ jsx(Button, { disableRipple: true, isIconOnly: true, ...otherProps, children: direction == "up" ? /* @__PURE__ */ jsx(ChevronUpIcon, {}) : /* @__PURE__ */ jsx(ChevronDownIcon, {}) });
};
NumberInputStepper.displayName = "HeroUI.NumberInputStepper";
var number_input_stepper_default = NumberInputStepper;

export {
  number_input_stepper_default
};
