const counterNames = [
  'gaming', 'studyWork', 'chill', 'customGameProfile', 'applied',
  'withoutChanges', 'failed', 'blockedByPolicy', 'adaptiveMemoryAdjustments',
];
const aggregateColumns = counterNames.map(name => `SUM(json_extract(counters, '$.${name}')) AS ${name}`).join(', ');
const maxBodyBytes = 2048;
const utcDay = (now, offset = 0) => new Date(now.getTime() - offset * 86400000).toISOString().slice(0, 10);

function respond(status) {
  return new Response(null, { status, headers: { 'Cache-Control': 'no-store' } });
}

async function readBatch(request) {
  if (!request.body) return null;
  const reader = request.body.getReader();
  const bytes = new Uint8Array(maxBodyBytes);
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (length + value.byteLength > maxBodyBytes) {
        await reader.cancel();
        return null;
      }
      bytes.set(value, length);
      length += value.byteLength;
    }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, length)));
  } catch {
    return null;
  } finally {
    reader.releaseLock();
  }
}

function validateBatch(data) {
  if (!data || typeof data !== 'object' || Object.keys(data).length !== 2 || typeof data.batchId !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(data.batchId)) return null;
  const { summary } = data;
  if (!summary || typeof summary !== 'object' || Object.keys(summary).length !== counterNames.length + 1 ||
      typeof summary.version !== 'string' || !/^(0|[1-9]\d{0,3})(\.(0|[1-9]\d{0,3})){2}$/.test(summary.version)) return null;
  const counters = {};
  for (const name of counterNames) {
    const value = summary[name];
    if (!Number.isSafeInteger(value) || value < 0 || value > 200000) return null;
    counters[name] = value;
  }
  const attempts = counters.applied + counters.withoutChanges + counters.failed;
  if (attempts + counters.adaptiveMemoryAdjustments === 0 ||
      counters.gaming + counters.studyWork + counters.chill + counters.customGameProfile > attempts ||
      counters.blockedByPolicy > attempts * 100) return null;
  return { id: data.batchId.toLowerCase(), version: summary.version, counters };
}

async function receiveUsage(request, env) {
  if (request.method !== 'POST') return respond(405);
  if (request.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLowerCase() !== 'application/json') return respond(415);
  if (+request.headers.get('Content-Length') > maxBodyBytes) return respond(413);

  const ip = request.headers.get('CF-Connecting-IP');
  if (!ip) return respond(403);
  // The rate limiter uses the request IP transiently. It is not written to D1.
  if (!(await env.USAGE_RATE.limit({ key: ip })).success) return respond(429);
  const batch = validateBatch(await readBatch(request));
  if (!batch) return respond(400);
  await env.USAGE_DB.prepare('INSERT OR IGNORE INTO usage_batches (id, received_day, version, counters) VALUES (?, ?, ?, ?)')
    .bind(batch.id, utcDay(new Date()), batch.version, JSON.stringify(batch.counters)).run();
  return respond(204);
}

function formatReport(day, results) {
  const totals = Object.fromEntries(counterNames.map(name => [name, 0]));
  for (const row of results) {
    for (const name of counterNames) totals[name] += row[name];
  }
  const versions = results.slice(0, 8).map(row =>
    `v${row.version} · ${row.applied} con cambios · ${row.failed} fallos · ${row.adaptiveMemoryAdjustments} ajustes de memoria`);
  return [
    `WinPulse Pro · resumen del ${day} (UTC)`,
    `Perfiles: Jugar ${totals.gaming}, Estudiar / Trabajar ${totals.studyWork}, Chill ${totals.chill}, por juego ${totals.customGameProfile}.`,
    `Operaciones: ${totals.applied} con cambios, ${totals.withoutChanges} sin cambios, ${totals.failed} fallidas.`,
    `Ajustes bloqueados por directiva: ${totals.blockedByPolicy}. Ajustes adaptativos de memoria: ${totals.adaptiveMemoryAdjustments}.`,
    '', ...versions,
  ].join('\n');
}

export async function sendDailyReports(env, now, send = fetch) {
  const cutoff = utcDay(now, 29);
  await env.USAGE_DB.batch([
    env.USAGE_DB.prepare('DELETE FROM usage_batches WHERE received_day < ?').bind(cutoff),
    env.USAGE_DB.prepare('DELETE FROM telegram_reports WHERE report_day < ?').bind(cutoff),
  ]);
  if (!env.TELEGRAM_BOT_TOKEN || !env.TELEGRAM_CHAT_ID) return;
  if (!/^\d+:[A-Za-z0-9_-]+$/.test(env.TELEGRAM_BOT_TOKEN) || !/^[1-9]\d{0,19}$/.test(env.TELEGRAM_CHAT_ID)) {
    throw new Error('Configuración del bot o del chat privado incorrecta.');
  }

  const { results: days } = await env.USAGE_DB.prepare(
    `SELECT DISTINCT received_day AS day FROM usage_batches
     WHERE received_day >= ? AND received_day < ?
       AND received_day NOT IN (SELECT report_day FROM telegram_reports)
     ORDER BY received_day`).bind(utcDay(now, 7), utcDay(now)).all();
  for (const { day } of days) {
    const { results } = await env.USAGE_DB.prepare(
      `SELECT version, COUNT(*) AS batches, ${aggregateColumns} FROM usage_batches
       WHERE received_day = ? GROUP BY version ORDER BY batches DESC, version ASC`).bind(day).all();
    if (!results.length) continue;
    let delivered = false;
    try {
      const response = await send(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/sendMessage`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ chat_id: env.TELEGRAM_CHAT_ID, text: formatReport(day, results) }),
        redirect: 'error',
      });
      delivered = response.ok && (await response.json()).ok === true;
    } catch { /* Do not propagate errors containing the bot URL or token. */ }
    if (!delivered) throw new Error('Telegram no confirmó el resumen. Se reintentará en la siguiente ejecución.');
    await env.USAGE_DB.prepare('INSERT OR IGNORE INTO telegram_reports (report_day) VALUES (?)').bind(day).run();
  }
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === '/privacidad' && request.method === 'GET' && env.USAGE_PRIVACY_TEXT?.trim()) {
      return new Response(env.USAGE_PRIVACY_TEXT, { headers: {
        'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
      } });
    }
    if (path !== '/v1/usage') return respond(404);
    if (!env.USAGE_PRIVACY_TEXT?.trim() || !env.TELEGRAM_BOT_TOKEN || !env.TELEGRAM_CHAT_ID) return respond(503);
    try { return await receiveUsage(request, env); }
    catch { return respond(503); }
  },
  async scheduled(event, env) {
    await sendDailyReports(env, new Date(event.scheduledTime));
  },
};
