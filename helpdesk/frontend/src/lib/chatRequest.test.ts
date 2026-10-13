import { describe, expect, it } from 'vitest';
import { MAX_HISTORY_MESSAGES, MAX_MESSAGE_LENGTH, type ChatHistoryMessage } from './api';
import { SINGLE_MESSAGE_LIMIT_BYTES, encodedSize, needsSplitting, toParts, type ChatSendRequest } from './chatRequest';

const request = (text: string, history: ChatHistoryMessage[] = []): ChatSendRequest => ({
  messageText: text,
  conversationId: 'c1',
  conversationHistory: history,
});

const longHistory = (content: string): ChatHistoryMessage[] =>
  Array.from({ length: MAX_HISTORY_MESSAGES }, (_, i) => ({ role: i % 2 ? 'assistant' : 'user', content }));

describe('chatRequest', () => {
  it('sends a short request whole', () => {
    expect(needsSplitting(request('how do I fix excel', [{ role: 'user', content: 'hi' }]))).toBe(false);
  });

  it('splits a request that would be too big for one hub message', () => {
    const big = request('a'.repeat(MAX_MESSAGE_LENGTH), longHistory('a'.repeat(MAX_MESSAGE_LENGTH)));
    expect(encodedSize(big)).toBeGreaterThan(SINGLE_MESSAGE_LIMIT_BYTES);
    expect(needsSplitting(big)).toBe(true);
  });

  it('counts bytes, not characters', () => {
    const accented = request('é'.repeat(MAX_MESSAGE_LENGTH), longHistory('é'.repeat(MAX_MESSAGE_LENGTH)));
    expect(encodedSize(accented)).toBeGreaterThan(MAX_MESSAGE_LENGTH * (MAX_HISTORY_MESSAGES + 1) * 2);
    expect(needsSplitting(accented)).toBe(true);
  });

  it('produces the header first, then one part per history message, oldest first', () => {
    const history: ChatHistoryMessage[] = [
      { role: 'user', content: 'first' },
      { role: 'assistant', content: 'second' },
    ];
    const parts = toParts(request('latest', history));

    expect(parts).toEqual([
      { conversationId: 'c1', messageText: 'latest' },
      { history: history[0] },
      { history: history[1] },
    ]);
  });

  it('keeps every part of the largest valid request under the single-message limit', () => {
    const parts = toParts(request('é'.repeat(MAX_MESSAGE_LENGTH), longHistory('é'.repeat(MAX_MESSAGE_LENGTH))));

    expect(parts).toHaveLength(MAX_HISTORY_MESSAGES + 1);
    // 4000 two-byte characters plus the envelope is about 8 KB, far below the 32 KB server limit.
    expect(Math.max(...parts.map(encodedSize))).toBeLessThan(SINGLE_MESSAGE_LIMIT_BYTES);
  });
});
