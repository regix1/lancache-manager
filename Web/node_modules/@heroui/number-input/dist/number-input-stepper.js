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

// src/number-input-stepper.tsx
var number_input_stepper_exports = {};
__export(number_input_stepper_exports, {
  default: () => number_input_stepper_default
});
module.exports = __toCommonJS(number_input_stepper_exports);
var import_button = require("@heroui/button");
var import_shared_icons = require("@heroui/shared-icons");
var import_jsx_runtime = require("react/jsx-runtime");
var NumberInputStepper = ({ direction, ...otherProps }) => {
  return /* @__PURE__ */ (0, import_jsx_runtime.jsx)(import_button.Button, { disableRipple: true, isIconOnly: true, ...otherProps, children: direction == "up" ? /* @__PURE__ */ (0, import_jsx_runtime.jsx)(import_shared_icons.ChevronUpIcon, {}) : /* @__PURE__ */ (0, import_jsx_runtime.jsx)(import_shared_icons.ChevronDownIcon, {}) });
};
NumberInputStepper.displayName = "HeroUI.NumberInputStepper";
var number_input_stepper_default = NumberInputStepper;
