/**
 * CartSmart Extension – Auto-Scan Content Script
 *
 * Runs ONLY on the CartSmart admin/manual-price page.
 * Adds an "Auto-Scan All" button that tells the background script to
 * open each task URL in a background tab, extract the price, and
 * fill the results back into the manual-price table.
 */

(function () {
  "use strict";

  // Avoid double-injection
  if (window.__cartsmartAutoScanInjected) return;
  window.__cartsmartAutoScanInjected = true;

  console.log("[CartSmart] Auto-scan content script loaded");

  // ─── UI injection ────────────────────────────────────────────────────

  let btnContainer = null;
  let progressBar = null;
  let progressLabel = null;

  function injectUI() {
    // Find the header area with the Refresh button
    const headerDiv = document.querySelector(
      ".flex.flex-col.gap-3.md\\:flex-row.md\\:items-center.md\\:justify-between"
    );
    if (!headerDiv) {
      // Page not fully rendered yet, retry
      setTimeout(injectUI, 500);
      return;
    }

    // Find the button group
    const btnGroup = headerDiv.querySelector(".flex.gap-2");
    if (!btnGroup) {
      setTimeout(injectUI, 500);
      return;
    }

    // Check if already injected
    if (document.getElementById("cs-autoscan-btn")) return;

    // Create Auto-Scan button
    const btn = document.createElement("button");
    btn.id = "cs-autoscan-btn";
    btn.type = "button";
    btn.textContent = "Auto-Scan All";
    btn.className =
      "px-3 py-2 rounded-md bg-[#4CAF50] text-white hover:bg-[#3d8b40] disabled:opacity-60";
    btn.style.cssText = "font-size:14px;font-weight:500;";
    btn.addEventListener("click", startAutoScan);
    btnGroup.appendChild(btn);

    // Create stop button (hidden initially)
    const stopBtn = document.createElement("button");
    stopBtn.id = "cs-autoscan-stop";
    stopBtn.type = "button";
    stopBtn.textContent = "Stop";
    stopBtn.className =
      "px-3 py-2 rounded-md bg-red-600 text-white hover:bg-red-700 disabled:opacity-60";
    stopBtn.style.cssText = "font-size:14px;font-weight:500;display:none;";
    stopBtn.addEventListener("click", stopAutoScan);
    btnGroup.appendChild(stopBtn);

    // Create progress bar below header
    const progressContainer = document.createElement("div");
    progressContainer.id = "cs-autoscan-progress";
    progressContainer.style.cssText =
      "display:none;margin-top:12px;padding:12px 16px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;";
    progressContainer.innerHTML = `
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px;">
        <span id="cs-progress-label" style="font-size:13px;font-weight:500;color:#166534;">
          Preparing…
        </span>
        <span id="cs-progress-count" style="font-size:12px;color:#6b7280;"></span>
      </div>
      <div style="width:100%;height:6px;background:#d1fae5;border-radius:4px;overflow:hidden;">
        <div id="cs-progress-bar" style="width:0%;height:100%;background:#4CAF50;border-radius:4px;transition:width 0.3s ease;"></div>
      </div>
      <div id="cs-progress-url" style="margin-top:4px;font-size:11px;color:#6b7280;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;"></div>
    `;
    headerDiv.parentElement.insertBefore(
      progressContainer,
      headerDiv.nextSibling
    );

    btnContainer = btnGroup;
    progressBar = document.getElementById("cs-progress-bar");
    progressLabel = document.getElementById("cs-progress-label");
  }

  // ─── Read tasks from the DOM ──────────────────────────────────────────

  function readTasksFromDOM() {
    const tasks = [];
    const rows = document.querySelectorAll("tbody tr");

    for (const row of rows) {
      // Skip "no pending tasks" row
      if (row.querySelector("td[colspan]")) continue;

      // Find the URL (the <a> with "Open URL" text or the truncated URL div)
      const urlLink = row.querySelector('a[target="_blank"]');
      const urlText = row.querySelector(".text-xs.text-gray-500.truncate");

      const url = urlLink?.href || urlText?.textContent?.trim();
      if (!url) continue;

      // Extract task ID from "Task #123" text
      const taskIdSpan = [...row.querySelectorAll(".text-xs.text-gray-600")].find(
        (el) => el.textContent?.trim().startsWith("Task #")
      );
      const taskId = taskIdSpan
        ? parseInt(taskIdSpan.textContent.replace("Task #", ""), 10)
        : null;

      if (!taskId) continue;

      // Get current price
      const priceText = row.querySelector(".text-xs.text-gray-500");
      const currentMatch = priceText?.textContent?.match(
        /Current:\s*\$?([\d,.]+)/
      );
      const currentPrice = currentMatch ? parseFloat(currentMatch[1]) : null;

      tasks.push({ taskId, url, currentPrice });
    }

    return tasks;
  }

  // ─── Auto-scan control ───────────────────────────────────────────────

  function startAutoScan() {
    const tasks = readTasksFromDOM();
    if (tasks.length === 0) {
      alert("No tasks found to scan.");
      return;
    }

    const btn = document.getElementById("cs-autoscan-btn");
    const stopBtn = document.getElementById("cs-autoscan-stop");
    const progressDiv = document.getElementById("cs-autoscan-progress");

    btn.disabled = true;
    btn.textContent = "Scanning…";
    stopBtn.style.display = "";
    progressDiv.style.display = "";

    document.getElementById("cs-progress-label").textContent = "Starting auto-scan…";
    document.getElementById("cs-progress-count").textContent = `0 / ${tasks.length}`;
    document.getElementById("cs-progress-bar").style.width = "0%";
    document.getElementById("cs-progress-url").textContent = "";

    console.log(`[CartSmart] Starting auto-scan with ${tasks.length} task(s)`);

    chrome.runtime.sendMessage(
      { type: "START_AUTOSCAN", tasks },
      (response) => {
        if (chrome.runtime.lastError) {
          console.error("[CartSmart] Auto-scan start failed:", chrome.runtime.lastError);
          resetUI("Error starting scan");
          return;
        }
        if (!response?.ok) {
          resetUI(response?.reason === "already_running" ? "Scan already running" : "Failed to start");
        }
      }
    );
  }

  function stopAutoScan() {
    chrome.runtime.sendMessage({ type: "STOP_AUTOSCAN" });
    resetUI("Scan stopped");
  }

  function resetUI(message) {
    const btn = document.getElementById("cs-autoscan-btn");
    const stopBtn = document.getElementById("cs-autoscan-stop");

    if (btn) {
      btn.disabled = false;
      btn.textContent = "Auto-Scan All";
    }
    if (stopBtn) stopBtn.style.display = "none";

    if (message) {
      const label = document.getElementById("cs-progress-label");
      if (label) label.textContent = message;
    }
  }

  // ─── Handle messages from background ──────────────────────────────────

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.type === "AUTOSCAN_PROGRESS") {
      const { current, total, currentUrl } = message.progress;
      const pct = Math.round((current / total) * 100);

      const bar = document.getElementById("cs-progress-bar");
      const label = document.getElementById("cs-progress-label");
      const count = document.getElementById("cs-progress-count");
      const urlEl = document.getElementById("cs-progress-url");

      if (bar) bar.style.width = pct + "%";
      if (label) label.textContent = `Scanning…`;
      if (count) count.textContent = `${current} / ${total}`;
      if (urlEl) urlEl.textContent = currentUrl;
      return;
    }

    if (message.type === "AUTOSCAN_COMPLETE") {
      const results = message.results || [];
      console.log("[CartSmart] Auto-scan complete, filling results:", results);

      applyResults(results);
      resetUI(`Done! Scanned ${results.length} URL(s)`);

      const bar = document.getElementById("cs-progress-bar");
      if (bar) bar.style.width = "100%";
      return;
    }
  });

  // ─── Apply extracted prices into the DOM inputs ───────────────────────

  function applyResults(results) {
    const resultMap = {};
    for (const r of results) {
      if (r.taskId) resultMap[r.taskId] = r;
    }

    const rows = document.querySelectorAll("tbody tr");
    let filledCount = 0;

    for (const row of rows) {
      if (row.querySelector("td[colspan]")) continue;

      // Find task ID
      const taskIdSpan = [...row.querySelectorAll(".text-xs.text-gray-600")].find(
        (el) => el.textContent?.trim().startsWith("Task #")
      );
      const taskId = taskIdSpan
        ? parseInt(taskIdSpan.textContent.replace("Task #", ""), 10)
        : null;
      if (!taskId || !resultMap[taskId]) continue;

      const result = resultMap[taskId];
      if (result.price == null) {
        // Mark row with a subtle indicator that no price was found
        highlightRow(row, "no-price");
        continue;
      }

      // Find the price input and fill it
      const input = row.querySelector('input[inputmode="decimal"]');
      if (input) {
        // Use React-compatible value setter
        const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
          window.HTMLInputElement.prototype,
          "value"
        ).set;
        nativeInputValueSetter.call(input, result.price.toFixed(2));
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));

        highlightRow(row, "filled");
        filledCount++;
      }

      // If the page reports in stock, click the "In Stock" button
      if (result.inStock === true) {
        const stockBtn = [...row.querySelectorAll("button")].find(
          (b) => b.textContent.trim() === "In Stock"
        );
        if (stockBtn) stockBtn.click();
      } else if (result.inStock === false) {
        const oosBtn = [...row.querySelectorAll("button")].find(
          (b) => b.textContent.trim() === "OOS"
        );
        if (oosBtn) oosBtn.click();
      }
    }

    console.log(`[CartSmart] Filled ${filledCount} price input(s)`);

    // Show summary
    const summaryDiv = document.getElementById("cs-autoscan-progress");
    if (summaryDiv) {
      const label = document.getElementById("cs-progress-label");
      const found = results.filter((r) => r.price != null).length;
      const failed = results.filter((r) => r.price == null).length;
      if (label) {
        label.textContent = `Done! ${found} price(s) found, ${failed} failed. Review and confirm each row.`;
      }
    }
  }

  function highlightRow(row, type) {
    if (type === "filled") {
      row.style.cssText =
        "background-color:#f0fdf4 !important;transition:background-color 0.3s;";
      // Flash effect
      setTimeout(() => {
        row.style.cssText = "transition:background-color 1s;";
      }, 2000);
    } else if (type === "no-price") {
      row.style.cssText =
        "background-color:#fef2f2 !important;transition:background-color 0.3s;";
      setTimeout(() => {
        row.style.cssText = "transition:background-color 1s;";
      }, 2000);
    }
  }

  // ─── Init ────────────────────────────────────────────────────────────

  // Wait for React to render, then inject UI
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => setTimeout(injectUI, 500));
  } else {
    setTimeout(injectUI, 500);
  }

  // Also observe for SPA navigation (React re-renders)
  const observer = new MutationObserver(() => {
    if (!document.getElementById("cs-autoscan-btn")) {
      injectUI();
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });
})();
