/**
 * Seeds demo telemetry so the admin dashboard has something to show in the
 * sandbox (real Windows clients cannot reach it). Live clients are created
 * through the real /api/heartbeat endpoint; the 24h trend is backfilled via
 * Prisma to match the final live online count.
 *
 * Usage: bun scripts/seed-demo.mjs
 */
import { PrismaClient } from "@prisma/client";

const BASE = "http://localhost:3000/api/heartbeat";
const db = new PrismaClient();

const ONLINE = [
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

const STOPPED = [
  { installId: "b7c8d9e0-1001-4b01-8d2e-000000000011", version: "1.2.0", os: "Windows 11", strategy: "general.bat" },
  { installId: "b7c8d9e0-1002-4b02-8d2e-000000000012", version: "1.1.0", os: "Windows 10", strategy: "general-alt.bat" },
  { installId: "b7c8d9e0-1003-4b03-8d2e-000000000013", version: "1.2.0", os: "Windows 10", strategy: "general.bat" },
  { installId: "b7c8d9e0-1004-4b04-8d2e-000000000014", version: "1.0.9", os: "Windows 11", strategy: "general.bat" },
  { installId: "b7c8d9e0-1005-4b05-8d2e-000000000015", version: "1.2.0", os: "Windows 11", strategy: "general-alt.bat" },
  { installId: "b7c8d9e0-1006-4b06-8d2e-000000000016", version: "1.1.3", os: "Windows 10", strategy: "general.bat" },
];

async function beat(client, event, attempt = 1) {
  const res = await fetch(BASE, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ ...client, event }),
  });
  if (res.status === 429 && attempt <= 25) {
    await new Promise((r) => setTimeout(r, 3000));
    return beat(client, event, attempt + 1);
  }
  if (!res.ok) throw new Error(`heartbeat ${event} failed: ${res.status}`);
}

async function main() {
  // 1) online clients (start event)
  for (const c of ONLINE) await beat(c, "start");

  // 2) stopped clients: start (registered) then stop (marked offline)
  for (const c of STOPPED) {
    await beat(c, "start");
    await beat(c, "stop");
  }

  // 3) backfill a realistic 24h online trend ending at the live count (9)
  const nowMinute = Math.floor(Date.now() / 60_000);
  const rows = [];
  for (let i = 1439; i >= 2; i--) {
    const t = new Date((nowMinute - i) * 60_000);
    const h = t.getHours() + t.getMinutes() / 60;
    // Diurnal curve: low at ~5:00, peak at ~21:00 (evening activity peak)
    const wave = 6.5 + 5.5 * Math.sin(((h - 9) / 24) * 2 * Math.PI);
    const noise = Math.sin(i * 12.9898) * 1.3;
    const value = Math.max(1, Math.min(16, Math.round(wave + noise)));
    rows.push({ minute: nowMinute - i, onlineCount: value, recordedAt: t });
  }
  for (let i = 0; i < rows.length; i += 200) {
    const chunk = rows.slice(i, i + 200);
    await db.$transaction(chunk.map((r) => db.snapshot.upsert({ where: { minute: r.minute }, create: r, update: {} })));
  }

  const online = await db.client.count({
    where: { stoppedAt: null, lastSeenAt: { gte: new Date(Date.now() - 150_000) } },
  });
  const total = await db.client.count();
  console.log(`seeded: online=${online}, total=${total}, snapshots=${rows.length}`);
}

main()
  .catch((e) => {
    console.error(e);
    process.exit(1);
  })
  .finally(() => db.$disconnect());
