/**
 * CartSmart Extension – Options Page Script
 *
 * Handles sign-in/sign-out with the user's CartSmart account.
 * Supports email/password login (/api/auth/login) and Google Sign-In
 * (/api/auth/social-login) via Google Identity Services.
 * The JWT is stored in chrome.storage.local — no manual token management.
 *
 * Depends on: lib/config.js (loaded first) for GOOGLE_CLIENT_ID, DEFAULT_API_BASE.
 */

const STORAGE_KEYS = {
  API_BASE: "cartsmart_api_base",
  API_TOKEN: "cartsmart_api_token",
  USER: "cartsmart_user",
};

document.addEventListener("DOMContentLoaded", async () => {
  // ── Load current state ────────────────────────────────────────────────
  const token = await storageGetLocal(STORAGE_KEYS.API_TOKEN);
  const user = await storageGetLocal(STORAGE_KEYS.USER);
  const apiBaseOverride = await storageGetLocal(STORAGE_KEYS.API_BASE);

  if (apiBaseOverride) {
    document.getElementById("api-base").value = apiBaseOverride;
  }

  if (token && user) {
    showLoggedInView(user);
  } else {
    showLoginView();
  }

  // ── Google Sign-In ────────────────────────────────────────────────────
  initGoogleSignIn();

  // ── Login ─────────────────────────────────────────────────────────────
  document.getElementById("login-btn").addEventListener("click", handleLogin);

  // Allow Enter key to submit
  document.getElementById("password").addEventListener("keydown", (e) => {
    if (e.key === "Enter") handleLogin();
  });

  // ── Logout ────────────────────────────────────────────────────────────
  document.getElementById("logout-btn").addEventListener("click", async () => {
    await chrome.storage.local.remove([
      STORAGE_KEYS.API_TOKEN,
      STORAGE_KEYS.USER,
    ]);
    // Notify background to clear stores
    chrome.runtime.sendMessage({ type: "REFRESH_STORES" });
    showLoginView();
    showToast("Signed out successfully.", "success");
  });

  // ── Developer settings toggle ─────────────────────────────────────────
  document.getElementById("dev-toggle").addEventListener("click", () => {
    document.getElementById("dev-section").classList.toggle("hidden");
  });

  document.getElementById("save-dev-btn").addEventListener("click", () => {
    const apiBase = document.getElementById("api-base").value.trim();
    if (apiBase && !apiBase.startsWith("http")) {
      showToast("URL must start with http:// or https://", "error");
      return;
    }
    chrome.storage.local.set({ [STORAGE_KEYS.API_BASE]: apiBase || "" }, () => {
      showToast(apiBase ? "API override saved." : "API override cleared (using production).", "success");
      chrome.runtime.sendMessage({ type: "REFRESH_STORES" });
    });
  });
});

// ─── Handlers ─────────────────────────────────────────────────────────────

