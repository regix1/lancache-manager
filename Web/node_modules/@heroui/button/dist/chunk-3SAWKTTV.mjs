"use client";

// src/button-group-context.ts
import { createContext } from "@heroui/react-utils";
var [ButtonGroupProvider, useButtonGroupContext] = createContext({
  name: "ButtonGroupContext",
  strict: false
});

export {
  ButtonGroupProvider,
  useButtonGroupContext
};
