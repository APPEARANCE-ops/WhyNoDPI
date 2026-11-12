import { db } from "@/lib/db";

/** How long after the last heartbeat a client is still considered online. */
export function onlineWindowMs(): number {
  const n = Number.parseInt(process.env.ONLINE_WINDOW_SECONDS ?? "150", 10);
  return (Number.isFinite(n) && n > 30 && n <= 3600 ? n : 150) * 1000;
}

export function onlineCondition(now: Date) {
  return {
    lastSeenAt: { gte: new Date(now.getTime() - onlineWindowMs()) },
    stoppedAt: null as Date | null,
  };
}

/** Persist a per-minute snapshot of the online count for the trend chart. */
export async function recordSnapshot(now: Date): Promise<void> {
  try {
    const onlineCount = await db.client.count({ where: onlineCondition(now) });
    const minute = Math.floor(now.getTime() / 60_000);
    await db.snapshot.upsert({
      where: { minute },
      create: { minute, onlineCount, recordedAt: now },
      update: { onlineCount },
    });
  } catch (e) {
    console.error("snapshot failed", e);
  }
}

/** Occasionally prune old snapshots (7 days) and expired sessions. */
export async function housekeeping(): Promise<void> {
  try {
    const cutoff = Date.now() - 7 * 24 * 3600_000;
    const minute = Math.floor(cutoff / 60_000);
    await db.snapshot.deleteMany({ where: { minute: { lt: minute } } });
    await db.adminSession.deleteMany({ where: { expiresAt: { lt: new Date() } } });
    await db.auditLog.deleteMany({
      where: { createdAt: { lt: new Date(cutoff) } },
    });
  } catch (e) {
    console.error("housekeeping failed", e);
  }
}

export type StatsPayload = Awaited<ReturnType<typeof computeStats>>;

export async function computeStats() {
  const now = new Date();
  const dayStart = new Date(now.getTime());
  dayStart.setHours(0, 0, 0, 0);
  const h24 = new Date(now.getTime() - 24 * 3600_000);

  const [online, total, newToday, active24h, versionGroups, osGroups, strategyGroups, recent, trend] =
    await Promise.all([
      db.client.count({ where: onlineCondition(now) }),
      db.client.count(),
      db.client.count({ where: { firstSeenAt: { gte: dayStart } } }),
      db.client.count({ where: { lastSeenAt: { gte: h24 } } }),
      db.client.groupBy({
        by: ["appVersion"],
        _count: { _all: true },
        orderBy: { _count: { appVersion: "desc" } },
        take: 6,
      }),
      db.client.groupBy({
        by: ["osInfo"],
        _count: { _all: true },
        orderBy: { _count: { osInfo: "desc" } },
        take: 6,
      }),
      db.client.groupBy({
        by: ["strategy"],
        _count: { _all: true },
        orderBy: { _count: { strategy: "desc" } },
        take: 6,
      }),
      db.client.findMany({
        orderBy: { lastSeenAt: "desc" },
        take: 25,
        select: {
          id: true,
          firstSeenAt: true,
          lastSeenAt: true,
          stoppedAt: true,
          appVersion: true,
          osInfo: true,
          strategy: true,
        },
      }),
      db.snapshot.findMany({
        where: { minute: { gte: Math.floor(h24.getTime() / 60_000) } },
        orderBy: { minute: "asc" },
      }),
    ]);

  const onlineIds = new Set(
    recent
      .filter(
        (c) =>
          c.stoppedAt === null && now.getTime() - c.lastSeenAt.getTime() <= onlineWindowMs()
      )
      .map((c) => c.id)
  );

  // Downsample the 24h minute-snapshots into 15-minute buckets (average peak).
  const BUCKET = 15;
  const bucketMap = new Map<number, { sum: number; n: number }>();
  for (const s of trend) {
    const b = Math.floor(s.minute / BUCKET) * BUCKET;
    const acc = bucketMap.get(b) ?? { sum: 0, n: 0 };
    acc.sum += s.onlineCount;
    acc.n += 1;
    bucketMap.set(b, acc);
  }
  const trendBuckets = Array.from(bucketMap.entries())
    .sort((a, b) => a[0] - b[0])
    .map(([minute, acc]) => ({
      t: minute * 60_000,
      online: Math.round((acc.sum / acc.n) * 10) / 10,
    }));

  return {
    serverTime: now.toISOString(),
    onlineWindowSeconds: onlineWindowMs() / 1000,
    online,
    total,
    newToday,
    active24h,
    versions: versionGroups.map((g) => ({ name: g.appVersion || "unknown", count: g._count._all })),
    oses: osGroups.map((g) => ({ name: g.osInfo || "unknown", count: g._count._all })),
    strategies: strategyGroups.map((g) => ({ name: g.strategy || "-", count: g._count._all })),
    recent: recent.map((c) => ({
      idMasked: c.id.slice(0, 10),
      firstSeenAt: c.firstSeenAt.toISOString(),
      lastSeenAt: c.lastSeenAt.toISOString(),
      online: onlineIds.has(c.id),
      appVersion: c.appVersion,
      osInfo: c.osInfo,
      strategy: c.strategy,
    })),
    trend: trendBuckets,
  };
}
