import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { expectNoAxeViolations } from './test-axe';

// Stand-in for lib/auth whose session has expired.
const auth = vi.hoisted(() => {
  class SignInRequiredError extends Error {
    constructor() {
      super('Your session has expired. Sign in again to continue.');
    }
  }
  const listeners = new Set<(required: boolean) => void>();
  const state = { required: false };
  return {
    SignInRequiredError,
    state,
    set(required: boolean) {
      state.required = required;
      listeners.forEach((l) => l(required));
    },
    module: {
      SignInRequiredError,
      isSignInRequired: () => state.required,
      onSignInRequiredChange: (l: (required: boolean) => void) => {
        listeners.add(l);
        return () => listeners.delete(l);
      },
      signIn: vi.fn(async () => {}),
      getAuthHeaders: vi.fn(async (): Promise<Record<string, string>> => {
        throw new SignInRequiredError();
      }),
    },
  };
});
vi.mock('./lib/auth', () => auth.module);
vi.mock('./lib/chatHub', () => ({
  getChatHub: () => ({
    connect: () => Promise.reject(new auth.SignInRequiredError()),
    send: vi.fn(),
    cancel: vi.fn(),
    onEvent: () => () => {},
    onConnectionLost: () => () => {},
  }),
}));

beforeEach(() => {
  auth.state.required = false;
  auth.module.signIn.mockClear();
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 200 })));
});

describe('App when the session has expired', () => {
  it('shows a sign-in banner instead of redirecting, and signs in on request', async () => {
    render(<App />);
    expect(screen.queryByRole('alert')).toBeNull();

    act(() => auth.set(true));
    const banner = screen.getByRole('alert');
    expect(banner).toHaveTextContent('Your session has expired. Sign in again to continue.');

    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(auth.module.signIn).toHaveBeenCalledTimes(1);
    await expectNoAxeViolations(document.body);
  });

  it('tells the user to sign in when a message cannot be authenticated', async () => {
    render(<App />);
    await userEvent.type(screen.getByRole('textbox'), 'My laptop will not boot');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));

    await waitFor(() =>
      expect(screen.getAllByText('Your session has expired. Sign in again to continue.').length).toBeGreaterThan(0),
    );
  });
});
