(() => {
  const authorizePath = "/diagnostics/hmrc/authorize";
  const signInPath = "/diagnostics/hmrc/sign-in";
  const nativeFetch = window.fetch.bind(window);

  // HMRC operations report a missing/expired grant as a same-origin 401. Turn that
  // response into the top-level OAuth journey Swagger cannot perform with fetch().
  window.fetch = async (...arguments_) => {
    const response = await nativeFetch(...arguments_);
    const requestedSignIn = response.headers.get("X-TaxHub-Sign-In");
    if (response.status === 401 && requestedSignIn === signInPath) {
      const input = arguments_[0];
      const requestUrl = new URL(typeof input === "string" || input instanceof URL ? input : input.url,
        window.location.origin);
      const returnTo = requestUrl.pathname === "/diagnostics/hmrc/fraud-prevention/validate"
        ? "?returnTo=validate"
        : "";
      window.location.assign(`${signInPath}${returnTo}`);
      return response;
    }
    const requestedAuthorization = response.headers.get("X-TaxHub-Hmrc-Authorize");
    if (response.status === 401 && requestedAuthorization === authorizePath) {
      const input = arguments_[0];
      const requestUrl = new URL(typeof input === "string" || input instanceof URL ? input : input.url,
        window.location.origin);
      if (requestUrl.origin === window.location.origin) {
        window.location.assign(authorizePath);
      }
    }
    return response;
  };

  // Keep the explicit authorization diagnostic usable too. OAuth authorization is browser
  // navigation, not an XHR, so its Swagger control must also navigate the top-level page.
  document.addEventListener("click", event => {
    const button = event.target.closest("button");
    const operation = button?.closest(".opblock");
    const operationPath = operation?.querySelector(".opblock-summary-path")?.dataset.path;
    if (operationPath !== authorizePath && operationPath !== signInPath
        || (!button.classList.contains("try-out__btn") && !button.classList.contains("execute"))) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    window.location.assign(operationPath);
  }, true);

  const captureBrowserFacts = async () => {
    const deviceKey = "trade-control-tax-hub-device-id";
    let deviceId = localStorage.getItem(deviceKey);
    if (!deviceId) {
      deviceId = crypto.randomUUID();
      localStorage.setItem(deviceKey, deviceId);
    }

    const offsetMinutes = -new Date().getTimezoneOffset();
    const sign = offsetMinutes >= 0 ? "+" : "-";
    const pad = value => String(Math.abs(value)).padStart(2, "0");
    const facts = {
      javascriptUserAgent: navigator.userAgent,
      deviceId,
      screens: [{
        width: screen.width,
        height: screen.height,
        scalingFactor: window.devicePixelRatio,
        colourDepth: screen.colorDepth
      }],
      timezone: `UTC${sign}${pad(Math.trunc(offsetMinutes / 60))}:${pad(offsetMinutes % 60)}`,
      windowSize: { width: window.innerWidth, height: window.innerHeight }
    };

    const response = await fetch("/diagnostics/hmrc/fraud-prevention/browser-session", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(facts)
    });
    if (!response.ok) {
      console.error(`Tax Hub browser-fact capture failed (${response.status}).`);
    }
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      void captureBrowserFacts();
    }, { once: true });
  } else {
    void captureBrowserFacts();
  }
})();
