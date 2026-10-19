# TKG PDA — M4 6.16.5 Contract Matrix

**Durum:** Canlı ortamdan kısmen doğrulanmış çalışma belgesi
**Güncelleme:** 16 Ekim 2026
**Hedef ortam:** `TKGADAAPP11`, M4 `6.16.5`
**Command gate:** Kapalı; bu belge canlı robot dispatch yetkisi vermez

## Kanıt düzeyleri

| Durum | Anlamı |
|---|---|
| `VERIFIED-LIVE` | M4 `6.16.5` ortamında salt okunur olarak gözlendi |
| `VERIFIED-DOC` | Resmi M4 dokümanında mevcut, canlı auth/response testi bekliyor |
| `BLOCKED` | Gerekli commissioning veya credential bulunmuyor |
| `UNRESOLVED` | Saha değeri/semantiği doğrulanmadı; tahmin edilmeyecek |
| `NOT-AUTHORIZED` | Robot veya configuration mutation olduğu için çalıştırılmadı |

## Ortam sözleşmesi

| Alan | Değer | Durum | Kanıt |
|---|---|---|---|
| M4 product version | `6.16.5` | `VERIFIED-LIVE` | Web UI ve runtime package adları |
| M4 Boot version | `2.2.1` | `VERIFIED-LIVE` | Windows installation/service |
| Base URL | `http://10.4.10.111:5800` | `VERIFIED-LIVE` | Web UI ve listening socket |
| API health path | `GET /api/ping` | `VERIFIED-LIVE` | `401 Unauthorized`; endpoint canlı, auth zorunlu |
| M4 Boot URL | `http://10.4.10.111:6800` | `VERIFIED-LIVE` | Launcher UI; Fleet API değildir |
| Secondary M4 port | `5820` | `VERIFIED-LIVE` | Listening; normal HTTP değildir |
| Project name/path | `TKG` / `C:\Users\amr\m4\projects\TKG` | `VERIFIED-LIVE` | Server filesystem |
| Transport security | Plain HTTP | `VERIFIED-LIVE` | Port `5800` |
| HTTPS/WSS availability | — | `UNRESOLVED` | TLS listener görülmedi |

## Authentication sözleşmesi

| Konu | Beklenen sözleşme | Durum | Sonraki kanıt |
|---|---|---|---|
| Human Web login | M4 kullanıcı hesabı | `VERIFIED-LIVE` | Admin Web girişi ve authenticated `/api/ping` başarılı |
| M2M application auth | `xyy-app-id`, `xyy-app-key` | `PARTIAL` | Contract önceki project durumunda doğrulandı; `OneDemo` sonrası integration hesabı güncel Agency listesinde yok, yeniden doğrulama gerekli |
| Unauthenticated API | `401` | `VERIFIED-LIVE` | `/api/ping` sonucu |
| Minimum read scope | ping, scene, robot, entity/order query | `UNRESOLVED` | Application role/permission matrisi |
| Minimum command scope | create/query/cancel order | `UNRESOLVED` | Simulator/staging contract testi |
| Secret storage | TKG protected deployment configuration | `UNRESOLVED` | Implementation review |
| Credential logging | Hiçbir header/secret loglanmamalı | `VERIFIED-DOC` | Redaction testi |

Human admin credential backend entegrasyonunda kullanılmayacaktır. Test application key'i repository veya raporlara yazılmamıştır. Agency account formunda ayrı scope alanı görülmediği için endpoint allowlist uygulama tarafında zorunlu tutulmalıdır.

## Read-only API capability matrix

| Amaç | Method / path | M4 dokümanı | Canlı 6.16.5 | Karar |
|---|---|---|---|---|
| Health | `GET /api/ping` | Var | Auth yokken `401`; M2M ile `200`; yanlış key ile `401` | M2M contract doğrulandı |
| Scene list | `GET /api/fleet/scenes/list` | Var | Başlangıçta `[]`; simulator başladıktan sonra enabled `OneDemo` | Simulator scene doğrulandı; production scene değildir |
| Robot list | `GET /api/fleet/robots/all-all` | Var | `OneDemo` için 15 online/Idle mock robot ve negatif error contract doğrulandı | Simulator read contract doğrulandı |
| Scene snapshot | `POST /api/fleet/scenes/{sceneId}/query-all` | Var | `BLOCKED` | Enabled `sceneId` yok |
| Scene schema | `GET /api/fleet/scenes/schema/{id}` | Var | `OneDemo` için geçerli schema ve nonexistent ID error contract doğrulandı | Simulator schema contract doğrulandı |
| Entity query | `POST /api/entity/find/many` | Var | M2M ile HTTP `200`, boş `TransportOrder` listesi | Read-only contract doğrulandı |
| Paged entity query | `POST /api/entity/find/page` | Var | M2M ile HTTP `200`, `total=0` | Read-only contract doğrulandı |
| Order detail | `GET /api/fleet/orders/query-order-detail?orderId=...` | Var | `BLOCKED` | Test/simulator order gerekir |
| WebSocket telemetry | `ws://host:5800/wsm` | Var | `UNRESOLVED` | Auth, event modeli ve reconnect testi |

