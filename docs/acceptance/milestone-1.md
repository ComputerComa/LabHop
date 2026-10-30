# Milestone 1 acceptance criteria

Derived from `AGENTS.md`. Tests assert these outcomes at application boundaries. Each criterion lists
failure cases where they matter.

## Accounts and browser sessions

- A1. Accounts are created only by the `create-user` CLI command. No web page or endpoint registers a
  user.
- A2. An anonymous browser requesting any application page is redirected to the login page.
- A3. Login rejects a wrong password, and repeated failures lock the account.
- A4. `ReturnUrl` cannot redirect off-site after login.

## Projects (website and MCP share one service)

- P1. An authenticated user can create, list, read, and edit their own projects: name, goal,
  description, current plan, status, archive state.
- P2. A user cannot see, read, change, or archive another user's project. The outcome is identical to
  a project that does not exist, and no change or activity is recorded.
- P3. Invalid input (blank name, over-length field, undefined status) is rejected with a message
  naming the field, and nothing is saved.
- P4. An archived project is hidden from the default list, can be listed on request, and cannot be
  edited until restored.
- P5. Results are bounded: list limits are capped server-side, whatever the caller requests.
- P6. Times are stored in UTC.

## Concurrency

- C1. An update carrying a stale version fails with a conflict and leaves the newer data intact.
- C2. Two concurrent updates from the same version: exactly one succeeds. The other gets a conflict,
  even when both passed the initial version check.
- C3. A stale archive or restore is also rejected.
- C4. The website shows a conflict without losing the text the user typed.

## Activity

- H1. Every create, change, archive, and restore records who, what, and when, in the same transaction
  as the change.
- H2. Human and agent actions are distinguishable; agent entries name the OAuth client.
- H3. A rejected operation (conflict, validation, not found) records nothing. A no-op update records
  nothing.
- H4. An update records the changed fields with their previous and new values.

## Database operations

- D1. Normal startup never applies migrations.
- D2. With pending migrations, startup fails with a message naming the command to run. The database is
  left unchanged.
- D3. The `migrate` command applies migrations against real PostgreSQL.

## MCP protocol behavior

- M1. `/mcp` with no token: HTTP 401, no redirect, no HTML, `WWW-Authenticate: Bearer` with
  `resource_metadata` and `scope`.
- M2. `/mcp` with a valid browser cookie and no bearer token: same as M1. Cookies never authenticate
  MCP.
- M3. `/mcp` with an invalid token: HTTP 401 with `error="invalid_token"`.
- M4. A token with no LabHop scope: HTTP 403 with `error="insufficient_scope"`.
- M5. A read-only token can list projects and read context. Calling a write tool returns HTTP 403 with
  `error="insufficient_scope"` and a `scope` listing what is needed. The write tool is not listed for
  that token, and no change is made even if the HTTP-level check is bypassed.
- M6. A read/write token can update a project through MCP. The change goes through the same rules
  (validation, concurrency, archive) and is recorded as an agent action.
- M7. A stale `expectedVersion` returns an actionable tool error, and nothing is saved.
- M8. A token for user A never returns user B's data from any tool. Tools have no user parameter; a
  supplied `userId` argument changes nothing.
- M9. Requests with a disallowed `Origin` get HTTP 403, including through path variations such as
  `/mcp/`.
- M10. Tool results use explicit DTOs and contain no owner IDs.
- M11. Project context is bounded in size.
- M12. Access tokens are accepted only in the `Authorization` header, not in the query string or a
  form body.
- M13. Methods other than POST on `/mcp` get protocol responses: 401 without a token, 405 with one.
  Unknown paths under `/mcp` get 404. None of them redirect to the login page.

## OAuth

- O1. Protected resource metadata is served at the root and at the `/mcp`-suffixed path. Both report the
  canonical resource and an issuer byte-identical to the authorization server metadata.
- O2. Authorization server metadata is served at both well-known paths. It advertises `S256` as the
  only PKCE method, the code grant and refresh grant only, and no implicit or password flows.
- O3. Code flow with PKCE works end to end: login, consent, code, token, MCP call.
- O4. Missing PKCE, `plain` PKCE, a wrong verifier, a reused code, an unknown client, and an
  unregistered redirect URI are all rejected. OpenIddict reports authorization requests that fail
  validation on its own error page rather than by redirect, so a client does not receive these errors.
- O5. A `resource` other than the canonical MCP URI gets `invalid_target`. Issued tokens work only for
  the canonical resource; a token issued for a different LabHop instance is rejected.
- O6. Loopback redirect URIs match on any port for loopback clients. A non-loopback host does not.
- O7. The consent page shows the client, the redirect host, a loopback warning when relevant, and the
  requested access. It requires a valid antiforgery token.
- O8. Denying consent returns `access_denied` to the client. No tokens are issued.
- O9. Withholding write access at consent yields a read-only token even if write was requested, and a
  later refresh cannot widen it.
- O10. Refresh tokens rotate; a replayed refresh token is rejected.
- O11. Revoking a connection from the website, or through the revocation endpoint, stops the access and
  refresh tokens immediately. Users can list and revoke only their own connections.
- O12. Signing, encryption, and Data Protection keys persist. A token issued before a restart still
  works after it.
- O13. No token, code, or password appears in logs during the flow.
- O14. A real MCP client completes login and calls a tool. The client used, and what remains
  unverified, is reported rather than implied.

## UI (browser check)

- U1. Log in, create, list, view, edit, and archive a project in a real browser.
- U2. Open the same project in two tabs, save in both, and see the conflict in the second, with its
  typed text preserved.
- U3. Project text containing HTML or script is displayed literally.
- U4. List and revoke a connected agent.
