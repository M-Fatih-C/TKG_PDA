# TKG PDA

> **Dokümanlar taşındı.** 16 Eylül 2026'da `docs/` klasörü ve müşteri kaynakları
> `Masaüstü/TKG_PDA_Arsiv` altına alındı. Bu depoda yalnız çalışan kod,
> veritabanı şeması, testler ve başlatma betikleri kalır. Aşağıdaki `docs/...`
> bağlantıları arşivdeki `dokumanlar/docs/` klasörüne karşılık gelir.


TKG PDA, TKG Otomotiv'deki dokuz üretim makinesi/hücresi için AMR lojistik taleplerini yönetecek sistemdir. Bu repository; PostgreSQL veri mimarisini, müşteri Excel'lerinden üretilen izlenebilir master data'yı, normalize fiziksel yerleşimleri, iş akışı motorunu, güvenli ASP.NET Core API'yi, React Admin Web'i ve Android + Web/PWA Flutter Operator uygulamasını içerir.

> Baseline durumu: Phase 01–09 tamamlandı. **Phase 08B execution integration, development-only `TKG_SIM_MOVE` ile canlı simulator E2E üzerinde doğrulandı; Phase 09 aynı Flutter operator istemcisini Android ve Windows Edge/Chrome Web/PWA hedeflerine taşır.** Üretim forklift task'ları, SEER map-route, PLC, SAP ve robot koordinatları başlamamıştır.

> **Donmuş ürün şartı:** TKG Operator Client must support both Android industrial PDAs and Windows-based production-line touch terminals through Web/PWA. Both platforms use the same Flutter codebase, TKG API contracts, device-bound security model, and server-authoritative workflow rules.

> **WP-33 Operator akışı:** Operatör `N. Kasa Doldu`, `N. Kasa Boş` veya `Tüm Kasaları Kaldır` eylemini seçer; fiziksel slot ve RDS endpoint çözümü backend'de kalır. Global `Görevler` görünümü tüm makinelerin aktif business kuyruğunu teknik kimlik göstermeden sunar.

## Mevcut baseline

| Katman | Durum | Sonuç |
|---|---|---|
| Phase 01 — Database Architecture | Tamamlandı | `auth`, `master`, `ops`, `integration`, `audit` şemaları |
| Phase 01.1 — Schema Consistency | Tamamlandı | Nullable planning uniqueness ve ortak layout/profile ilişkileri doğrulandı |
| Phase 02A — Textual Master Data | Tamamlandı | 161 part, 10 container type, 51 product group, 191 planning row |
| Phase 02B — Physical Layout Extraction | Tamamlandı | 21 layout, 24 variant assignment, 183 display slot |
| Phase 03 — Business Workflow Engine | Tamamlandı | `BRING_EMPTY`, `REMOVE_FULL`, `REMOVE_EMPTY`, reservation, idempotency ve variant-switch koruması |
| Phase 04 — Backend API + Auth | Tamamlandı | JWT, RBAC, device binding, machine permission ve workflow HTTP boundary |
| Phase 05 — Admin Configuration API | Tamamlandı | Admin Web auth boundary, provisioning, draft/publish/versioning, ETag ve audit |
| Phase 06 — Admin Web | Tamamlandı | React/TypeScript yönetim ekranları ve normalize fiziksel layout editörü |
| Phase 07A — PDA + RDS Contract Readiness | Tamamlandı | PDA operational context, available-actions ve persisted RDS execution/readiness projeksiyonu |
| Phase 07B — Flutter PDA Application | Tamamlandı | Device-bound Android istemci, aynı sayfada part/variant/operation akışı, server-authoritative slot routing ve güvenli offline/idempotency UX |
| Phase 08A — Live RDS Readiness | Uygulandı | Salt-okunur RDS client, WorkSite mirror, explicit admin mapping ve readiness projection |
| Phase 08B — RDS Execution Integration | Tamamlandı | Server-side dispatch, callback, reconciliation, stop boundary ve load-state entegrasyonu |
| Phase 08C — RDS Simulator Commissioning | Tamamlandı | `TKG_SIM_MOVE`, tek TKG UUID, canlı `sim_01`, callback ve duplicate-safe retry kanıtlandı |
| Phase 09 — Multi-Platform Operator Client | Tamamlandı | Aynı Flutter kodundan Android + Edge/Chrome Web/PWA, responsive touch UI ve static-only PWA cache |

