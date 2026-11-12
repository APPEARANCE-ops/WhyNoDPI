import { NextRequest, NextResponse } from "next/server";
import { z } from "zod";
import { db } from "@/lib/db";
import {
  ADMIN_COOKIE,
  cookieOptions,
  generateSessionToken,
  hashToken,
  sessionExpiry,
  verifyAdminPassword,
} from "@/lib/admin-auth";
import { rateLimit, recordFailure, resetFailures, LOGIN_LIMIT, LOGIN_LOCK } from "@/lib/rate-limit";
import { checkLoginLock } from "@/lib/login-throttle";
import { audit, getClientIp, hashIp, sameOrigin } from "@/lib/request-utils";

export const dynamic = "force-dynamic";

const loginSchema = z.object({
  password: z.string().min(1).max(200),
});

export async function POST(request: NextRequest) {
  const headers = request.headers;

  // CSRF: browsers send Origin on cross-site posts; reject mismatches.
  if (!sameOrigin(headers)) {
    await audit("DENIED", headers, "login:bad-origin");
    return NextResponse.json({ ok: false, error: "forbidden" }, { status: 403 });
  }

  const ipHash = hashIp(getClientIp(headers));

  // Brute-force protection: sliding window + hard lockout.
  const limit = rateLimit(`login:${ipHash}`, { ...LOGIN_LIMIT, ...LOGIN_LOCK });
  if (!limit.allowed) {
    await audit("RATE_LIMITED", headers, "login");
    return NextResponse.json(
      { ok: false, error: "too_many_attempts", retryAfter: limit.retryAfterSeconds },
      { status: 429, headers: { "retry-after": String(limit.retryAfterSeconds) } }
    );
  }

  // Serverless (Vercel): in-memory counters are per-instance, so the shared
  // audit log decides whether this IP is locked out.
  const dbLock = await checkLoginLock(ipHash);
  if (dbLock.locked) {
    await audit("RATE_LIMITED", headers, "login:db-lock");
    return NextResponse.json(
      { ok: false, error: "too_many_attempts", retryAfter: dbLock.retryAfterSeconds },
      { status: 429, headers: { "retry-after": String(dbLock.retryAfterSeconds) } }
    );
  }

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ ok: false, error: "bad_request" }, { status: 400 });
  }

  const parsed = loginSchema.safeParse(body);
  if (!parsed.success) {
    return NextResponse.json({ ok: false, error: "bad_request" }, { status: 400 });
  }

  const { password } = parsed.data;

  if (!verifyAdminPassword(password)) {
    recordFailure(`login:${ipHash}`, LOGIN_LOCK);
    await audit("LOGIN_FAIL", headers);
    // Generic message: do not reveal whether the password exists.
    return NextResponse.json({ ok: false, error: "invalid_credentials" }, { status: 401 });
  }

  resetFailures(`login:${ipHash}`);
  await audit("LOGIN_OK", headers);

  // Prune expired sessions opportunistically.
  await db.adminSession.deleteMany({ where: { expiresAt: { lt: new Date() } } }).catch(() => undefined);

  const token = generateSessionToken();
  await db.adminSession.create({
    data: {
      id: hashToken(token),
      expiresAt: sessionExpiry(),
      userAgent: (headers.get("user-agent") ?? "unknown").slice(0, 200),
    },
  });

  const response = NextResponse.json({ ok: true }, { status: 200 });
  response.cookies.set(ADMIN_COOKIE, token, cookieOptions(headers));
  return response;
}
