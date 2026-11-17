# Platform operations: who can use the service

Solvix EndPoint is a multi-tenant service. Each subscribed company is a **workspace** with its own
users, devices and data, isolated from every other company in the application and in the database
(Row-Level Security). This page explains how companies get access and how the provider controls it.

## Registration is closed

`POST /api/v1/tenant/register` answers **403** and the web app has no sign-up page. Nobody can create
a workspace by themselves. Only the **platform operator** can, so only companies you create have access.

`GET /api/v1/tenant/registration` reports `{ "open": false }`. For evaluation only, the switch
`Solvix:Platform:AllowPublicRegistration` (`SOLVIX_ALLOW_PUBLIC_REGISTRATION=true`) re-opens it; the
Companies page shows a warning while it is open. Leave it `false` in production.

## The platform operator

One account, defined by deployment configuration, can manage every company:

| Setting | Environment variable | Purpose |
|---------|----------------------|---------|
| `Solvix:Platform:Operator:Email` | `SOLVIX_PLATFORM_OPERATOR_EMAIL` | E-mail of the operator. Required by `docker compose`. |
| `Solvix:Platform:Operator:InitialPassword` | `SOLVIX_PLATFORM_OPERATOR_PASSWORD` | Used **only** to create the account if it does not exist (min. 10 characters, a letter and a digit). |
| `Solvix:Platform:Operator:DisplayName` | `SOLVIX_PLATFORM_OPERATOR_NAME` | Display name for a newly created account. |

On every start the API makes the database agree with this configuration:

* The configured e-mail is the operator and **nobody else is**. Changing or clearing the e-mail revokes
  the previous operator on the next start (that person remains a normal user of their workspaces).
* If the account does not exist it is created from the initial password, with its own workspace
  ("Platform") so it can sign in. An existing account is promoted; its password is never touched.
* The operator flag cannot be set through any API, so no customer, owner or admin can obtain it.
* Operator tokens carry a claim, but every platform call also re-checks the flag **in the database**, so
  revoking the operator takes effect immediately, even for tokens that are still valid.

Keep the operator password in your secret store, sign in once, and use a strong, unique password.
The app does not yet have a change-password screen (see Known gaps).

## Giving a company access

1. Sign in as the operator and open **Platform → Companies**.
2. Choose **New company**: company name, workspace id (derived from the name, immutable), the owner's
   name and e-mail, and the generated initial password.
3. Copy the details shown on the next screen and hand them to the company. The password is not shown again.
4. The company's owner signs in at `/login` and adds their colleagues under **Settings → Workspace**
   (roles: Owner, Admin, Member). They install agents from **Endpoints → Enrollment**.

The same actions are available over the API for automation (operator token required):

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/platform/summary` | Totals: companies, active, suspended |
| `GET /api/v1/platform/tenants?search=&status=&page=&pageSize=` | List companies with owners, user and device counts |
| `POST /api/v1/platform/tenants` | Create a workspace and its first owner |
| `POST /api/v1/platform/tenants/{id}/suspend` | Block a company (e.g. subscription ended) |
| `POST /api/v1/platform/tenants/{id}/reactivate` | Restore it |

## Suspending and reactivating

Suspending is immediate and reversible, and deletes nothing:

* Every user of the company is refused (their open sessions show "This workspace is suspended") and
  cannot sign in; its agents can no longer report.
* Reactivating restores access with all data intact.
* You cannot suspend the operator's own workspace.

Suspensions and creations are written to the API log with the operator's user id.

## What the operator can and cannot see

The console shows company names, workspace ids, owner e-mails, user and device **counts** and status.
It does not give the operator access to a company's devices, inventory or users: to see those, sign in
as a member of that workspace (which the company controls).

## Known gaps

* No password change or reset in the app yet: owners keep the initial password you hand over until a
  change-password feature is added.
* No subscription tracking (plan, seats, expiry, billing). Suspension is a manual decision.
* Deleting a company is not offered; suspend instead.
* Platform actions are logged but there is no in-app audit trail yet.
