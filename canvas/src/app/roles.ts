// Roles to Fluent status tokens. The canvas draws with a light or a dark palette, so each takes the status colors of the matching
// Fluent theme (high contrast uses the dark one: the glyph and the word carry the role there).
import { tokens, webDarkTheme, webLightTheme } from "@fluentui/react-components";
import type { Role } from "../source/presentation";

export const roleColors = (theme: "light" | "dark"): Record<Role, string> => {
  const t = theme === "light" ? webLightTheme : webDarkTheme;
  return { ok: t.colorStatusSuccessForeground1, warning: t.colorStatusWarningForeground1, danger: t.colorStatusDangerForeground1, unknown: t.colorNeutralForeground3, neutral: t.colorNeutralForeground2 };
};
/** CSS variable of the same tokens for the DOM (legend swatches follow whichever Fluent theme is active, high contrast included). */
export const roleCss: Record<Role, string> = { ok: tokens.colorStatusSuccessForeground1, warning: tokens.colorStatusWarningForeground1, danger: tokens.colorStatusDangerForeground1, unknown: tokens.colorNeutralForeground3, neutral: tokens.colorNeutralForeground2 };