M4 query endpoint'lerinin bazıları `POST` kullanır; tabloda read-only olarak işaretlenenler mutation amacıyla kullanılmaz.

## Command API capability matrix

| Amaç | Method / path | Durum | Gerekli kapı |
|---|---|---|---|
| Order create | `POST /api/fleet/orders/create` | `NOT-AUTHORIZED` | Scene + endpoint + BinTask + simulator proof + kesin canlı test onayı |
| Order cancel | `POST /api/fleet/orders/cancel` | `NOT-AUTHORIZED` | Test order lifecycle ve cancel safety kanıtı |
| Callback add | `POST /api/fleet/orders/callback-add` | `NOT-AUTHORIZED` | Callback ingress/security tasarımı |
| Callback remove | `POST /api/fleet/orders/callback-remove` | `NOT-AUTHORIZED` | Kayıtlı callback ve rollback planı |
| Priority update | `POST /api/fleet/orders/update-priority-batch` | `NOT-AUTHORIZED` | İş ihtiyacı ve race testi |
| Fault retry | `POST /api/fleet/orders/retry-failed-orders` | `NOT-AUTHORIZED` | İlk fazda otomatik kullanılmayacak |
| Robot/scene/point mutations | Çeşitli | `NOT-AUTHORIZED` | Ayrı safety analizi ve scope |

## Canlı commissioning envanteri

| Kaynak | Doğrulanan durum | Contract etkisi |
|---|---|---|
| Enabled Fleet Scene | `OneDemo` simulator | Simulator contract testinde kullanılabilir; production mapping değildir |
| Robot | 15 simulated: Box 4, DN 3, Jack 8 | Tamamı mock/online/Idle; production robot değildir |
| Area | 2 simulator area | Production warehouse/area mapping yapılamaz |
| Point/path/bin | Simulator schema içinde mevcut | Yalnız açık simulator test location'ları kullanılabilir |
| Fleet TCP Server | Disabled | Robot bağlantı yolu hazır değil |
| RBK 3.5 protocol | Disabled | Robot protokolü etkin değil |
| QuickStore zones/tasks | Seçilmemiş | Warehouse readiness/occupancy kaynağı yok |
| Falcon templates | Test ve built-in template'ler | Production `BinTask` olarak kullanılamaz |

## TKG operasyon matrisi

Hiçbir location veya `BinTask` adı tahmin edilmemiştir.

| TKG operation | M4 pickup | M4 drop | Warehouse readiness | Fiziksel kanıt | Durum |
|---|---|---|---|---|---|
| `BRING_EMPTY` | `UNRESOLVED` empty source | `UNRESOLVED` machine slot | Gerekmez | Source load + target occupancy | `BLOCKED` |
| `BRING_FULL` | `UNRESOLVED` prepared full source | `UNRESOLVED` machine slot | Zorunlu | Source load + target occupancy | `BLOCKED` |
| `REMOVE_FULL` | `UNRESOLVED` machine slot | `UNRESOLVED` finished goods | Gerekmez | Source load + target occupancy | `BLOCKED` |
| `REMOVE_EMPTY` | `UNRESOLVED` machine slot | `UNRESOLVED` empty return | Gerekmez | Source load + target occupancy | `BLOCKED` |

## Order alan sözleşmesi

