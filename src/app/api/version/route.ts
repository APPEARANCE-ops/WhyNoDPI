import { NextResponse } from "next/server";

export const dynamic = "force-dynamic";

/**
 * Версия приложения для встроенной проверки обновлений в WhyNoDPI.
 *
 * Клиент раз в сутки спрашивает этот адрес и сравнивает `version` со своей.
 * Значения задаются переменными окружения Vercel — чтобы выпустить обновление,
 * достаточно поменять APP_LATEST_VERSION и APP_DOWNLOAD_URL, без правки кода:
 *
 *   APP_LATEST_VERSION=1.3.0
 *   APP_DOWNLOAD_URL=https://github.com/you/why-no-dpi/releases/latest
 *   APP_RELEASE_NOTES=Исправлена проверка стратегий
 */
export async function GET() {
  const version = process.env.APP_LATEST_VERSION?.trim() || "1.3.0";
  const url = process.env.APP_DOWNLOAD_URL?.trim() || "";
  const notes = process.env.APP_RELEASE_NOTES?.trim() || "";

  return NextResponse.json(
    { version, url, notes },
    {
      status: 200,
      headers: { "cache-control": "no-store, max-age=0" },
    }
  );
}