Phase 02B'deki 183 `master.layout_slots` kaydının 172'si lojistik pozisyon, 11'i `slot_role = 'MACHINE'` olan makine/hücre bölgesidir. Bu 11 bölge yalnızca fiziksel ve ekran yerleşim referansıdır; taşıma isteğinin `source_slot_id` veya `destination_slot_id` değeri olamaz. Şema ayrımı mevcut `slot_role` alanıyla yapabiliyor; Phase 03 workflow engine bu invariant'ı uygular ve Phase 04 API bütün mutasyonları aynı service sınırından geçirir.

## Repository yapısı

```text
db/migrations/                 PostgreSQL baseline şeması
db/seeds/generated/            Deterministik Phase 02A ve 02B seed'leri
data/generated/                Normalize JSON/CSV, issue raporları ve review PNG'leri
tools/phase_02a/               Metinsel Excel extraction pipeline'ı
tools/phase_02b/               Fiziksel layout extraction pipeline'ı
tests/                         Unit/regression ve fresh PostgreSQL testleri
docs/                          Mimari, ERD, phase raporları ve referanslar
src/TkgPda.Domain/             Saf workflow kuralları ve state machine
src/TkgPda.Application/        API'den bağımsız application service/port tanımları
src/TkgPda.Infrastructure.../  Transactional PostgreSQL workflow store
src/TkgPda.Api/                Authenticated ASP.NET Core HTTP boundary
src/TkgPda.AdminWeb/           React + TypeScript + Vite administration client
src/TkgPda.Pda/                Flutter Android + Web/PWA industrial operator client
rds/scripts/                   Version-controlled RDS Script artifact'i ve Node contract testleri
rds/tasks/commissioning/       Development-only RDS simulator task artifact'i
TKG.Pda.slnx                   .NET 10 solution
```

Müşterinin ham Excel, CAD, STEP ve görsel dosyaları yerel kaynak paketinde tutulur ve Git'e alınmaz. Repository'deki üretilmiş kayıtlar kaynak dosya adı, sheet/hücre aralığı ve SHA-256 provenance bilgilerini korur.

## Veritabanını kurma

Desteklenen yerel kurulum `tools/dev/start_local_stack.ps1` dosyasıdır. Betik `db/migrations` altındaki migration dosyalarını dosya sırasıyla uygular, ardından `db/seeds/generated/002a_customer_textual_master_data.sql`, `002b_customer_physical_layouts.sql` ve `002c_customer_layout_slot_part_assignments.sql` tohumlarını yükler.

Yalnızca `001_initial_schema.sql` uygulamak güncel API şemasını üretmez. Yeni bir veritabanı migration 001 ile başlar ve dizindeki son migration ile biter.

Geliştirme hesaplarının parolası betiğe yazılmaz. `-Password` veya `-SecurePassword` verin ya da genel geliştirme hesapları yoksa betiğin sorduğu parolayı girin.

Seed dosyaları idempotent tasarlanmıştır. İkinci uygulama mantıksal DB durumunu değiştirmemelidir.

## Pipeline ve testler

Python bağımlılıkları ilgili phase klasörlerindeki `requirements.txt` dosyalarındadır.

```powershell
python tools/phase_02a/pipeline.py --project-root .
python -m unittest discover -s tests/phase_02a -p "test_*.py" -v

python tools/phase_02b/pipeline.py --project-root .
python -m unittest discover -s tests/phase_02b -p "test_*.py" -v

pwsh tests/phase_02b/run_postgres_integration.ps1

dotnet build TKG.Pda.slnx --configuration Release
dotnet test tests/TkgPda.Auth.Tests --configuration Release
pwsh tests/phase_03/run_postgres_integration.ps1
pwsh tests/phase_04/run_postgres_api_integration.ps1
pwsh tests/phase_05/run_postgres_admin_integration.ps1
pwsh tests/phase_07a/run_postgres_pda_rds_integration.ps1
pwsh tests/phase_07b/run_real_api_contract.ps1
pwsh tests/phase_08a/run_postgres_rds_readiness_integration.ps1
pwsh tests/phase_08b/run_postgres_rds_execution_integration.ps1
# Explicit live opt-in + loopback simulator scene only:
pwsh tests/phase_08c/run_live_rds_simulator_e2e.ps1
node --test rds/scripts/test/tkg_integration.test.js

cd src/TkgPda.AdminWeb
npm ci
npm run typecheck
npm run lint
npm run test:run
npm run test:e2e
npm run build

cd ../TkgPda.Pda
dart format --output=none --set-exit-if-changed .
flutter analyze
flutter test
flutter build apk --debug --dart-define=TKG_API_BASE_URL=https://api.example.invalid
flutter build web --release --no-web-resources-cdn --base-href /operator/

cd ../..
node --test tests/phase_09/web_contract.test.mjs
pwsh tests/phase_09/run_web_operator_e2e.ps1
```

