using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace TkgPda.Api;

public static class TkgStaticWebHosting
{
    public static WebApplication UseTkgStaticWebHosting(this WebApplication app)
    {
        var configuration = app.Configuration;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TkgPda.WebHosting");

        var configuredWebRoot = configuration["Web:RootPath"];
        string? resolvedWebRoot = null;

        var candidates = new[]
        {
            configuredWebRoot,
            Path.Combine(AppContext.BaseDirectory, "web"),
            Path.Combine(AppContext.BaseDirectory, "Web"),
            Path.Combine(AppContext.BaseDirectory, "..", "web"),
            Path.Combine(AppContext.BaseDirectory, "..", "Web"),
            Path.Combine(Directory.GetCurrentDirectory(), "web"),
            Path.Combine(Directory.GetCurrentDirectory(), "Web"),
            Path.Combine(Directory.GetCurrentDirectory(), "build_staging", "Web"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "build_staging", "Web")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                resolvedWebRoot = Path.GetFullPath(candidate);
                break;
            }
        }

        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".wasm"] = "application/wasm";
        contentTypeProvider.Mappings[".json"] = "application/json";
        contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
        contentTypeProvider.Mappings[".woff2"] = "font/woff2";
        contentTypeProvider.Mappings[".svg"] = "image/svg+xml";

        string? adminDir = null;
        string? pdaDir = null;
        string? rootDir = null;

        if (resolvedWebRoot != null)
        {
            logger.LogInformation("Web assets directory resolved to: {Path}", resolvedWebRoot);
            var candAdmin = Path.Combine(resolvedWebRoot, "admin");
            if (Directory.Exists(candAdmin)) adminDir = candAdmin;

            var candPda = Path.Combine(resolvedWebRoot, "pda");
            if (Directory.Exists(candPda)) pdaDir = candPda;

            var candRoot = Path.Combine(resolvedWebRoot, "root");
            if (Directory.Exists(candRoot)) rootDir = candRoot;
        }
        else
        {
            logger.LogInformation("No external web assets directory found. Embedded landing page active.");
        }

        // 1. Admin Web
        if (adminDir != null && File.Exists(Path.Combine(adminDir, "index.html")))
        {
            logger.LogInformation("Admin Web mounted at /admin from {Path}", adminDir);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(adminDir),
                RequestPath = "/admin",
                ContentTypeProvider = contentTypeProvider
            });

            app.MapGet("/admin/{*subpath}", async (HttpContext context, string? subpath) =>
            {
                var path = context.Request.Path.Value ?? "";
                if (string.Equals(path, "/admin", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/admin/");
                    return;
                }

                var relative = subpath?.TrimStart('/') ?? "";
                var targetFile = Path.Combine(adminDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(relative) && File.Exists(targetFile))
                {
                    if (!contentTypeProvider.TryGetContentType(targetFile, out var contentType))
                    {
                        contentType = "application/octet-stream";
                    }
                    context.Response.ContentType = contentType;
                    await context.Response.SendFileAsync(targetFile);
                    return;
                }

                var ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext) && !ext.Equals(".html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var indexPath = Path.Combine(adminDir, "index.html");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
            }).AllowAnonymous();
        }

        // 2. PDA Web
        if (pdaDir != null && File.Exists(Path.Combine(pdaDir, "index.html")))
        {
            logger.LogInformation("PDA Web mounted at /pda from {Path}", pdaDir);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(pdaDir),
                RequestPath = "/pda",
                ContentTypeProvider = contentTypeProvider
            });

            app.MapGet("/pda/{*subpath}", async (HttpContext context, string? subpath) =>
            {
                var path = context.Request.Path.Value ?? "";
                if (string.Equals(path, "/pda", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/pda/");
                    return;
                }

                var relative = subpath?.TrimStart('/') ?? "";
                var targetFile = Path.Combine(pdaDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(relative) && File.Exists(targetFile))
                {
                    if (!contentTypeProvider.TryGetContentType(targetFile, out var contentType))
                    {
                        contentType = "application/octet-stream";
                    }
                    context.Response.ContentType = contentType;
                    await context.Response.SendFileAsync(targetFile);
                    return;
                }

                var ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext) && !ext.Equals(".html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var indexPath = Path.Combine(pdaDir, "index.html");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
            }).AllowAnonymous();

            // Root asset redirects in case a client or proxy requests Flutter assets without /pda/ prefix
            var rootFlutterAssets = new[] { "flutter_bootstrap.js", "main.dart.js", "operator_service_worker.js", "flutter_service_worker.js" };
            foreach (var asset in rootFlutterAssets)
            {
                var targetAsset = asset;
                app.MapGet($"/{targetAsset}", () => Results.Redirect($"/pda/{targetAsset}", permanent: true)).AllowAnonymous();
            }
        }

        // 3. Root landing page
        if (rootDir != null && File.Exists(Path.Combine(rootDir, "index.html")))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(rootDir),
                RequestPath = "",
                ContentTypeProvider = contentTypeProvider
            });

            app.MapGet("/", async (HttpContext context) =>
            {
                var indexPath = Path.Combine(rootDir, "index.html");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
            }).AllowAnonymous();
        }
        else
        {
            app.MapGet("/", (DemoModeOptions? demo) => Results.Content(GetDefaultLandingPageHtml(demo?.Enabled ?? false), "text/html; charset=utf-8")).AllowAnonymous();
        }

        // 4. Version endpoint
        app.MapGet("/version", (DemoModeOptions? demo) => Results.Ok(new
        {
            version = TkgVersionInfo.Version,
            commit = TkgVersionInfo.CommitHash,
            buildTimestamp = TkgVersionInfo.BuildTimestamp,
            service = TkgVersionInfo.ServiceName,
            status = "ok",
            demoMode = demo?.Enabled ?? false,
            adminWebMounted = adminDir != null,
            pdaWebMounted = pdaDir != null
        })).AllowAnonymous();

        app.MapGet("/health/version", (DemoModeOptions? demo) => Results.Ok(new
        {
            version = TkgVersionInfo.Version,
            commit = TkgVersionInfo.CommitHash,
            buildTimestamp = TkgVersionInfo.BuildTimestamp,
            service = TkgVersionInfo.ServiceName,
            status = "ok",
            demoMode = demo?.Enabled ?? false
        })).AllowAnonymous();

        return app;
    }

    private static string GetDefaultLandingPageHtml(bool isDemoMode = false)
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="tr">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>TKG PDA · Endüstriyel Sunucu</title>
          <style>
            :root {
              --bg: #0b0f19;
              --card-bg: #151d2f;
              --card-border: #22304d;
              --card-hover: #1b263e;
              --text-main: #f1f5f9;
              --text-muted: #94a3b8;
              --accent-blue: #3b82f6;
              --accent-blue-hover: #2563eb;
              --accent-teal: #0d9488;
              --accent-green: #10b981;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Oxygen, Ubuntu, Cantarell, "Helvetica Neue", sans-serif;
              background-color: var(--bg);
              color: var(--text-main);
              min-height: 100vh;
              display: flex;
              flex-direction: column;
              align-items: center;
              justify-content: center;
              padding: 24px;
            }
            .container {
              width: 100%;
              max-width: 840px;
              background: var(--card-bg);
              border: 1px solid var(--card-border);
              border-radius: 16px;
              padding: 36px 32px;
              box-shadow: 0 20px 40px rgba(0, 0, 0, 0.4);
            }
            .header {
              display: flex;
              align-items: center;
              justify-content: space-between;
              border-bottom: 1px solid var(--card-border);
              padding-bottom: 20px;
              margin-bottom: 28px;
            }
            .brand-title {
              display: flex;
              align-items: center;
              gap: 12px;
            }
            .badge-mark {
              background: var(--accent-blue);
              color: #ffffff;
              font-weight: 800;
              font-size: 18px;
              padding: 6px 14px;
              border-radius: 8px;
              letter-spacing: 0.5px;
            }
            .title-text h1 {
              font-size: 22px;
              font-weight: 700;
              color: #ffffff;
              line-height: 1.2;
            }
            .title-text p {
              font-size: 13px;
              color: var(--text-muted);
            }
            .status-pill {
              display: inline-flex;
              align-items: center;
              gap: 6px;
              padding: 6px 12px;
              border-radius: 9999px;
              background: rgba(16, 185, 129, 0.15);
              border: 1px solid rgba(16, 185, 129, 0.3);
              color: #34d399;
              font-size: 12px;
              font-weight: 600;
            }
            .status-dot {
              width: 8px;
              height: 8px;
              border-radius: 50%;
              background: #10b981;
              box-shadow: 0 0 8px #10b981;
            }
            .grid {
              display: grid;
              grid-template-columns: 1fr 1fr;
              gap: 20px;
              margin-bottom: 28px;
            }
            @media (max-width: 640px) {
              .grid { grid-template-columns: 1fr; }
            }
            .action-card {
              display: flex;
              flex-direction: column;
              justify-content: space-between;
              background: #0f172a;
              border: 1px solid var(--card-border);
              border-radius: 12px;
              padding: 24px;
              text-decoration: none;
              color: inherit;
              transition: all 0.2s ease;
            }
            .action-card:hover {
              background: var(--card-hover);
              border-color: var(--accent-blue);
              transform: translateY(-2px);
            }
            .card-icon {
              font-size: 28px;
              margin-bottom: 12px;
            }
            .card-title {
              font-size: 18px;
              font-weight: 700;
              color: #ffffff;
              margin-bottom: 8px;
            }
            .card-desc {
              font-size: 13px;
              color: var(--text-muted);
              line-height: 1.5;
              margin-bottom: 20px;
            }
            .card-btn {
              display: inline-flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              padding: 10px 16px;
              background: var(--accent-blue);
              color: #ffffff;
              font-weight: 600;
              font-size: 14px;
              border-radius: 8px;
              transition: background 0.2s ease;
            }
            .action-card:hover .card-btn {
              background: var(--accent-blue-hover);
            }
            .action-card.admin:hover {
              border-color: var(--accent-teal);
            }
            .action-card.admin .card-btn {
              background: var(--accent-teal);
            }
            .action-card.admin:hover .card-btn {
              background: #0f766e;
            }
            .info-panel {
              background: #0d1322;
              border-radius: 8px;
              padding: 16px 20px;
              font-size: 13px;
              color: var(--text-muted);
              display: flex;
              flex-wrap: wrap;
              justify-content: space-between;
              gap: 12px;
            }
            .info-panel a {
              color: var(--accent-blue);
              text-decoration: none;
            }
            .info-panel a:hover {
              text-decoration: underline;
            }
          </style>
        </head>
        <body>
          <div class="container">
            <div class="header">
              <div class="brand-title">
                <div class="badge-mark">TKG</div>
                <div class="title-text">
                  <h1>TKG PDA Server</h1>
                  <p>Endüstriyel Ambar & Operatör Sistemi</p>
                </div>
              </div>
              {{(isDemoMode
                ? """<div class="status-pill" style="background: rgba(245, 158, 11, 0.15); border: 1px solid rgba(245, 158, 11, 0.3); color: #fbbf24;"><span class="status-dot" style="background: #f59e0b; box-shadow: 0 0 8px #f59e0b;"></span><span>DEMO MODU (Çevrimdışı Simülasyon)</span></div>"""
                : $"<div class=\"status-pill\"><span class=\"status-dot\"></span><span>Çevrimiçi (v{TkgVersionInfo.Version})</span></div>")}}
            </div>

            {{(isDemoMode
              ? """
              <div style="margin-bottom: 24px; padding: 18px 20px; background: rgba(245, 158, 11, 0.08); border: 1px solid rgba(245, 158, 11, 0.25); border-radius: 14px;">
                <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 12px;">
                  <span style="font-size: 18px;">💡</span>
                  <h3 style="margin: 0; font-size: 15px; color: #fbbf24; font-weight: 600;">Demo Sunumu Hızlı Giriş Bilgileri</h3>
                </div>
                <div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 14px; font-size: 13px; color: #cbd5e1;">
                  <div style="background: rgba(15, 23, 42, 0.5); padding: 12px; border-radius: 8px; border: 1px solid rgba(255,255,255,0.06);">
                    <div style="color: #94a3b8; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px; font-weight: 600;">PDA Operatör Terminali (/pda/)</div>
                    <div>Cihaz Kodu: <code style="color: #38bdf8;">DEV-LINE</code> veya <code style="color: #38bdf8;">DEV-SUPERVISOR</code></div>
                    <div>Operatör Girişi: Sicil No <strong style="color: #fff;">1001</strong> (Şifresiz Giriş)</div>
                    <div>Süpervizör: Kullanıcı <code style="color: #fff;">dev-supervisor</code> / Şifre <code style="color: #fff;">Admin123!</code></div>
                  </div>
                  <div style="background: rgba(15, 23, 42, 0.5); padding: 12px; border-radius: 8px; border: 1px solid rgba(255,255,255,0.06);">
                    <div style="color: #94a3b8; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px; font-weight: 600;">Yönetim Paneli (/admin/)</div>
                    <div>Kullanıcı: <strong style="color: #fff;">dev-admin</strong></div>
                    <div>Şifre: <strong style="color: #fff;">Admin123!</strong></div>
                    <div>Kapsam: 9 Makine, Varyantlar, Slotlar, Raporlar & Simülasyon</div>
                  </div>
                </div>
              </div>
              """
              : "")}}

            <div class="grid">
              <a href="/pda/" class="action-card pda">
                <div>
                  <div class="card-icon">📱</div>
                  <div class="card-title">PDA / Operatör Terminali</div>
                  <div class="card-desc">Hat Operatörü, Yarı Mamül & Mamül Ambarı, Aktif Taşımalar, Mamul Kasa ve Üretim Sonu ekranları.</div>
                </div>
                <div class="card-btn">PDA Ekranını Aç →</div>
              </a>

              <a href="/admin/" class="action-card admin">
                <div>
                  <div class="card-icon">⚙️</div>
                  <div class="card-title">Yönetim Paneli (Admin)</div>
                  <div class="card-desc">Kullanıcı ve cihaz yetkilendirme, hat / varyant konfigürasyonu, RDS entegrasyonu ve raporlar.</div>
                </div>
                <div class="card-btn">Yönetim Panelini Aç →</div>
              </a>
            </div>

            <div class="info-panel">
              <div>Sürüm: <strong>{{TkgVersionInfo.Version}}</strong> (Commit: <code>{{TkgVersionInfo.CommitHash}}</code>)</div>
              <div>
                Hızlı Erişim: 
                <a href="/health/live">Sağlık Kontrolü</a> · 
                <a href="/health/ready">Hazır Olma</a> · 
                <a href="/version">Sürüm API</a>
              </div>
            </div>
          </div>
        </body>
        </html>
        """;
    }
}
