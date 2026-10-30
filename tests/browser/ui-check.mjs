// Browser check for acceptance criteria U1-U4, run against a running LabHop instance with a real browser.
// See tests/browser/README.md. Set BASE_URL, LABHOP_EMAIL, LABHOP_PASSWORD, and optionally CLIENT_ID and SHOTS.
import { createHash, randomBytes } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { chromium } from 'playwright';

const base = process.env.BASE_URL.replace(/\/$/, '');
const email = process.env.LABHOP_EMAIL;
const password = process.env.LABHOP_PASSWORD;
const clientId = process.env.CLIENT_ID ?? 'claude-code';
const shots = process.env.SHOTS ?? 'shots';
mkdirSync(shots, { recursive: true });

let failures = 0;
const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${!ok && detail ? ` -- ${detail}` : ''}`);
  if (!ok) failures++;
};

const browser = await chromium.launch();
const context = await browser.newContext({
  ignoreHTTPSErrors: true,
  timezoneId: 'Pacific/Auckland',
  viewport: { width: 1280, height: 900 },
});
const page = await context.newPage();

const problems = [];
const watch = (p) => {
  p.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
  p.on('console', (m) => {
    if (m.type() === 'error' && !m.text().includes('49999')) problems.push(`console: ${m.text()}`);
  });
};
watch(page);

const ready = (p) => p.waitForFunction(() => window.Blazor !== undefined && document.querySelector('.rz-layout, .labhop-card') !== null);
const shot = (p, name) => p.screenshot({ path: `${shots}/${name}.png`, fullPage: true });

async function setText(p, name, value) {
  const field = p.locator(`input[name="${name}"], textarea[name="${name}"]`);
  await field.fill(value);
  await field.blur();
}

// --- Login (A2, A3, U1)
await page.goto(`${base}/projects`);
check('anonymous visitor lands on the login page', page.url().includes('/Account/Login'), page.url());
await shot(page, '01-login');

await page.fill('input[name="Input.Email"]', email);
await page.fill('input[name="Input.Password"]', 'definitely-not-the-password');
await page.click('button[type="submit"]');
await page.waitForSelector('.labhop-status-error');
check('wrong password shows an error', (await page.locator('.labhop-status-error').innerText()).includes('Invalid'));

await page.fill('input[name="Input.Email"]', email);
await page.fill('input[name="Input.Password"]', password);
await page.click('button[type="submit"]');
await page.waitForURL(/\/projects/);
await page.waitForSelector('button:has-text("New project")');
check('login returns to the requested page', page.url().endsWith('/projects'), page.url());

// --- Create, with markup in the text (U1, U3)
const marker = `Rack ${Date.now()}`;
const hostileName = `${marker} <img src=x onerror="window.__xss=1"><b>bold</b>`;
await page.click('button:has-text("New project")');
await page.waitForSelector('input[name="Name"]');
await setText(page, 'Name', hostileName);
await setText(page, 'Goal', '<script>window.__xss=2</script> Keep it quiet');
await setText(page, 'Description', 'Line one\nLine two');
await setText(page, 'CurrentPlan', 'Measure the closet first');
await shot(page, '02-create-form');
await page.click('button:has-text("Create project")');
await page.waitForSelector('button:has-text("Archive")');
const projectUrl = page.url();
check('create goes to the details page', /\/projects\/[0-9a-f-]{36}$/.test(projectUrl), projectUrl);
await shot(page, '03-details');

const heading = await page.locator('h1').innerText();
check('markup in the name is displayed literally', heading.includes('<b>bold</b>'), heading);
check('markup does not become elements', (await page.locator('.labhop-page b, .labhop-page img[src="x"]').count()) === 0);
check('scripts in project text do not run', (await page.evaluate(() => window.__xss)) === undefined);
const goal = await page.locator('.labhop-text').first().innerText();
check('script text in the goal is displayed literally', goal.includes('<script>window.__xss=2</script>'), goal);
check('activity shows a human created it', (await page.locator('.labhop-activity').innerText()).includes('created this project'));
const time = page.locator('.labhop-activity time').first();
await page.waitForFunction(() => !document.querySelector('.labhop-activity time')?.textContent.includes('UTC'));
check('times display in the browser time zone', !(await time.innerText()).includes('UTC'), await time.innerText());
check('the UTC time stays available on hover', ((await time.getAttribute('title')) ?? '').includes('UTC'));

// --- List and filters (U1)
await page.goto(`${base}/projects`);
await page.waitForSelector(`a:has-text("${marker}")`);
check('the new project is listed', true);
await shot(page, '04-list');

// --- Two tabs editing the same project (C4, U2)
const editUrl = `${projectUrl}/edit`;
const tabA = await context.newPage();
const tabB = await context.newPage();
watch(tabA);
watch(tabB);
await tabA.goto(editUrl);
await tabB.goto(editUrl);
await tabA.waitForSelector('textarea[name="Goal"]');
await tabB.waitForSelector('textarea[name="Goal"]');

await setText(tabA, 'Goal', 'Saved first from tab A');
await tabA.click('button:has-text("Save changes")');
await tabA.waitForURL(projectUrl);
check('first save succeeds', true);

const typed = 'Typed in tab B, must not be lost';
await setText(tabB, 'Goal', typed);
await tabB.click('button:has-text("Save changes")');
await tabB.waitForSelector('text=Someone else changed this project');
check('second save reports a conflict', true);
check('the second tab keeps its typed text', (await tabB.locator('textarea[name="Goal"]').inputValue()) === typed);
await shot(tabB, '05-conflict');

await tabA.reload();
await tabA.waitForSelector('.labhop-text');
check('the newer value was not overwritten', (await tabA.locator('.labhop-text').first().innerText()) === 'Saved first from tab A');

await tabB.click('button:has-text("Discard my edits")');
await tabB.waitForFunction(() => document.querySelector('textarea[name="Goal"]')?.value === 'Saved first from tab A');
await setText(tabB, 'Goal', 'Saved after reloading');
await tabB.click('button:has-text("Save changes")');
await tabB.waitForURL(projectUrl);
check('saving works after loading the latest version', true);
await tabA.close();
await tabB.close();

// --- Archive and restore (P4, U1)
await page.goto(projectUrl);
await page.waitForSelector('button:has-text("Archive")');
await page.click('button:has-text("Archive")');
await page.waitForSelector('button:has-text("Restore")');
// Case-insensitive because the badge's CSS uppercases its text, and innerText reports it that way.
check('archiving shows the archived state', (await page.locator('.labhop-page').innerText()).toLowerCase().includes('archived'));
check('an archived project offers no Edit button', (await page.locator('button:has-text("Edit")').count()) === 0);

await page.goto(`${base}/projects`);
await page.waitForSelector('.rz-data-grid');
await page.waitForTimeout(500);
check('archived projects are hidden by default', (await page.locator(`a:has-text("${marker}")`).count()) === 0);
await page.click('.rz-switch');
await page.waitForSelector(`a:has-text("${marker}")`);
check('archived projects appear when requested', true);
await shot(page, '06-list-archived');

await page.goto(projectUrl);
await page.waitForSelector('button:has-text("Restore")');
await page.click('button:has-text("Restore")');
await page.waitForSelector('button:has-text("Archive")');
check('restoring brings the project back', true);

// --- Consent, then connections and revocation (O7, U4)
const verifier = randomBytes(32).toString('base64url');
const challenge = createHash('sha256').update(verifier).digest('base64url');
const redirect = 'http://localhost:49999/callback';
const authorize = new URL(`${base}/connect/authorize`);
Object.entries({
  response_type: 'code',
  client_id: clientId,
  redirect_uri: redirect,
  scope: 'labhop.read labhop.write offline_access',
  state: 'state-value',
  code_challenge: challenge,
  code_challenge_method: 'S256',
  resource: `${base}/mcp`,
}).forEach(([key, value]) => authorize.searchParams.set(key, value));

await page.goto(authorize.toString());
await page.waitForSelector('button:has-text("Approve")');
const consent = await page.locator('main').innerText();
check('consent names the signed-in user', consent.includes(email));
check('consent shows the redirect host', consent.includes('localhost'));
check('consent warns about a loopback redirect', consent.includes('on your own computer'));
check('consent offers write access as an unticked choice', !(await page.locator('input[type="checkbox"]').isChecked()));
await shot(page, '07-consent');
await page.setViewportSize({ width: 390, height: 844 });
await shot(page, '07-consent-phone');
check('consent fits a phone without sideways scrolling', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
await page.setViewportSize({ width: 1280, height: 900 });

const callback = page.waitForRequest((request) => request.url().startsWith(redirect));
await page.click('button:has-text("Approve")');
const code = new URL((await callback).url()).searchParams.get('code');
check('approval sends a code to the client', Boolean(code));

const token = await context.request.post(`${base}/connect/token`, {
  form: { grant_type: 'authorization_code', client_id: clientId, redirect_uri: redirect, code, code_verifier: verifier, resource: `${base}/mcp` },
});
const tokens = await token.json();
check('the code redeems for tokens', token.ok() && Boolean(tokens.access_token), JSON.stringify(tokens.error ?? ''));
check('unticked write access is not granted', !String(tokens.scope).includes('labhop.write'), tokens.scope);

const callMcp = () => context.request.post(`${base}/mcp`, {
  headers: { Authorization: `Bearer ${tokens.access_token}`, Accept: 'application/json, text/event-stream', 'Content-Type': 'application/json' },
  data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'list_projects', arguments: {} } },
});
check('the token works at /mcp through the proxy', (await callMcp()).status() === 200);

await page.goto(`${base}/agents`);
await page.waitForSelector('button:has-text("Revoke")');
const agents = await page.locator('.labhop-page').innerText();
check('the connection is listed', agents.includes('Read only'), agents.slice(0, 200));
await shot(page, '08-agents');

await page.click('button:has-text("Revoke")');
await page.waitForSelector('.rz-dialog');
await page.locator('.rz-dialog button:has-text("Revoke")').click();
await page.waitForSelector('text=No agents are connected.');
check('revoking removes the connection', true);
check('the revoked token stops working immediately', (await callMcp()).status() === 401);

// --- Layout at phone width
await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${base}/projects`);
await page.waitForSelector('.rz-data-grid');
await page.waitForTimeout(500);
await shot(page, '09-list-phone');
check('the project list fits a phone without page scrolling sideways', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
const nameBox = await page.locator('.rz-data-grid tbody a').first().boundingBox();
check('project names are readable on a phone', Boolean(nameBox) && nameBox.width > 100 && nameBox.x >= 0 && nameBox.x < 390, JSON.stringify(nameBox));
await page.setViewportSize({ width: 1280, height: 900 });

// --- Account pages reached from the interactive app
await page.goto(`${base}/projects`);
await page.waitForSelector('button:has-text("New project")');
await page.click('a:has-text("Change password"), .rz-navigation-item:has-text("Change password")');
await page.waitForSelector('input[name="Input.OldPassword"]');
check('change password opens from the menu', true);

// --- Logout
await page.goto(`${base}/projects`);
await page.waitForSelector('.labhop-logout button');
await page.click('.labhop-logout button');
await page.waitForURL(/\/Account\/Login/);
check('log out returns to the login page', true);
await page.goto(`${base}/projects`);
check('the session really ended', page.url().includes('/Account/Login'), page.url());

check('no browser errors during the run', problems.length === 0, problems.slice(0, 5).join(' | '));

await browser.close();
console.log(failures === 0 ? '\nAll browser checks passed.' : `\n${failures} browser check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
