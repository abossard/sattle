// The canvas look as Fluent themes: a drawing sheet. Paper and ink, square corners, one drafting blue for selection and focus.
// Amber and red stay with diagnostics; nothing else carries color. High contrast is Fluent's own.
import { createDarkTheme, createLightTheme, teamsHighContrastTheme, type BrandVariants, type Theme } from "@fluentui/react-components";
import type { ThemeName } from "../state/types";

const drafting: BrandVariants = {
  10: "#060d19", 20: "#09192d", 30: "#0d2441", 40: "#103055", 50: "#133c6a", 60: "#16487e", 70: "#1a5392", 80: "#1d5fa6",
  90: "#3e76b3", 100: "#5a8abe", 110: "#759dc9", 120: "#8fb0d4", 130: "#a9c2de", 140: "#c1d4e8", 150: "#dae5f1", 160: "#f2f6fb",
};

const shape: Partial<Theme> = {
  fontFamilyBase: '"Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif',
  fontFamilyMonospace: '"Cascadia Mono", "SF Mono", ui-monospace, Menlo, Consolas, monospace',
  borderRadiusSmall: "1px", borderRadiusMedium: "2px", borderRadiusLarge: "3px", borderRadiusXLarge: "4px",
  shadow2: "0 1px 2px rgba(0,0,0,.10)", shadow4: "0 1px 3px rgba(0,0,0,.12)", shadow8: "0 2px 6px rgba(0,0,0,.14)", shadow16: "0 4px 12px rgba(0,0,0,.16)",
};

const light: Theme = {
  ...createLightTheme(drafting), ...shape,
  colorNeutralBackground1: "#ffffff", colorNeutralBackground2: "#f6f7f8", colorNeutralBackground3: "#eceef1", colorNeutralBackground4: "#e4e6ea",
  colorNeutralForeground1: "#202124", colorNeutralForeground2: "#46484d", colorNeutralForeground3: "#5f6268", colorNeutralForeground4: "#696b70",
  colorNeutralStroke1: "#868990", colorNeutralStroke2: "#c9cbd0", colorNeutralStroke3: "#e3e5e8", colorNeutralStrokeAccessible: "#5f6268",
};

const dark: Theme = {
  ...createDarkTheme(drafting), ...shape,
  colorBrandForeground1: drafting[120], colorBrandForegroundLink: drafting[120], colorCompoundBrandStroke: drafting[110],
  colorNeutralBackground1: "#1b1d20", colorNeutralBackground2: "#17191c", colorNeutralBackground3: "#121316", colorNeutralBackground4: "#0e0f11",
  colorNeutralForeground1: "#e6e7e9", colorNeutralForeground2: "#c3c5ca", colorNeutralForeground3: "#a4a7ad", colorNeutralForeground4: "#989ba1",
  colorNeutralStroke1: "#80838a", colorNeutralStroke2: "#3a3d42", colorNeutralStroke3: "#2c2e32", colorNeutralStrokeAccessible: "#a4a7ad",
};

export const THEMES: Record<ThemeName, Theme> = { light, dark, contrast: teamsHighContrastTheme };
/** The browser's own surfaces (scrollbars, form controls) follow the theme. */
export const colorScheme = (t: ThemeName) => (t === "light" ? "light" : "dark");
