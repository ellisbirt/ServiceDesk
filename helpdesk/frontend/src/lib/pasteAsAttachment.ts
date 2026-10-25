/** Decides when pasted text becomes an attachment instead of going into the message box. Pure, so it is easy to test. */

export const DEFAULT_PASTE_MIN_CHARS = 1500;
export const DEFAULT_PASTE_MIN_LINES = 25;

/** How many lines must look like log output, and what share of the non-empty lines they must be. */
const LOG_LINES_REQUIRED = 5;
const LOG_LINE_SHARE = 0.4;

const LOG_LINE_PATTERNS: RegExp[] = [
  // Timestamps: 2024-05-01 12:00:01, 2024-05-01T12:00:01Z, [05/01/2024 ...], 12:00:01.123
  /^\s*\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/,
  /^\s*\[?\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}[ T,]+\d{1,2}:\d{2}/,
  /^\s*\[?\d{2}:\d{2}:\d{2}/,
  // Levels, in capitals as logs write them.
  /\b(?:ERROR|WARN|WARNING|INFO|DEBUG|FATAL|TRACE|CRITICAL)\b/,
  /Exception\b/,
  // Python: the traceback header and its final "SomethingError: message" line.
  /^Traceback \(most recent call last\):/,
  /^\w+(?:Error|Exception)\b/,
  // Stack frames: "   at Foo.Bar(" (.NET, Java), "at fn (file.js:1:2)", 'File "x.py", line 3'.
  /^\s+at\s+[\w$.<>[\]`]+\s*\(/,
  /^\s*at\s+.+:\d+:\d+\)?\s*$/,
  /^\s*File ".*", line \d+/,
];

export interface PasteThresholds {
  pasteMinChars: number;
  pasteMinLines: number;
}

function lines(text: string): string[] {
  return text.split(/\r\n|\r|\n/);
}

/** True when enough of the lines look like a log or a stack trace. */
export function looksLikeLog(text: string): boolean {
  const nonEmpty = lines(text).filter((line) => line.trim() !== '');
  if (nonEmpty.length < LOG_LINES_REQUIRED) return false;
  const matching = nonEmpty.filter((line) => LOG_LINE_PATTERNS.some((pattern) => pattern.test(line))).length;
  return matching >= LOG_LINES_REQUIRED && matching / nonEmpty.length >= LOG_LINE_SHARE;
}

/** True when pasted text is long or tall, or looks like a log, and so belongs in an attachment. */
export function shouldPasteAsAttachment(text: string, thresholds: PasteThresholds): boolean {
  if (text.trim() === '') return false;
  if (text.length >= thresholds.pasteMinChars) return true;
  // A trailing newline does not make another line.
  if (lines(text.replace(/(\r\n|\r|\n)$/, '')).length >= thresholds.pasteMinLines) return true;
  return looksLikeLog(text);
}

const pad = (n: number) => String(n).padStart(2, '0');

/** The local time as YYYYMMDD-HHmmss, for file names. */
export function timestampForFileName(now: Date): string {
  return (
    `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}` +
    `-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`
  );
}

export function pastedLogFileName(now: Date): string {
  return `pasted-log-${timestampForFileName(now)}.txt`;
}

/**
 * Inserts text over the selection (start to end) of a value, keeping the result within maxLength.
 * `truncated` is true when some of the text did not fit.
 */
export function insertAtCaret(
  value: string,
  start: number,
  end: number,
  text: string,
  maxLength: number,
): { value: string; caret: number; truncated: boolean } {
  const from = Math.min(Math.max(0, Math.min(start, end)), value.length);
  const to = Math.min(Math.max(0, Math.max(start, end)), value.length);
  const room = Math.max(0, maxLength - (value.length - (to - from)));
  const inserted = text.slice(0, room);
  return {
    value: value.slice(0, from) + inserted + value.slice(to),
    caret: from + inserted.length,
    truncated: inserted.length < text.length,
  };
}
