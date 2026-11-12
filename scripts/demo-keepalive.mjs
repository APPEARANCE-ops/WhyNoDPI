/**
 * Sandbox demo keep-alive: re-sends heartbeats for the seeded demo clients so
 * the admin dashboard shows a live "online" number during the demo session.
 * In production with real clients this script is NOT needed.
 * Usage: nohup bun scripts/demo-keepalive.mjs &
 */
const BASE = "http://localhost:3000/api/heartbeat";

const CLIENTS = [
  { installId: "a1e2f3b4-0001-4a01-9c1d-000000000001", version: "1.2.0", os: "Windows 11", strategy: "general.bat" },
  { installId: "a1e2f3b4-0002-4a02-9c1d-000000000002", version: "1.2.0", os: "Windows 11", strategy: "general-alt.bat" },
  { installId: "a1e2f3b4-0003-4a03-9c1d-000000000003", version: "1.1.3", os: "Windows 10", strategy: "general.bat" },
  { installId: "a1e2f3b4-0004-4a04-9c1d-000000000004", version: "1.2.0", os: "Windows 10", strategy: "general.bat" },
  { installId: "a1e2f3b4-0005-4a05-9c1d-000000000005", version: "1.1.0", os: "Windows 11", strategy: "general-advanced.bat" },
  { installId: "a1e2f3b4-0006-4a06-9c1d-000000000006", version: "1.2.0", os: "Windows 11", strategy: "general.bat" },
  { installId: "a1e2f3b4-0007-4a07-9c1d-000000000007", version: "1.0.9", os: "Windows 10", strategy: "general-alt.bat" },
  { installId: "a1e2f3b4-0008-4a08-9c1d-000000000008", version: "1.2.0", os: "Windows 11", strategy: "general.bat" },
  { installId: "a1e2f3b4-0009-4a09-9c1d-000000000009", version: "1.1.3", os: "Windows 11", strategy: "general-advanced.bat" },
];

async function tick() {
  for (const c of CLIENTS) {
    try {
      const res = await fetch(BASE, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ ...c, event: "heartbeat" }),
      });
      if (!res.ok) console.log("beat", c.installId.slice(-2), res.status);
    } catch (e) {
      console.log("err", String(e).slice(0, 80));
    }
  }
  console.log(new Date().toISOString(), "keepalive tick");
}

await tick();
setInterval(tick, 60_000);
