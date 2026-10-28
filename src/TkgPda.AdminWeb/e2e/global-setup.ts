import { execFileSync, spawn } from 'node:child_process'
import { existsSync, mkdirSync, readdirSync } from 'node:fs'
import path from 'node:path'
import {
  ADMIN_USERNAME,
  API_URL,
  PG_DATABASE,
  PG_PORT,
  TEMP_ROOT,
  assertPortAvailable,
  buildConnectionString,
  createRuntimeSecrets,
  ensureLogFile,
  hashPassword,
  killProcessTree,
  pgTool,
  psql,
  psqlArgs,
  removeRuntime,
  removeTempRoot,
  repoRoot,
  sleep,
  stopPostgres,
  writeRuntime,
} from './harness'

const run = (exe: string, args: string[], env?: NodeJS.ProcessEnv) =>
  execFileSync(exe, args, { stdio: 'inherit', env: { ...process.env, ...env } })

const applySqlFile = (relativePath: string) =>
  run(pgTool('psql'), [...psqlArgs(), '-f', path.join(repoRoot, relativePath)], { PGPASSWORD: 'trust' })

async function waitForApi(apiPid: number): Promise<void> {
  const deadline = Date.now() + 90_000
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${API_URL}/health/ready`)
      if (response.ok) {
        const body = (await response.json()) as { status?: string }
        if (body.status === 'ready') return
      }
    } catch {
      // API not accepting connections yet.
    }
    await sleep(750)
  }
  throw new Error(`API did not report ready at ${API_URL}/health/ready within the timeout (pid ${apiPid}).`)
}

export default async function globalSetup(): Promise<void> {
  const dataDir = path.join(TEMP_ROOT, 'data')
  const { adminPassword, jwtSigningKey } = createRuntimeSecrets()
  const connectionString = buildConnectionString()
  let apiPid = 0

  removeRuntime()
  await assertPortAvailable(PG_PORT)
  await assertPortAvailable(Number(new URL(API_URL).port))

  // Fresh, isolated cluster every run. Clean up any crashed leftover first.
  if (existsSync(TEMP_ROOT)) {
    stopPostgres(dataDir)
    removeTempRoot()
  }
  mkdirSync(TEMP_ROOT, { recursive: true })

  console.log('[e2e] initializing throwaway PostgreSQL cluster…')
  run(pgTool('initdb'), ['-D', dataDir, '-U', 'postgres', '-A', 'trust', '--no-locale', '-E', 'UTF8'])
  run(pgTool('pg_ctl'), [
    '-D', dataDir,
    '-l', path.join(TEMP_ROOT, 'postgres.log'),
    '-o', `-p ${PG_PORT} -h 127.0.0.1`,
    '-w', 'start',
  ])

  try {
    run(pgTool('createdb'), ['-h', '127.0.0.1', '-p', String(PG_PORT), '-U', 'postgres', PG_DATABASE], { PGPASSWORD: 'trust' })

    console.log('[e2e] applying all SQL migrations and required customer master seeds…')
    const migrationsDir = path.join(repoRoot, 'db/migrations')
    const migrations = readdirSync(migrationsDir)
      .filter((name) => name.endsWith('.sql'))
      .sort()
    for (const migration of migrations) applySqlFile(`db/migrations/${migration}`)
    applySqlFile('db/seeds/generated/002a_customer_textual_master_data.sql')
    applySqlFile('db/seeds/generated/002b_customer_physical_layouts.sql')
    applySqlFile('db/seeds/generated/002c_customer_layout_slot_part_assignments.sql')

    console.log('[e2e] seeding isolated ADMIN identity…')
    const hash = hashPassword(adminPassword).replaceAll("'", "''")
    psql(
      `INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active) ` +
      `VALUES ('${ADMIN_USERNAME}', '${hash}', 'E2E Yonetici', 'E2E-ADMIN', true) ` +
      `ON CONFLICT (username) DO NOTHING;` +
      `INSERT INTO auth.user_roles (user_id, role_id) ` +
      `SELECT u.id, r.id FROM auth.users u JOIN auth.roles r ON r.code = 'ADMIN' ` +
      `WHERE u.username = '${ADMIN_USERNAME}' ON CONFLICT DO NOTHING;`,
    )

    console.log('[e2e] building the ASP.NET Core API (Release)…')
    run('dotnet', ['build', path.join(repoRoot, 'src/TkgPda.Api/TkgPda.Api.csproj'), '-c', 'Release', '--nologo', '-v', 'quiet'])

    console.log(`[e2e] starting the real API on ${API_URL}…`)
    const dll = path.join(repoRoot, 'src/TkgPda.Api/bin/Release/net10.0/TkgPda.Api.dll')
    const logFd = ensureLogFile()
    const api = spawn('dotnet', [dll, '--urls', API_URL], {
      cwd: path.join(repoRoot, 'src/TkgPda.Api'),
      env: {
        ...process.env,
        ConnectionStrings__TkgPda: connectionString,
        Authentication__Jwt__SigningKey: jwtSigningKey,
        Rds__Enabled: 'false',
        Rds__DispatchEnabled: 'false',
        RdsCoreDiscovery__Enabled: 'false',
        RdsCoreExecution__Enabled: 'false',
        ASPNETCORE_ENVIRONMENT: 'Production',
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      },
      stdio: ['ignore', logFd, logFd],
    })
    api.unref()
    apiPid = api.pid ?? 0

    writeRuntime({
      apiUrl: API_URL,
      apiPid,
      pgPort: PG_PORT,
      pgDatabase: PG_DATABASE,
      pgBin: path.dirname(pgTool('psql')),
      tempRoot: TEMP_ROOT,
      dataDir,
      adminUsername: ADMIN_USERNAME,
      adminPassword,
    })

    await waitForApi(apiPid)
    console.log('[e2e] API is ready; handing off to Playwright.')
  } catch (error) {
    // Best-effort teardown so a failed setup does not leak a running cluster.
    killProcessTree(apiPid)
    stopPostgres(dataDir)
    removeTempRoot()
    removeRuntime()
    throw error
  }
}
