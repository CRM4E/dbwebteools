import type { InputMask } from "./api";

const characterSetLabels = {
  letters: "letters",
  digits: "numbers",
  alphanumeric: "letters and numbers",
} as const;

type ExactToken = {
  kind: "literal" | "letter" | "digit" | "alphanumeric";
  required: boolean;
  literal?: string;
};

function exactTokens(pattern: string): { tokens?: ExactToken[]; error?: string } {
  if (pattern.length < 1 || pattern.length > 1024)
    return { error: "Exact mask patterns must be between 1 and 1024 characters." };
  const tokens: ExactToken[] = [];
  let hasPlaceholder = false;
  for (let index = 0; index < pattern.length; index += 1) {
    const symbol = pattern[index];
    if (symbol < " " || symbol > "~")
      return { error: "Exact mask patterns may contain printable characters only." };
    let token: ExactToken;
    if (symbol === "\\") {
      index += 1;
      if (index >= pattern.length)
        return { error: "An exact mask cannot end with an escape character." };
      if (!"#AX?\\".includes(pattern[index]))
        return { error: "Only mask symbols need an escape character." };
      token = { kind: "literal", required: true, literal: pattern[index] };
    } else {
      if (symbol === "?")
        return { error: "An optional marker must follow a mask position." };
      token =
        symbol === "#" ? { kind: "digit", required: true }
        : symbol === "A" ? { kind: "letter", required: true }
        : symbol === "X" ? { kind: "alphanumeric", required: true }
        : { kind: "literal", required: true, literal: symbol };
    }
    if (pattern[index + 1] === "?") {
      token = { ...token, required: false };
      index += 1;
    }
    hasPlaceholder ||= token.kind !== "literal";
    tokens.push(token);
    if (tokens.length > 256)
      return { error: "Exact masks may contain at most 256 positions." };
  }
  if (!hasPlaceholder)
    return { error: "An exact mask must contain at least one character placeholder." };
  return { tokens };
}

export function maskTip(mask: InputMask): string {
  if (mask.pattern != null)
    return `Format: ${mask.pattern}. # is a number, A a letter, and X a letter or number. Add ? after any position to make it optional. Other characters are required literals; use \\ to escape #, A, X, ?, or \\.`;
  const required = mask.requiredCharacters || "";
  const characterSet = mask.characterSet || "alphanumeric";
  return `Use at least ${mask.minimumLength || 1} characters. Allowed: ${characterSetLabels[characterSet]}${required ? " plus the required characters." : "."}${required ? ` Include each of these characters: ${[...required].map((character) => `'${character}'`).join(" ")}.` : ""}`;
}

export function maskConfigurationError(mask: InputMask): string | null {
  if (mask.pattern != null) {
    if (
      (mask.characterSet != null && mask.characterSet !== "") ||
      (mask.minimumLength != null && mask.minimumLength !== 1) ||
      (mask.requiredCharacters != null && mask.requiredCharacters !== "")
    )
      return "Exact masks cannot be combined with character rules.";
    return exactTokens(mask.pattern).error || null;
  }
  if (!mask.characterSet || !["letters", "digits", "alphanumeric"].includes(mask.characterSet))
    return "Choose a supported character set.";
  const minimumLength = mask.minimumLength ?? 1;
  if (!Number.isInteger(minimumLength) || minimumLength < 1 || minimumLength > 4000)
    return "Minimum length must be between 1 and 4000.";
  const required = mask.requiredCharacters || "";
  if (required.length > 16)
    return "Use at most 16 required characters.";
  if ([...required].some((character) => character < "!" || character > "~" || /[A-Za-z0-9]/.test(character)))
    return "Required characters must be punctuation without spaces.";
  if (new Set(required).size !== required.length)
    return "Required characters must not be repeated.";
  if (minimumLength < required.length)
    return "Minimum length must allow all required characters.";
  return null;
}

export function maskMinimumLength(mask: InputMask): number {
  if (mask.pattern == null) return mask.minimumLength ?? 1;
  return exactTokens(mask.pattern).tokens?.filter((token) => token.required).length ?? 0;
}

export function maskMaximumLength(mask: InputMask): number | undefined {
  if (mask.pattern == null) return undefined;
  return exactTokens(mask.pattern).tokens?.length;
}

// HTML pattern expressions are generated from the bounded mask grammar; mask
// authors never provide arbitrary regular expressions.
export function maskRegexPattern(mask: InputMask): string | undefined {
  if (mask.pattern == null) {
    return mask.characterSet === "digits" && !(mask.requiredCharacters || "")
      ? `[0-9]{${mask.minimumLength ?? 1},}`
      : undefined;
  }
  const tokens = exactTokens(mask.pattern).tokens;
  if (!tokens) return undefined;
  return tokens.map((token) => {
    const atom =
      token.kind === "digit" ? "[0-9]"
      : token.kind === "letter" ? "[A-Za-z]"
      : token.kind === "alphanumeric" ? "[A-Za-z0-9]"
      : `\\x${token.literal!.charCodeAt(0).toString(16).padStart(2, "0")}`;
    return token.required ? atom : `(?:${atom})?`;
  }).join("");
}

function exactMaskMatches(tokens: ExactToken[], value: string): boolean {
  let positions = new Set([0]);
  for (const token of tokens) {
    const next = new Set<number>();
    for (const position of positions) {
      if (!token.required) next.add(position);
      const character = value[position];
      if (character === undefined) continue;
      const matches =
        token.kind === "literal" ? character === token.literal
        : token.kind === "letter" ? /^[A-Za-z]$/.test(character)
        : token.kind === "digit" ? /^[0-9]$/.test(character)
        : /^[A-Za-z0-9]$/.test(character);
      if (matches) next.add(position + 1);
    }
    positions = next;
    if (positions.size === 0) return false;
  }
  return positions.has(value.length);
}

export function maskValueError(mask: InputMask, value: unknown): string | null {
  if (value == null || value === "") return null;
  if (typeof value !== "string") return maskTip(mask);
  if (mask.pattern != null) {
    const parsed = exactTokens(mask.pattern);
    return parsed.tokens && exactMaskMatches(parsed.tokens, value) ? null : maskTip(mask);
  }
  const required = mask.requiredCharacters || "";
  const base =
    mask.characterSet === "letters"
      ? /^[A-Za-z]$/
      : mask.characterSet === "digits"
        ? /^[0-9]$/
        : /^[A-Za-z0-9]$/;
  if (
    value.length < (mask.minimumLength ?? 1) ||
    [...value].some((character) => !base.test(character) && !required.includes(character)) ||
    [...required].some((character) => !value.includes(character))
  )
    return maskTip(mask);
  return null;
}
