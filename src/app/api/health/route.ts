import { NextResponse } from "next/server";
import { db } from "@/lib/db";

export const dynamic = "force-dynamic";

/**
 * Публичный health-check. Ничего секретного не отдаёт: нужен для того, чтобы
 * приложение WhyNoDPI могло проверить адрес панели кнопкой «Проверить связь»,
 * а вы — убедиться, что после деплоя на Vercel жива база.
 */
export async function GET() {
  let database: "up" | "down" = "up";
  try {
    await db.$queryRaw`SELECT 1`;
  } catch (error) {
    console.error("health: database check failed", error);
    database = "down";
  }

  return NextResponse.json(
    {
      ok: database === "up",
      service: "whynodpi-panel",
      database,
      time: new Date().toISOString(),
    },
    {
      status: database === "up" ? 200 : 503,
      headers: { "cache-control": "no-store, max-age=0" },
    }
  );
}
