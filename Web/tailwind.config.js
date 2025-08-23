// This file is optional in Tailwind v4 but can help maintain consistency
// Most configuration is now done via CSS @theme directive

export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class', // This is now handled by @custom-variant in CSS
  theme: {
    // Most theme customization should be done in CSS with @theme
    // This is here for backwards compatibility and IDE support
  },
  plugins: [],
}