async function handleLogin() {
  const email = document.getElementById("email").value.trim();
  const password = document.getElementById("password").value;

  if (!email || !password) {
    showToast("Please enter your email and password.", "error");
    return;
  }

  const btn = document.getElementById("login-btn");
  btn.textContent = "Signing in…";
  btn.disabled = true;

  try {
    // Determine API base
    const apiBaseOverride = await storageGetLocal(STORAGE_KEYS.API_BASE);
    const apiBase = (apiBaseOverride || "https://cartsmart.com").replace(/\/+$/, "");

    const res = await fetch(`${apiBase}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ emailAddress: email, password }),
    });

    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      showToast(body.message || `Login failed (${res.status})`, "error");
      btn.textContent = "Sign In";
      btn.disabled = false;
      return;
    }

    const data = await res.json();

    if (!data.success || !data.token) {
      showToast(data.message || "Login failed – no token received.", "error");
      btn.textContent = "Sign In";
      btn.disabled = false;
      return;
    }

    const user = data.user
      ? {
          id: data.user.id,
          email: data.user.email,
          displayName: data.user.displayName || data.user.userName,
          isAdmin: !!data.user.admin,
        }
      : { email };

    await chrome.storage.local.set({
      [STORAGE_KEYS.API_TOKEN]: data.token,
      [STORAGE_KEYS.USER]: user,
    });

    showLoggedInView(user);
    showToast("Signed in successfully!", "success");

    // Trigger store refresh
    chrome.runtime.sendMessage({ type: "REFRESH_STORES" });
  } catch (err) {
    console.error("[CartSmart] Login error:", err);
    showToast("Network error – could not connect to CartSmart.", "error");
  } finally {
    btn.textContent = "Sign In";
    btn.disabled = false;
  }
}

// ─── UI helpers ───────────────────────────────────────────────────────────

/**
 * Initialise the Google Sign-In button.
 * Uses chrome.identity.launchWebAuthFlow() because MV3 extension pages
 * block external scripts (GIS library cannot load due to CSP).
 */
function initGoogleSignIn() {
  const separator = document.querySelector(".separator");
  const container = document.getElementById("google-signin-container");
  const btn = document.getElementById("google-login-btn");

  if (!GOOGLE_CLIENT_ID) {
    // No client ID configured — hide the Google section entirely
    if (separator) separator.style.display = "none";
    if (container) container.style.display = "none";
    return;
  }

  btn.addEventListener("click", handleGoogleLogin);
}

async function handleGoogleLogin() {
  const btn = document.getElementById("google-login-btn");
  btn.disabled = true;
  btn.textContent = "Signing in…";

  try {
    // Build Google OAuth2 URL for implicit flow to get an id_token
    const redirectUri = chrome.identity.getRedirectURL();
    const nonce = crypto.randomUUID();
    const authUrl = new URL("https://accounts.google.com/o/oauth2/v2/auth");
    authUrl.searchParams.set("client_id", GOOGLE_CLIENT_ID);
    authUrl.searchParams.set("response_type", "id_token");
    authUrl.searchParams.set("redirect_uri", redirectUri);
    authUrl.searchParams.set("scope", "openid email profile");
    authUrl.searchParams.set("nonce", nonce);
    authUrl.searchParams.set("prompt", "select_account");

    // Launch the auth flow — Chrome shows a popup for the user to sign in
    const responseUrl = await new Promise((resolve, reject) => {
      chrome.identity.launchWebAuthFlow(
        { url: authUrl.toString(), interactive: true },
        (callbackUrl) => {
          if (chrome.runtime.lastError) {
            reject(new Error(chrome.runtime.lastError.message));
          } else {
            resolve(callbackUrl);
          }
        }
      );
    });

    // Extract the id_token from the redirect URL hash fragment
    const hashParams = new URLSearchParams(responseUrl.split("#")[1] || "");
    const idToken = hashParams.get("id_token");

    if (!idToken) {
      showToast("Google sign-in failed — no token received.", "error");
      return;
    }

    // Send the id_token to CartSmart API (same as the website does)
    const apiBaseOverride = await storageGetLocal(STORAGE_KEYS.API_BASE);
    const apiBase = (apiBaseOverride || DEFAULT_API_BASE).replace(/\/+$/, "");

    const res = await fetch(`${apiBase}/api/auth/social-login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        provider: "Google",
        token: idToken,
      }),
    });

    const data = await res.json().catch(() => ({}));

    if (!res.ok || !data.success || !data.token) {
      showToast(data.message || `Google login failed (${res.status})`, "error");
      return;
    }

    const user = data.user
      ? {
          id: data.user.id,
          email: data.user.email,
          displayName: data.user.displayName || data.user.userName,
          isAdmin: !!data.user.admin,
        }
      : {};

    await chrome.storage.local.set({
      [STORAGE_KEYS.API_TOKEN]: data.token,
      [STORAGE_KEYS.USER]: user,
    });

    showLoggedInView(user);
    showToast("Signed in with Google!", "success");

    // Trigger store refresh
    chrome.runtime.sendMessage({ type: "REFRESH_STORES" });
  } catch (err) {
    // "The user did not approve access" = user closed the popup
    if (err.message?.includes("canceled") || err.message?.includes("not approve")) {
      // User cancelled — no toast needed
    } else {
      console.error("[CartSmart] Google login error:", err);
      showToast("Google sign-in failed. Please try again.", "error");
    }
  } finally {
    btn.disabled = false;
    btn.innerHTML = `<svg width="18" height="18" viewBox="0 0 48 48" style="flex-shrink:0"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"/><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"/><path fill="#FBBC05" d="M10.53 28.59a14.5 14.5 0 0 1 0-9.18l-7.98-6.19a24.07 24.07 0 0 0 0 21.56l7.98-6.19z"/><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"/></svg> Sign in with Google`;
  }
}

function showLoggedInView(user) {
  document.getElementById("login-view").classList.add("hidden");
  document.getElementById("logged-in-view").classList.remove("hidden");

  const name = user?.displayName || user?.email || "User";
  document.getElementById("user-name").textContent = name;
  document.getElementById("user-email").textContent = user?.email || "";
  document.getElementById("user-avatar").textContent = (name[0] || "?").toUpperCase();
}

function showLoginView() {
  document.getElementById("login-view").classList.remove("hidden");
  document.getElementById("logged-in-view").classList.add("hidden");
  document.getElementById("email").value = "";
  document.getElementById("password").value = "";
}

function showToast(message, type) {
  const toast = document.getElementById("toast");
  toast.textContent = message;
  toast.className = `toast ${type}`;
  setTimeout(() => {
    toast.className = "toast";
  }, 4000);
}

function storageGetLocal(key) {
  return new Promise((resolve) => {
    chrome.storage.local.get(key, (result) => resolve(result[key]));
  });
}