Phase 02B pipeline'ı fiziksel metre/milimetre veya robot/map koordinatı üretmez. `ui_x`, `ui_y`, `ui_width`, `ui_height` yalnızca PDA ekranında kullanılacak 0–1 normalize gösterim geometrisidir.

## Dokümantasyon

- [Dokümantasyon indeksi](docs/README.md)
- [Kabul edilmiş proje baseline'ı](docs/PROJECT_BASELINE.md)
- [Database ERD](docs/database/ERD.md)
- [Phase 01 notları](docs/PHASE_01_NOTES.md)
- [Phase 02A notları](docs/PHASE_02A_NOTES.md)
- [Phase 02B notları](docs/PHASE_02B_NOTES.md)
- [Phase 03 workflow engine notları](docs/PHASE_03_NOTES.md)
- [Phase 04 authenticated API notları](docs/PHASE_04_NOTES.md)
- [Phase 05 Admin Configuration API](docs/PHASE_05_ADMIN_CONFIGURATION_API.md)
- [Phase 06 Admin Web](docs/PHASE_06_ADMIN_WEB.md)
- [Phase 07A PDA + RDS Contract Readiness](docs/PHASE_07A_PDA_RDS_CONTRACT_READINESS.md)
- [Phase 07B Flutter PDA Application](docs/PHASE_07B_FLUTTER_PDA.md)
- [Phase 08A Live RDS Readiness](docs/PHASE_08A_LIVE_RDS_READINESS.md)
- [Phase 08B RDS Execution Integration](docs/PHASE_08B_RDS_EXECUTION_INTEGRATION.md)
- [Phase 08C RDS Simulator Commissioning](docs/PHASE_08C_RDS_SIMULATOR_COMMISSIONING.md)
- [Phase 09 Android + Web/PWA Operator](docs/PHASE_09_MULTIPLATFORM_OPERATOR.md)
- [RDSCore commissioning discovery](docs/commissioning/RDS_COMMISSIONING_DISCOVERY.md)
- [RDS commissioning snapshot schema](docs/commissioning/RDS_COMMISSIONING_SNAPSHOT_SCHEMA.md)
- [Operator Web/PWA deployment](docs/OPERATOR_WEB_DEPLOYMENT.md)
- [Normative RDS/PDA entegrasyon sözleşmesi](docs/RDS_PDA_INTEGRATION_SPEC.md)
- [Database specification v0.1](docs/TKG_PDA_DATABASE_SPEC_v0.1.md)

## Bilinen veri kalitesi sınırları

- Phase 02A: 159 izlenebilir data issue; bunların 156'sı warning, 3'ü info seviyesindedir.
- Çözülmemiş container kategori kodu yalnız `1285`'tir; blank planning mapping'leri ayrıca `NULL` kalır. `1284`, `285C`, `BYK`, `ORT`, `KCK` ve `ARABA` mevcut customer görsel/planning kanıtıyla uzlaştırılmıştır.
- Phase 02B: 28 slotta part ilişkisi, 74 slotta container ilişkisi çözülememiştir.
- 54 lojistik slot source-backed nominal container footprint'i taşır; kalan 129 region (118 lojistik + 11 MACHINE) `NULL` kalır. Nominal container ölçüsü robot pozu/clearance değildir.
- `67115BN050-10-HAOS` canonical koddur; HMTR görünümü source discrepancy olarak korunur.
- SEALER V1/V2 ortak layout kullanır; V3 için kanıt bulunmadığından layout ataması yapılmamıştır.

Ayrıntılı kararlar ve issue dağılımları [PROJECT_BASELINE.md](docs/PROJECT_BASELINE.md) ile phase raporlarında yer alır.
