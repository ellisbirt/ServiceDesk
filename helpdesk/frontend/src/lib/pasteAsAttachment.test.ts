import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PASTE_MIN_CHARS,
  DEFAULT_PASTE_MIN_LINES,
  insertAtCaret,
  looksLikeLog,
  pastedLogFileName,
  shouldPasteAsAttachment,
  timestampForFileName,
} from './pasteAsAttachment';

const thresholds = { pasteMinChars: DEFAULT_PASTE_MIN_CHARS, pasteMinLines: DEFAULT_PASTE_MIN_LINES };

const dotNetLog = [
  '2024-05-01 12:00:01.123 [ERROR] Unhandled exception while processing request',
  'System.InvalidOperationException: Sequence contains no elements',
  '   at System.Linq.ThrowHelper.ThrowNoElementsException()',
  '   at Orders.Repo.Save(Order order) in C:\\src\\Repo.cs:line 42',
  '   at Orders.Api.Post(Order order) in C:\\src\\Api.cs:line 17',
  '   at Microsoft.AspNetCore.Mvc.Invoke()',
].join('\n');

const pythonTrace = [
  'Traceback (most recent call last):',
  '  File "app.py", line 10, in <module>',
  '    main()',
  '  File "app.py", line 6, in main',
  '    run()',
  '  File "worker.py", line 22, in run',
  '    raise RuntimeError("boom")',
  'RuntimeError: boom',
].join('\n');

const jsTrace = [
  'TypeError: Cannot read properties of undefined',
  '    at render (http://localhost:5173/src/App.tsx:120:15)',
  '    at update (http://localhost:5173/src/lib.js:44:9)',
  '    at flush (http://localhost:5173/src/lib.js:60:3)',
  '    at run (http://localhost:5173/src/lib.js:70:1)',
  '    at main (http://localhost:5173/src/main.tsx:5:1)',
].join('\n');

describe('shouldPasteAsAttachment', () => {
  it.each([
    ['a short sentence', 'My laptop will not turn on.'],
    ['a few lines of ordinary prose', 'Hi,\nmy screen flickers.\nIt started yesterday.\nThanks'],
    ['an error message on one line', 'ERROR: Access is denied (0x80070005)'],
    ['a short code snippet', 'const a = 1;\nconst b = 2;\nconsole.log(a + b);'],
    ['nothing but whitespace', '   \n\n  '],
    ['an empty paste', ''],
  ])('keeps %s in the message box', (_name, text) => {
    expect(shouldPasteAsAttachment(text, thresholds)).toBe(false);
  });

  it('converts text at the character threshold, but not one character below', () => {
    expect(shouldPasteAsAttachment('x'.repeat(DEFAULT_PASTE_MIN_CHARS - 1), thresholds)).toBe(false);
    expect(shouldPasteAsAttachment('x'.repeat(DEFAULT_PASTE_MIN_CHARS), thresholds)).toBe(true);
  });

  it('converts text at the line threshold, but not one line below', () => {
    const twentyFour = Array.from({ length: DEFAULT_PASTE_MIN_LINES - 1 }, (_, i) => `line ${i}`).join('\n');
    expect(shouldPasteAsAttachment(twentyFour, thresholds)).toBe(false);
    expect(shouldPasteAsAttachment(`${twentyFour}\nlast line`, thresholds)).toBe(true);
  });

  it('does not count a trailing newline as another line', () => {
    const justUnder = Array.from({ length: DEFAULT_PASTE_MIN_LINES - 1 }, (_, i) => `line ${i}`).join('\n');
    expect(shouldPasteAsAttachment(`${justUnder}\n`, thresholds)).toBe(false);
    expect(shouldPasteAsAttachment(`${justUnder}\r\n`, thresholds)).toBe(false);
  });

  it('honours thresholds from the server', () => {
    expect(shouldPasteAsAttachment('x'.repeat(300), { pasteMinChars: 200, pasteMinLines: 25 })).toBe(true);
    expect(shouldPasteAsAttachment('a\nb\nc', { pasteMinChars: 1500, pasteMinLines: 3 })).toBe(true);
  });

  it.each([
    ['a .NET stack trace with a timestamped error', dotNetLog],
    ['a Python traceback', pythonTrace],
    ['a JavaScript stack trace', jsTrace],
  ])('converts %s even though it is short', (_name, text) => {
    expect(text.length).toBeLessThan(DEFAULT_PASTE_MIN_CHARS);
    expect(shouldPasteAsAttachment(text, thresholds)).toBe(true);
  });
});

