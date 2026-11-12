import { db } from "@/lib/db";

/**
 * Блокировка входа, которая работает на Vercel.
 *
 * In-memory лимитер (src/lib/rate-limit.ts) живёт внутри одного lambda-инстанса,
 * а на Vercel каждый холодный старт — новый инстанс, поэтому попытки подбора
 * пароля не накапливались. Здесь счётчик берётся из журнала аудита, который
 * общий для всех инстансов.
 */

const WINDOW_MS = 10 * 60_000;
const MAX_FAILURES = 5;

export type LoginLock = {
  locked: boolean;
  retryAfterSeconds: number;
};

export async function checkLoginLock(ipHash: string): Promise<LoginLock> {
  try {
    const since = new Date(Date.now() - WINDOW_MS);
    const failures = await db.auditLog.count({
      where: {
        event: "LOGIN_FAIL",
        ipHash,
        createdAt: { gte: since },
      },
    });

    if (failures < MAX_FAILURES)
      return { locked: false, retryAfterSeconds: 0 };

    return { locked: true, retryAfterSeconds: Math.ceil(WINDOW_MS / 1000) };
  } catch (error) {
    // Если база недоступна, не блокируем вход навсегда: in-memory лимитер
    // и проверка пароля всё равно работают.
    console.error("login lock check failed", error);
    return { locked: false, retryAfterSeconds: 0 };
  }
}
