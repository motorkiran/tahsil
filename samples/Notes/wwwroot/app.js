// The sample's settings come from its API, so the Kimlik address lives in one place.
const config = await (await fetch("/config.json")).json();

const userManager = new oidc.UserManager({
  authority: config.authority,
  client_id: config.clientId,
  redirect_uri: `${location.origin}/`,
  post_logout_redirect_uri: `${location.origin}/`,
  response_type: "code",
  scope: "openid profile email offline_access notes",
});

const $ = (id) => document.getElementById(id);
const show = (message) => { $("status").textContent = message; };

// Kimlik redirects back here with ?code=…&state=… after sign-in.
if (new URLSearchParams(location.search).has("code")) {
  await userManager.signinRedirectCallback();
  history.replaceState(null, "", "/");
}

async function api(path, options = {}) {
  const user = await userManager.getUser();
  return fetch(path, { ...options, headers: { ...options.headers, Authorization: `Bearer ${user.access_token}` } });
}

async function render() {
  const user = await userManager.getUser();
  const signedIn = user !== null && !user.expired;
  $("sign-in").hidden = signedIn;
  $("sign-out").hidden = !signedIn;
  $("signed-in").hidden = !signedIn;
  $("signed-out").hidden = signedIn;
  $("greeting").textContent = signedIn ? `Signed in as ${user.profile.name ?? user.profile.email}` : "";
  if (!signedIn) {
    return;
  }

  // The plan comes from the access token, which Kimlik issues with the plan Tahsil gave it.
  const me = await (await api("/api/me")).json();
  $("plan").textContent = me.plan ?? "none";
  $("upgrade").hidden = me.plan === "pro";
  const notes = await (await api("/api/notes")).json();
  $("notes").replaceChildren(...notes.map((note) => Object.assign(document.createElement("li"), { textContent: note.text })));
}

// A token carries the plan it was issued with; the refresh token gets one with the plan Tahsil set since.
async function refreshUntilPro(attempts = 10) {
  for (let attempt = 0; attempt < attempts; attempt++) {
    await userManager.signinSilent();
    const me = await (await api("/api/me")).json();
    if (me.plan === "pro") {
      return true;
    }
    await new Promise((resolve) => setTimeout(resolve, 2000));
  }
  return false;
}

$("sign-in").addEventListener("click", () => userManager.signinRedirect());
$("sign-out").addEventListener("click", () => userManager.signoutRedirect());

$("upgrade").addEventListener("click", async () => {
  const response = await api("/api/billing/checkout", { method: "POST" });
  if (!response.ok) {
    show("Tahsil could not start the checkout; perhaps you already subscribe. Try Manage billing.");
    return;
  }
  location.href = (await response.json()).url;
});

$("billing").addEventListener("click", async () => {
  location.href = (await (await api("/api/billing/portal", { method: "POST" })).json()).url;
});

$("new-note").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await api("/api/notes", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text: form.get("text") }) });
  event.target.reset();
  await render();
});

$("export").addEventListener("click", async () => {
  const response = await api("/api/notes/export");
  show(response.ok ? `Exported:\n${await response.text()}` : "Export is part of Pro.");
});

// Back from Tahsil's checkout: Tahsil gives Kimlik the plan once the payment is recorded, in a moment.
if (new URLSearchParams(location.search).has("upgraded")) {
  history.replaceState(null, "", "/");
  show("Thank you! Your plan is updating…");
  show((await refreshUntilPro()) ? "You are on Pro now: export works." : "Your payment went through; your plan updates shortly.");
}

await render();
