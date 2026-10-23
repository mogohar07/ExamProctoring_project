# Exam Proctoring – instructor web dashboard

A React (TypeScript, Vite) dashboard for instructors to manage their exams on the ASP.NET Core backend in `../src`:
sign in, list and filter exams, create and edit exams, manage MCQ and Essay questions, and publish, unpublish or
cancel exams.

The student exam-taking pages are **not** built. The backend has no student exam endpoints yet: Step 2.5 is blocked
by the open decision in `architecture-proposal.md` §2.4.

## Backend endpoints used

Every request and response type is in `src/types/api.ts`, mirroring the backend DTOs.

| Area | Endpoints |
|---|---|
| Authentication | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/change-password`, `POST /api/auth/logout` |
| Exams | `GET /api/exams?status=&page=&pageSize=`, `POST /api/exams`, `GET /api/exams/{id}`, `PUT /api/exams/{id}`, `DELETE /api/exams/{id}` |
| Lifecycle | `POST /api/exams/{id}/publish`, `/unpublish`, `/cancel` |
| Questions | `GET` and `POST /api/exams/{id}/questions`, `PUT` and `DELETE /api/questions/{id}` |

## Requirements

- Node.js 22.12 or later (24 LTS recommended), which includes npm.
- The backend running locally, with its database migrated and a first admin seeded (see below).

## Local development

**1. Start the backend** from the repository root:

```bash
dotnet run --project src/ExamProctoring.Api --launch-profile https
```

- It listens on `https://localhost:7046` (`src/ExamProctoring.Api/Properties/launchSettings.json`).
- It refuses to start while the database has no admin and `Seed:AdminEmail` and `Seed:AdminPassword` are not configured (User Secrets).

**2. Configure the frontend** (one time):

```bash
cd frontend
cp .env.example .env.local
npm install
```

**3. Start the frontend:**

```bash
npm run dev
```

Then open `http://localhost:5173`.

**Why requests go through a proxy:** in development the Vite server forwards `/api/*` to `VITE_DEV_PROXY_TARGET`, the backend's HTTPS URL. The browser therefore only talks to its own origin, and the backend needs no CORS entry. The proxy skips the TLS certificate check by default (`VITE_DEV_PROXY_VERIFY_TLS=false`), because Node.js doesn't trust the ASP.NET Core development certificate. This applies only to the local dev proxy.

**4. Sign in with an instructor account.**
1. An admin creates the instructor (`POST /api/admin/instructors`).
2. The instructor signs in with the temporary password.
3. The dashboard then asks for a new password.

### Calling the backend directly (without the proxy)

Set `VITE_API_BASE_URL=https://localhost:7046`. The backend must then allow the frontend's origin. Today `Cors:WebExamOrigins` in `src/ExamProctoring.Api/appsettings.json` is empty, so browsers block these calls. Adding the origin is a backend configuration change (decision D24) and needs approval. A narrowly scoped development value would be `http://localhost:5173`.

## Scripts

| Command | What it does |
|---|---|
| `npm run dev` | Development server with the API proxy |
| `npm run typecheck` | TypeScript check |
| `npm test` | Unit and component tests (Vitest, Testing Library, jsdom) |
| `npm run build` | Type check, then the production build into `dist/` |

## How the frontend uses authentication

- The access token (15 minutes) is kept **in memory** and sent as `Authorization: Bearer …`.
- The refresh token (7 days) is kept in **sessionStorage**, so a reload in the same tab can continue the session; it is cleared when the tab closes.
- Before the access token expires, or after a `401 token_invalid`, the client refreshes **once** and retries.
  - The backend rotates refresh tokens and treats a reused one as theft, so refreshes never run in parallel.
- If the refresh is rejected, the user is signed out.

**Limitation:** any script running on the page could read sessionStorage. The app renders only plain text (React escapes it). The safer alternative, an HttpOnly cookie, would need backend changes; the backend currently returns tokens only in JSON.

**Role check:** the role check in the UI (instructors only) is for convenience. The backend enforces roles, account status and ownership on every request.

## Times

Exam times are entered and shown in the browser's local time zone.
- **Sending:** the start time is sent with the local offset, for example `2026-11-01T09:00:00+03:00`. The backend requires an explicit offset and stores UTC.
- **Receiving:** responses are in UTC and are displayed in local time.
