import { afterEach, describe, expect, it, vi } from 'vitest';
import { readRetryAfter } from './api';

afterEach(() => vi.useRealTimers());

describe('readRetryAfter', () => {
  it.each([null, '', ' ', '-1', 'NaN', 'Infinity', 'not a date'])('ignores invalid delay %s', (value) => {
    expect(readRetryAfter(value)).toBeUndefined();
  });

  it.each(['0', '2', '2.5'])('reads a delay in seconds: %s', (value) => {
    expect(readRetryAfter(value)).toBe(Number(value));
  });

  it('reads HTTP dates relative to now and treats a past date as immediately available', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-24T12:00:00Z'));
    expect(readRetryAfter('Sat, 24 Oct 2026 12:00:30 GMT')).toBe(30);
    expect(readRetryAfter('Sat, 24 Oct 2026 11:59:00 GMT')).toBe(0);
  });
});