| TKG alanı | M4 alanı | Doğrulama durumu | Kural |
|---|---|---|---|
| `TransportRequest.Id` | `externalId` | `VERIFIED-DOC` | Deterministic, uzunluk/uniqueness canlı test edilecek |
| External execution ID | `orderId` | `VERIFIED-DOC` | Create response sonrası saklanır |
| Navigation profile | `sceneId` | `BLOCKED` | Onaylı profile-scene mapping gerekli |
| Template priority | `priority` | `VERIFIED-DOC` | Yüksek değer/yüksek öncelik canlı test edilecek |
| Source endpoint | Step `location` | `BLOCKED` | Point/bin type açıkça kaydedilecek |
| Destination endpoint | Step `location` | `BLOCKED` | Point/bin type açıkça kaydedilecek |
| Pickup action | `rbkArgs.binTask`, `forLoad=true` | `BLOCKED` | Onaylı saha BinTask değeri gerekli |
| Drop action | `rbkArgs.binTask`, `forUnload=true` | `BLOCKED` | Onaylı saha BinTask değeri gerekli |
| Container | `containerId` | `UNRESOLVED` | Kullanım/uniqueness sözleşmesi gerekli |
| Robot restriction | robot names/groups/tags | `BLOCKED` | Robot envanteri sonrası belirlenir |

## Status ve reconciliation sözleşmesi

| Konu | Durum | Zorunlu test |
|---|---|---|
| M4 order status enum'ları | `VERIFIED-DOC` | 6.16.5 gerçek response örnekleri |
| Terminal success/failure mapping | `UNRESOLVED` | Simulator + staging lifecycle |
| Callback event sırası | `UNRESOLVED` | Duplicate/out-of-order testleri |
| Query görünürlük gecikmesi | `UNRESOLVED` | Create sonrası bounded polling |
| `externalId` duplicate davranışı | `UNRESOLVED` | Aynı identity ile tekrar create testi |
| Create timeout belirsiz sonucu | Tasarım hazır | Otomatik retry öncesi query/reconcile |
| Order retention | `UNRESOLVED` | Entity query zaman aralığı testi |
| Restart recovery | Tasarım hazır | TKG worker restart acceptance |

## Physical evidence sözleşmesi

RDS WorkSite `filled/locked/disabled/syncFailed` semantiği M4'e kopyalanmayacaktır. M4 için ayrı ve doğrulanmış evidence adapter gerekir.

| Kanıt | Aday M4 kaynağı | Durum |
|---|---|---|
| Occupancy | Storage bin/entity/scene state | `UNRESOLVED` |
| Load | Bin/container/robot state | `UNRESOLVED` |
| Endpoint enabled | Scene/bin/point state | `UNRESOLVED` |
| Lock/reservation | M4 allocation veya TKG reservation | `UNRESOLVED` |
| Freshness timestamp | API response/entity timestamp | `UNRESOLVED` |
| Contradiction policy | TKG fail-close | Tasarım kararı |

`UNKNOWN`, stale veya contradiction halinde dispatch kapalı kalır.

## Gate durumu

| Gate | Durum | Kapatılması gerekenler |
|---|---|---|
| Contract Ready | `BLOCKED` | Scene, robot, endpoint, BinTask ve gerçek status örnekleri eksik |
| Read-only Ready | `PARTIAL` | M2M contract geçmiş durumda geçti; simulator scene/robot/schema okundu; güncel project credential, snapshot ve WebSocket eksik |
| Simulator Ready | `PARTIAL` | OneDemo ve 15 mock robot aktif; test location ve order lifecycle henüz doğrulanmadı |
| Shadow Ready | `PARTIAL` | Provider-neutral model ve SQL compatibility hazır; M4 read client ve read comparison eksik |
| Canary Ready | `NOT STARTED` | Commissioning, safety, rollback ve açık test onayı |
| Production Expansion | `NOT STARTED` | Canary sonuçları |
| RDS Retirement | `NOT STARTED` | Tüm profile'lar M4 ve aktif RDS işi yok |

## Bir sonraki veri toplama oturumu

Simulator doğrulamasında aşağıdaki sıra uygulanır:

1. `OneDemo` scene ve 15 mock robot salt okunur doğrulandı.
2. Simulator project için application credential ve secret handling yeniden doğrulanır.
3. Scene snapshot ve WebSocket telemetry redacted kanıt olarak alınır.
4. İki simulator location açıkça seçilir; production mapping olarak kaydedilmez.
5. No-motion payload doğrulamasından sonra simulator create/query/cancel testi ayrıca onaylanır.
6. Production scene, robot ve endpoint commissioning ayrı bir kapı olarak yürütülür.
