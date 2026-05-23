/**
 * CartSmart Extension – Popup Script
 */

document.addEventListener("DOMContentLoaded", async () => {
  // Version
  const manifest = chrome.runtime.getManifest();
  document.getElementById("version").textContent = `v${manifest.version}`;

  // Options / sign-in link
  document.getElementById("options-link").addEventListener("click", (e) => {
    e.preventDefault();
    chrome.runtime.openOptionsPage();
  });

  document.getElementById("auth-link").addEventListener("click", (e) => {
    e.preventDefault();
    chrome.runtime.openOptionsPage();
  });

  // Check auth state
  chrome.storage.local.get(
    ["cartsmart_api_token", "cartsmart_user"],
    (result) => {
      const authBar = document.getElementById("auth-bar");
      const authText = document.getElementById("auth-text");
      const authLink = document.getElementById("auth-link");

      if (result.cartsmart_api_token && result.cartsmart_user) {
        const name =
          result.cartsmart_user.displayName || result.cartsmart_user.email;
        authBar.classList.add("signed-in");
        authText.textContent = `Signed in as ${name}`;
        authLink.textContent = "Settings";
      } else {
        authBar.classList.remove("signed-in");
        authText.textContent = "Not signed in – prices won't be submitted";
        authLink.textContent = "Sign in";
      }
    }
  );

  // Refresh button
  document.getElementById("refresh-btn").addEventListener("click", async () => {
    const btn = document.getElementById("refresh-btn");
    btn.textContent = "Refreshing…";
    btn.disabled = true;

    chrome.runtime.sendMessage({ type: "REFRESH_STORES" }, (response) => {
      btn.textContent = "Refresh Stores";
      btn.disabled = false;
      if (response?.storeCount !== undefined) {
        document.getElementById("store-count").textContent = response.storeCount;
      }
    });
  });

  // Load status from background
  chrome.runtime.sendMessage({ type: "GET_STATUS" }, (response) => {
    if (!response) return;

    document.getElementById("store-count").textContent = response.storeCount || 0;
    document.getElementById("report-count").textContent =
      response.recentReports?.length || 0;

    renderReports(response.recentReports || []);
  });

  // Check current tab
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab?.url) {
    chrome.runtime.sendMessage(
      { type: "GET_STORE_MATCH", url: tab.url },
      (response) => {
        const dot = document.getElementById("status-dot");
        const label = document.getElementById("status-label");
        const detail = document.getElementById("status-detail");

        if (response?.store) {
          dot.className = "status-indicator active";
          label.textContent = `Tracking: ${response.store.name}`;
          detail.textContent = tab.url;
          // Show the extract button for tracked stores
          const extractBtn = document.getElementById("extract-btn");
          extractBtn.style.display = "block";
          extractBtn.addEventListener("click", () => {
            extractBtn.textContent = "Extracting…";
            extractBtn.disabled = true;
            chrome.runtime.sendMessage({ type: "EXTRACT_CURRENT" }, (res) => {
              extractBtn.textContent = res?.ok ? "Sent!" : "Extract Price Now";
              extractBtn.disabled = false;
              setTimeout(() => { extractBtn.textContent = "Extract Price Now"; }, 2000);
            });
          });

          // Admin-only Add Product button. Refresh the cached user from the
          // server first so a freshly-promoted admin (or anyone whose stored
          // user predates the isAdmin field being persisted on login) actually
          // sees the button without needing to re-login.
          chrome.runtime.sendMessage({ type: "REFRESH_USER" }, () => {
            chrome.storage.local.get(["cartsmart_user"], (storeResult) => {
              if (!storeResult?.cartsmart_user?.isAdmin) return;

              const addBtn = document.getElementById("add-product-btn");
              const resultBox = document.getElementById("add-product-result");
              addBtn.style.display = "block";
              addBtn.addEventListener("click", () => {
                addBtn.textContent = "Submitting…";
                addBtn.disabled = true;
                resultBox.style.display = "none";
                resultBox.textContent = "";

                chrome.runtime.sendMessage({ type: "SUBMIT_PRODUCT_CANDIDATE" }, (res) => {
                  addBtn.disabled = false;
                  addBtn.textContent = "Add Product";
                  resultBox.style.display = "block";
                  resultBox.textContent = formatCandidateResult(res);
                });
              });
            });
          });
        } else {
          dot.className = "status-indicator inactive";
          label.textContent = "Not a tracked store";
          detail.textContent = tab.url;
        }
      }
    );
  } else {
    const dot = document.getElementById("status-dot");
    const label = document.getElementById("status-label");
    dot.className = "status-indicator inactive";
    label.textContent = "No active page";
  }
});

function renderReports(reports) {
  const list = document.getElementById("report-list");
  if (!reports.length) return;

  list.innerHTML = "";
  for (const r of reports.slice(0, 10)) {
    const li = document.createElement("li");

    const store = document.createElement("span");
    store.className = "report-store";
    store.textContent = r.storeName || "Unknown";
    store.title = r.url || "";

    const price = document.createElement("span");
    price.className = r.submitted ? "report-price" : "report-price failed";
    price.textContent = r.price !== null ? `$${r.price.toFixed(2)}` : "No price";

    const time = document.createElement("span");
    time.className = "report-time";
    time.textContent = formatTimeAgo(r.timestamp);

    li.appendChild(store);
    li.appendChild(price);
    li.appendChild(time);
    list.appendChild(li);
  }
}

function formatCandidateResult(res) {
  if (!res) return "No response from extension.";
  if (!res.ok) {
    if (res.reason === "no_store_match") return "This page isn't on an approved store.";
    if (res.reason === "not_logged_in" || res.reason === "token_expired") return "Please sign in again.";
    if (res.reason === "forbidden") return "Admin access required.";
    if (res.reason === "extract_failed") return "Could not read product details from this page.";
    if (res.reason === "not_product_page") return "This doesn't look like a product page — nothing to add.";
    return res.message || `Submission failed (${res.reason || "unknown"}).`;
  }
  const d = res.data || {};
  switch (d.status) {
    case "created":
      return `Submitted for review (candidate #${d.candidateId}).`;
    case "suggested_merge":
      return `Looks similar to product #${d.suggestedMergeProductId} — admin will confirm.`;
    case "duplicate_candidate":
      return `Already queued for review (${d.submissionCount} submission${d.submissionCount === 1 ? "" : "s"}).`;
    case "duplicate_live_product":
      return `Already tracked as product #${d.productId}.`;
    default:
      return d.message || "Submitted.";
  }
}

function formatTimeAgo(ts) {
  if (!ts) return "";
  const diff = Date.now() - ts;
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  return `${Math.floor(hrs / 24)}d ago`;
}
