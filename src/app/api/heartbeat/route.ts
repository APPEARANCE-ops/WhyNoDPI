import { NextRequest, NextResponse } from "next/server";
import { z } from "zod";
import { db } from "@/lib/db";
import { rateLimit, HEARTBEAT_LIMIT } from "@/lib/rate-limit";
import { audit, getClientIp, hashIp, hashInstallId } from "@/lib/request-utils";
import { recordSnapshot, housekeeping } from "@/lib/telemetry";

export const dynamic = "force-dynamic";

/**
 * Public telemetry endpoint for WhyNoDPI desktop clients.
 *
 * Design notes:
 *  - Accepts a strictly validated, small JSON payload (zod + size cap).
 *  - No cookies, no PII: installId is hashed with a server pepper before storage.
 *  - Rate limited per IP; abusive callers are dropped with 429.
 *  - Never returns anything but a tiny status object (no data leak).
 */

const heartbeatSchema = z.preprocess(
  (raw) => {
    // Десктопный клиент сериализует поля через System.Text.Json с политикой
    // SnakeCaseLower, то есть отправляет install_id. Принимаем оба варианта,
    // чтобы смена сериализатора на клиенте не ломала приём телеметрии.
    if (raw === null || typeof raw !== "object" || Array.isArray(raw)) return raw;
    const value = { ...(raw as Record<string, unknown>) };
    if (value.installId === undefined && typeof value.install_id === "string") {
      value.installId = value.install_id;
    }
    return value;
  },
  z.object({
    installId: z
      .string()
      .min(8)
      .max(64)
      .regex(/^[0-9a-fA-F-]{8,64}$/, "installId must be a GUID"),
    version: z.string().max(24).optional(),
    os: z.string().max(64).optional(),
    strategy: z.string().max(96).optional(),
    event: z.enum(["start", "heartbeat", "stop"]).default("heartbeat"),
  })
);

const MAX_BODY_BYTES = 4096;

export async function POST(request: NextRequest) {
  const headers = request.headers;
  const ip = getClientIp(headers);

  // Rate limit: 30 req/min per IP is generous for a 60s heartbeat.
  const limit = rateLimit(`hb:${hashIp(ip)}`, HEARTBEAT_LIMIT);
  if (!limit.allowed) {
    await audit("RATE_LIMITED", headers, "heartbeat");
    return NextResponse.json(
      { ok: false, error: "rate_limited", retryAfter: limit.retryAfterSeconds },
      { status: 429, headers: { "retry-after": String(limit.retryAfterSeconds) } }
    );
  }

  // Hard body-size cap (defence in depth beyond server limits).
  const contentLength = Number(headers.get("content-length") ?? "0");
  if (contentLength > MAX_BODY_BYTES) {
    return NextResponse.json({ ok: false, error: "payload_too_large" }, { status: 413 });
  }

  let raw: string;
  try {
    raw = await request.text();
  } catch {
    return NextResponse.json({ ok: false, error: "bad_request" }, { status: 400 });
  }
  if (Buffer.byteLength(raw, "utf8") > MAX_BODY_BYTES) {
    return NextResponse.json({ ok: false, error: "payload_too_large" }, { status: 413 });
  }

  let parsedJson: unknown;
  try {
    parsedJson = JSON.parse(raw);
  } catch {
    return NextResponse.json({ ok: false, error: "invalid_json" }, { status: 400 });
  }

  const parsed = heartbeatSchema.safeParse(parsedJson);
  if (!parsed.success) {
    await audit("HEARTBEAT_REJECTED", headers, "validation");
    return NextResponse.json({ ok: false, error: "validation_failed" }, { status: 400 });
  }

  const { installId, version, os, strategy, event } = parsed.data;
  const clientId = hashInstallId(installId);
  const now = new Date();

  try {
    if (event === "stop") {
      await db.client.updateMany({
        where: { id: clientId },
        data: { lastSeenAt: now, stoppedAt: now },
      });
    } else {
      const existing = await db.client.findUnique({ where: { id: clientId }, select: { id: true } });
      if (existing) {
        await db.client.update({
          where: { id: clientId },
          data: {
            lastSeenAt: now,
            stoppedAt: null, // a fresh heartbeat cancels an earlier "stop"
            ...(version !== undefined ? { appVersion: version } : {}),
            ...(os !== undefined ? { osInfo: os } : {}),
            ...(strategy !== undefined ? { strategy } : {}),
          },
        });
      } else {
        await db.client.create({
          data: {
            id: clientId,
            firstSeenAt: now,
            lastSeenAt: now,
            stoppedAt: null,
            appVersion: version ?? "unknown",
            osInfo: os ?? "windows",
            strategy: strategy ?? "-",
          },
        });
      }
    }

    await recordSnapshot(now);

    // Light background maintenance (~5% of requests).
    if (Math.random() < 0.05) {
      await housekeeping();
    }
  } catch (e) {
    console.error("heartbeat db error", e);
    return NextResponse.json({ ok: false, error: "server_error" }, { status: 500 });
  }

  return NextResponse.json({ ok: true, event }, { status: 202 });
}

export async function GET() {
  // Intentionally minimal: no information disclosure on the public endpoint.
  return NextResponse.json({ ok: true, service: "whynodpi-telemetry" }, { status: 200 });
}