describe('looksLikeLog', () => {
  it('recognises timestamped log lines', () => {
    const log = [
      '2024-05-01T12:00:01Z service started',
      '2024-05-01T12:00:02Z listening on 8080',
      '2024-05-01T12:00:03Z client connected',
      '2024-05-01T12:00:04Z request received',
      '2024-05-01T12:00:05Z request completed',
    ].join('\n');
    expect(looksLikeLog(log)).toBe(true);
  });

  it('recognises time-only and log-level lines', () => {
    const log = [
      '12:00:01 INFO starting',
      '12:00:02 DEBUG config loaded',
      '12:00:03 WARN slow response',
      '12:00:04 ERROR connection refused',
      '12:00:05 FATAL giving up',
    ].join('\n');
    expect(looksLikeLog(log)).toBe(true);
  });

  it('needs at least five matching lines', () => {
    const fourLines = dotNetLog.split('\n').slice(0, 4).join('\n');
    expect(looksLikeLog(fourLines)).toBe(false);
  });

  it('is not fooled by a few log-like words in a long message', () => {
    const prose = [
      'Hi team,',
      'I keep getting an ERROR when I open the app.',
      'It worked fine last week and nothing changed on my side.',
      'I have restarted twice and cleared the cache.',
      'My colleague sees the same thing and gets a WARNING too.',
      'We both use the same VPN profile.',
      'Could somebody take a look this afternoon?',
      'It is blocking the month end report.',
      'Thanks very much,',
      'Sam',
    ].join('\n');
    expect(looksLikeLog(prose)).toBe(false);
  });

  it('does not treat lower-case words as log levels', () => {
    const text = Array.from({ length: 8 }, () => 'there was an error and a warning in the info panel').join('\n');
    expect(looksLikeLog(text)).toBe(false);
  });
});

describe('file names', () => {
  it('formats a local timestamp as YYYYMMDD-HHmmss', () => {
    expect(timestampForFileName(new Date(2026, 9, 24, 8, 5, 9))).toBe('20261024-080509');
  });

  it('names a pasted log as a text file', () => {
    expect(pastedLogFileName(new Date(2026, 0, 2, 3, 4, 5))).toBe('pasted-log-20260102-030405.txt');
  });
});

describe('insertAtCaret', () => {
  it('inserts at the caret', () => {
    expect(insertAtCaret('hello world', 5, 5, ',', 100)).toEqual({ value: 'hello, world', caret: 6, truncated: false });
  });

  it('replaces the selection', () => {
    expect(insertAtCaret('hello world', 6, 11, 'there', 100)).toEqual({ value: 'hello there', caret: 11, truncated: false });
  });

  it('accepts a selection given backwards', () => {
    expect(insertAtCaret('abcdef', 4, 2, 'X', 100).value).toBe('abXef');
  });

  it('clamps positions that no longer fit the value', () => {
    expect(insertAtCaret('abc', 10, 20, 'Z', 100)).toEqual({ value: 'abcZ', caret: 4, truncated: false });
  });

  it('keeps the result within the maximum length and says when text was cut', () => {
    const result = insertAtCaret('ab', 1, 1, 'XXXXXXXX', 6);
    expect(result.value).toBe('aXXXXb');
    expect(result.value).toHaveLength(6);
    expect(result.truncated).toBe(true);
  });

  it('counts the selection being replaced as free room', () => {
    expect(insertAtCaret('abcdef', 0, 6, 'ZZZZZZ', 6)).toEqual({ value: 'ZZZZZZ', caret: 6, truncated: false });
  });
});
