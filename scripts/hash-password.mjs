// Генератор хеша пароля администратора и «перца» для телеметрии.
//
//   node scripts/hash-password.mjs "мой пароль"
//   node scripts/hash-password.mjs "мой пароль" --pepper
//
// Первая строка — значение для ADMIN_PASSWORD_HASH (вставлять в .env / Vercel).
// Вторая (с --pepper) — случайный TELEMETRY_PEPPER.
// Сам пароль никуда не записывается: на выходе только scrypt-хеш.

import { randomBytes, scryptSync } from "node:crypto";

const args = process.argv.slice(2);
const withPepper = args.includes("--pepper");
const password = args.find((value) => !value.startsWith("--"));

if (!password) {
  console.error('Использование: node scripts/hash-password.mjs "пароль" [--pepper]');
  process.exit(1);
}

const N = 16384;
const r = 8;
const p = 1;
const salt = randomBytes(16);
const hash = scryptSync(password.normalize("NFKC"), salt, 64, { N, r, p });

const line = `scrypt:${N}:${r}:${p}:${salt.toString("hex")}:${hash.toString("hex")}`;

console.log("");
console.log("ADMIN_PASSWORD_HASH:");
console.log(line);
if (withPepper) {
  console.log("");
  console.log("TELEMETRY_PEPPER:");
  console.log(randomBytes(32).toString("hex"));
}
console.log("");
console.log("Пароль в открытом виде нигде не сохранён — сохраните его в менеджере паролей.");
