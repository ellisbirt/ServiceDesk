import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser';

const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined;
const authority = import.meta.env.VITE_ENTRA_AUTHORITY as string | undefined;
const apiScope = import.meta.env.VITE_API_SCOPE as string | undefined;

export const isEntraConfigured = Boolean(clientId && authority && apiScope);

/**
 * Local dev auth: no Entra settings in a dev server. A production build without them fails (see vite.config.ts), and
 * this check keeps the dev identity out of production bundles even if that guard is bypassed.
 */
export const isDevAuth = !isEntraConfigured && import.meta.env.DEV;

/** Thrown when an access token cannot be had without the user signing in again. */
export class SignInRequiredError extends Error {
  constructor() {
    super('Your session has expired. Sign in again to continue.');
    this.name = 'SignInRequiredError';
  }
}

// Set while an automatic sign-in redirect is in flight. If the user comes back still signed out, the app shows a
// sign-in prompt instead of redirecting again, so a broken sign-in cannot bounce the page in a loop.
const REDIRECT_GUARD_KEY = 'agentdesk.signInRedirect';

let msal: PublicClientApplication | undefined;
let signInRequired = false;
const signInListeners = new Set<(required: boolean) => void>();

function setSignInRequired(required: boolean) {
  if (signInRequired === required) return;
  signInRequired = required;
  signInListeners.forEach((listener) => listener(required));
}

/** Whether the user must sign in again before the API can be called. */
export function isSignInRequired(): boolean {
  return signInRequired;
}

export function onSignInRequiredChange(listener: (required: boolean) => void): () => void {
  signInListeners.add(listener);
  return () => signInListeners.delete(listener);
}

async function getMsal(): Promise<PublicClientApplication> {
  if (!msal) {
    msal = new PublicClientApplication({
      auth: { clientId: clientId!, authority: authority!, knownAuthorities: [new URL(authority!).host] },
      cache: { cacheLocation: 'sessionStorage' },
    });
    await msal.initialize();
    await msal.handleRedirectPromise();
  }
  return msal;
}

/**
 * Call once at start-up. Completes any pending redirect and, the first time in a browser session, sends a signed-out
 * user to sign in. After that one automatic attempt the app loads anyway and asks the user to sign in.
 */
export async function ensureSignedIn(): Promise<void> {
  if (!isEntraConfigured) return;
  const app = await getMsal();
  if (app.getAllAccounts().length > 0) {
    sessionStorage.removeItem(REDIRECT_GUARD_KEY);
    return;
  }
  if (sessionStorage.getItem(REDIRECT_GUARD_KEY)) {
    setSignInRequired(true);
    return;
  }
  sessionStorage.setItem(REDIRECT_GUARD_KEY, '1');
  await app.loginRedirect({ scopes: [apiScope!] });
}

/** Starts an interactive sign-in. Only call this from a user action, such as the "Sign in" button. */
export async function signIn(): Promise<void> {
  if (!isEntraConfigured) return;
  const app = await getMsal();
  sessionStorage.setItem(REDIRECT_GUARD_KEY, '1');
  const account = app.getAllAccounts()[0];
  if (account) await app.acquireTokenRedirect({ scopes: [apiScope!], account });
  else await app.loginRedirect({ scopes: [apiScope!] });
}

/**
 * A fresh access token for the API, or undefined with dev auth. Never redirects: when the user has to sign in
 * again it throws {@link SignInRequiredError} and the app asks them to. Other failures (network, timeouts) are
 * rethrown as they are, because signing in again would not fix them.
 */
export async function getAccessToken(): Promise<string | undefined> {
  if (!isEntraConfigured) return undefined;

  const app = await getMsal();
  const account = app.getAllAccounts()[0];
  if (!account) {
    setSignInRequired(true);
    throw new SignInRequiredError();
  }
  try {
    const result = await app.acquireTokenSilent({ scopes: [apiScope!], account });
    setSignInRequired(false);
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      setSignInRequired(true);
      throw new SignInRequiredError();
    }
    throw err;
  }
}

export async function getAuthHeaders(): Promise<Record<string, string>> {
  const token = await getAccessToken();
  if (token !== undefined) return { Authorization: 'Bearer ' + token };
  // Honoured only by the API's Development-only dev auth handler, and never sent from a production build.
  return isDevAuth ? { 'X-Dev-Tenant': 'dev-tenant', 'X-Dev-User': 'dev-user' } : {};
}

/** Dev-auth identity for the hub: browsers cannot set headers on a WebSocket, so it travels in the query string. */
export const devHubQuery = isDevAuth ? '?x-dev-tenant=dev-tenant&x-dev-user=dev-user' : '';
