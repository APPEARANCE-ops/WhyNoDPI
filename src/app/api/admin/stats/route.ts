import { NextRequest, NextResponse } from "next/server";
import { rateLimit, ADMIN_API_LIMIT } from "@/lib/rate-limit";
import { readSessionFromRequest } from "@/lib/admin-session";
import { computeStats } from "@/lib/telemetry";
import { getClientIp, hashIp } from "@/lib/request-utils";

export const dynamic = "force-dynamic";

export async function GET(request: NextRequest) {
  const session = await readSessionFromRequest(request);
  if (!session) {
    return NextResponse.json({ ok: false, error: "unauthorized" }, { status: 401 });
  }

  const limit = rateLimit(`stats:${hashIp(getClientIp(request.headers))}`, ADMIN_API_LIMIT);
  if (!limit.allowed) {
    return NextResponse.json(
      { ok: false, error: "rate_limited", retryAfter: limit.retryAfterSeconds },
      { status: 429, headers: { "retry-after": String(limit.retryAfterSeconds) } }
    );
  }

  const stats = await computeStats();
  const response = NextResponse.json({ ok: true, stats }, { status: 200 });
  // Stats must never be cached by intermediaries.
  response.headers.set("cache-control", "no-store, max-age=0");
  return response;
}
