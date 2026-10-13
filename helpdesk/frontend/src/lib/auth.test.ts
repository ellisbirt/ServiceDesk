import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const msal = vi.hoisted(() => {
  class InteractionRequiredAuthError extends Error {}
  return {
    InteractionRequiredAuthError,
    accounts: [] as { username: string }[],
    initialize: vi.fn(async () => {}),
    handleRedirectPromise: vi.fn(async () => null),
    loginRedirect: vi.fn(async () => {}),
    acquireTokenRedirect: vi.fn(async () => {}),
    acquireTokenSilent: vi.fn<() => Promise<{ accessToken: string }>>(),
  };
});

vi.mock('@azure/msal-browser', () => ({
  InteractionRequiredAuthError: msal.InteractionRequiredAuthError,
  PublicClientApplication: class {
    initialize = msal.initialize;
    handleRedirectPromise = msal.handleRedirectPromise;
    loginRedirect = msal.loginRedirect;
    acquireTokenRedirect = msal.acquireTokenRedirect;
    acquireTokenSilent = msal.acquireTokenSilent;
    getAllAccounts = () => msal.accounts;
  },
}));

function configureEntra() {
  vi.stubEnv('VITE_ENTRA_CLIENT_ID', 'client');
  vi.stubEnv('VITE_ENTRA_AUTHORITY', 'https://login.example.com/tenant');
  vi.stubEnv('VITE_API_SCOPE', 'api://helpdesk/.default');
}

async function loadAuth() {
  vi.resetModules();
  return import('./auth');
}

beforeEach(() => {
  sessionStorage.clear();
  msal.accounts = [];
  vi.clearAllMocks();
});

afterEach(() => vi.unstubAllEnvs());

describe('dev auth', () => {
  it('sends the dev identity from a dev server without Entra settings', async () => {
    vi.stubEnv('DEV', true);
    const auth = await loadAuth();
    expect(await auth.getAuthHeaders()).toEqual({ 'X-Dev-Tenant': 'dev-tenant', 'X-Dev-User': 'dev-user' });
    expect(auth.devHubQuery).toContain('x-dev-tenant');
  });

  it('never sends the dev identity from a production build', async () => {
    vi.stubEnv('DEV', false);
    const auth = await loadAuth();
    expect(auth.isDevAuth).toBe(false);
    expect(await auth.getAuthHeaders()).toEqual({});
    expect(auth.devHubQuery).toBe('');
  });
});

describe('ensureSignedIn', () => {
  beforeEach(configureEntra);

  it('redirects a signed-out user once per session, then shows the sign-in prompt', async () => {
    let auth = await loadAuth();
    await auth.ensureSignedIn();
    expect(msal.loginRedirect).toHaveBeenCalledTimes(1);

    // Back from the identity provider but still signed out: no second redirect.
    auth = await loadAuth();
    await auth.ensureSignedIn();
    expect(msal.loginRedirect).toHaveBeenCalledTimes(1);
    expect(auth.isSignInRequired()).toBe(true);
  });

  it('clears the loop guard once signed in', async () => {
    sessionStorage.setItem('helpdesk.signInRedirect', '1');
    msal.accounts = [{ username: 'a' }];
    const auth = await loadAuth();
    await auth.ensureSignedIn();
    expect(sessionStorage.getItem('helpdesk.signInRedirect')).toBeNull();
    expect(auth.isSignInRequired()).toBe(false);
  });

  it('lets the user retry with the Sign in button even after the guard is set', async () => {
    sessionStorage.setItem('helpdesk.signInRedirect', '1');
    const auth = await loadAuth();
    await auth.signIn();
    expect(msal.loginRedirect).toHaveBeenCalledTimes(1);
  });
});

describe('getAccessToken', () => {
  beforeEach(configureEntra);

  it('returns a bearer header when a token is available silently', async () => {
    msal.accounts = [{ username: 'a' }];
    msal.acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
    const auth = await loadAuth();
    expect(await auth.getAuthHeaders()).toEqual({ Authorization: 'Bearer tok' });
  });

  it('throws SignInRequiredError instead of redirecting when interaction is required', async () => {
    msal.accounts = [{ username: 'a' }];
    msal.acquireTokenSilent.mockRejectedValue(new msal.InteractionRequiredAuthError('expired'));
    const auth = await loadAuth();
    const listener = vi.fn();
    auth.onSignInRequiredChange(listener);

    await expect(auth.getAccessToken()).rejects.toBeInstanceOf(auth.SignInRequiredError);
    expect(msal.acquireTokenRedirect).not.toHaveBeenCalled();
    expect(msal.loginRedirect).not.toHaveBeenCalled();
    expect(listener).toHaveBeenCalledWith(true);
  });

  it('throws SignInRequiredError when there is no account', async () => {
    const auth = await loadAuth();
    await expect(auth.getAccessToken()).rejects.toBeInstanceOf(auth.SignInRequiredError);
    expect(msal.loginRedirect).not.toHaveBeenCalled();
  });

  it('rethrows other failures without asking the user to sign in', async () => {
    msal.accounts = [{ username: 'a' }];
    msal.acquireTokenSilent.mockRejectedValue(new Error('network down'));
    const auth = await loadAuth();
    await expect(auth.getAccessToken()).rejects.toThrow('network down');
    expect(auth.isSignInRequired()).toBe(false);
  });
});
