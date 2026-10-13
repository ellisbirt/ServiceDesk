import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ServiceStatusIndicator } from './ServiceStatusIndicator';

describe('ServiceStatusIndicator', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('probes readiness immediately and keeps the status updated', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 503 }));
    vi.stubGlobal('fetch', fetchMock);

    render(<ServiceStatusIndicator />);

    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.getByRole('status')).toHaveTextContent('Service ready');
    expect(fetchMock).toHaveBeenCalledWith('/readyz', {
      method: 'GET',
      cache: 'no-store',
      signal: expect.any(AbortSignal),
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(screen.getByRole('status')).toHaveTextContent('Service unavailable');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
