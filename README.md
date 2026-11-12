# WhyNoDPI

Обход блокировок YouTube, Discord и Telegram на Windows (пакет Flowseal / zapret)
плюс приватная панель статистики, которая показывает, сколько людей запускают
приложение.

## Скачать

Готовые сборки лежат в разделе **Releases** или собираются из исходников:

| Сборка | Для кого | Отличия |
|---|---|---|
| `WhyNoDPI-*-Admin.exe` | автор | адрес панели задаётся в настройках, есть диагностика |
| `WhyNoDPI-*-Client.exe` | пользователи | адрес панели вшит, настраивать нечего |

## Что в репозитории

| Путь | Что это |
|---|---|
| `download/WhyNoDPI` | WPF-приложение (Windows x64, .NET 10) |
| `src` | админ-панель и приём телеметрии (Next.js + Prisma) |
| `prisma` | схемы БД: SQLite для локального запуска, Postgres для Vercel, готовый SQL |
| `scripts` | `hash-password.mjs` — генератор хеша админского пароля |
| `README-VERCEL.md` | пошаговый деплой панели на Vercel + Neon |
| `АДМИН_БЫСТРЫЙ_СТАРТ.md` | что нажать, чтобы получить доступ к статистике |
| `СПИСОК_ИЗМЕНЕНИЙ.md` | что и зачем менялось |

## Собрать приложение

```powershell
cd download\WhyNoDPI
.\Build-EXE.ps1                                   # версия для автора
.\Build-User.cmd https://ваш-домен.vercel.app     # версия для пользователей
```

Нужен .NET 10 SDK. Готовый EXE ~59 МБ, самодостаточный, без установки.
GitHub Actions собирает его же: вкладка **Actions → Сборка WhyNoDPI для Windows**.

## Развернуть панель

Коротко: база Postgres (Neon) → вставить `prisma/schema.postgres.sql` в SQL Editor →
на Vercel задать `DATABASE_URL`, `ADMIN_PASSWORD_HASH`, `TELEMETRY_PEPPER` → Deploy.
Подробно — в [README-VERCEL.md](README-VERCEL.md).

## Как это работает

```
WhyNoDPI.exe ──анонимный heartbeat раз в 60 с──▶ /api/heartbeat ──▶ Postgres
                                                                        │
вы ──пароль──▶ админ-панель: онлайн, всего, новые, тренд 24 ч ◀──────────┘
```

Отправляются только случайный номер установки, версия, строка ОС и имя профиля.
Ни IP, ни путей, ни паролей, ни секретов прокси. Установка хешируется на сервере
с секретным «перцем», так что связать запись с человеком нельзя.

## Благодарности

Пакет профилей — [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube).
WhyNoDPI — независимый интерфейс и не является официальным продуктом Flowseal.
