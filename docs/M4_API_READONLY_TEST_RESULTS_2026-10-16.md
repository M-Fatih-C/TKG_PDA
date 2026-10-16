# M4 6.16.5 Read-only API Test Sonuçları — 16 Ekim 2026

## Kapsam

Hedef: `http://10.4.10.111:5800`
M4: `6.16.5`
Project: `TKG`
Test türü: Liveness, authentication boundary, read-only discovery ve negatif contract testleri

Bu oturumda order create/cancel, callback add/remove, robot mutation, scene mutation veya canlı robot hareketi yapılmamıştır.

## Sonuç özeti

| # | Test | Authentication | Beklenen | Gözlenen | Sonuç |
|---:|---|---|---|---|---|
| 1 | `GET /api/ping` | Yok | `401` | HTTP `401`, boş `text/plain` body | PASS |
| 2 | `GET /api/fleet/scenes/list` | Yok | `401` | HTTP `401`, boş `text/plain` body | PASS |
| 3 | `GET /api/fleet/robots/all-all` | Yok | `401` | HTTP `401`, boş `text/plain` body | PASS |
| 4 | `GET /api/ping` | Human admin session | Kullanıcı bilgisi | `id`, `username`, `roAdmin` ve permissions yapısı döndü | PASS |
| 5 | `GET /api/fleet/scenes/list` | Human admin session | Scene listesi | `[]` | PASS; enabled scene yok |
| 6 | `GET /api/fleet/robots/all-all` | Human admin session | Parametre validasyonu | `errMissingHttpQueryParam`, `sceneName|sceneId` | PASS |
| 7 | `GET /api/fleet/robots/all-all?sceneName=__contract_test_nonexistent__&rawAll=false` | Human admin session | Scene bulunamadı | `errFleetNoSuchSceneByName` | PASS |
| 8 | `GET /api/fleet/scenes/schema/__contract_test_nonexistent__` | Human admin session | Scene bulunamadı | `errFleetNoSuchSceneById` | PASS |
| 9 | Agency account read | Human admin session | Existing M2M account listesi | Başlangıçta tablo boştu; açık kullanıcı onayıyla test hesabı oluşturuldu | PASS |
| 10 | TransportOrder read | Human admin session | Read-only entity query | Failed/Unallocated/Allocated/Created Today/Done Today sayaçları `0`; tablo boş | PASS |
| 11 | `GET /api/ping` | M2M application headers | `200` | HTTP `200`, JSON object, `173 ms` | PASS |
| 12 | `GET /api/ping` | M2M, yanlış key | `401` | HTTP `401`, boş `text/plain` body | PASS |
| 13 | `GET /api/fleet/scenes/list` | M2M application headers | Scene listesi | HTTP `200`, boş liste, `44 ms` | PASS; enabled scene yok |
| 14 | `GET /api/fleet/robots/all-all` | M2M application headers | Parametre validasyonu | HTTP `400`, `errMissingHttpQueryParam`, `44 ms` | PASS |
| 15 | `GET /api/fleet/robots/all-all?sceneName=__contract_test_nonexistent__&rawAll=false` | M2M application headers | Scene bulunamadı | HTTP `400`, `errFleetNoSuchSceneByName`, `53 ms` | PASS |
| 16 | `GET /api/fleet/scenes/schema/__contract_test_nonexistent__` | M2M application headers | Scene bulunamadı | HTTP `400`, `errFleetNoSuchSceneById`, `44 ms` | PASS |
| 17 | `POST /api/entity/find/page` (`TransportOrder`) | M2M application headers | Boş sayfa | HTTP `200`, `total=0`, kayıt `0`, `54 ms` | PASS |
| 18 | `POST /api/entity/find/many` (`TransportOrder`) | M2M application headers | Boş liste | HTTP `200`, kayıt `0`, `38 ms` | PASS |
| 19 | `GET /api/fleet/scenes/list` | Human admin session | Aktif simulator scene | `OneDemo`, disabled=`false` | PASS |
| 20 | `GET /api/fleet/robots/all-all?sceneName=OneDemo&rawAll=false` | Human admin session | Simulator robot envanteri | 15 robot; tamamı online, On Duty, Idle, `mock=true`, alarms boş | PASS |
| 21 | `GET /api/fleet/scenes/schema/{OneDemoSceneId}` | Human admin session | Geçerli scene schema | 2 area, 3 robot group, 15 simulated robot, map/point/path/bin yapıları | PASS |

Human-session testlerinde response body M4 Web oturumuyla aynı origin üzerinden okunmuştur. Browser test yüzeyi HTTP status kodunu ayrıca raporlamadığı için bu ilk testlerde body contract'ı kaydedilmiştir. Unauthenticated ve M2M testlerinin HTTP status kodları doğrudan HTTP client ile doğrulanmıştır.

Çalıştırılan 21 salt-okunur kontrolün tamamı geçti. Geçerli scene snapshot ve WebSocket kontrolleri henüz çalıştırılmadı. Hiçbir order/configuration mutation endpoint'i çağrılmadı.

## OneDemo simulator kanıtı

Kullanıcı M4 One-click Simulation başlattıktan sonra API envanteri yeniden okunmuştur. Bu envanter yalnız simulator contract kanıtıdır; production robot, saha haritası veya TKG endpoint eşlemesi değildir.

| Alan | Gözlenen değer |
|---|---|
| Scene | `OneDemo`, enabled |
| Area | `1F Warehouse - Production Line`, `2F Raw Material Inbound` |
| Robot group | `Box`, `DN`, `Jack` |
| Robot dağılımı | Box `4`, DN `3`, Jack `8`; toplam `15` |
| Robot türü | Tamamı `simulated=true` / runtime `mock=true` |
| Runtime | Tamamı online, On Duty, Idle, disabled=`false` |
| Safety/status | blocked=`false`, emergency=`false`, alarms boş |
| Controller/IP | `M4QuickFleet` / loopback `127.0.0.1` |
| Haritalar | `demo_box_3.smap`, `demo_DN.smap`, `demo_jack_2F.smap` |

## Doğrulanan hata sözleşmeleri

### Eksik scene parametresi

```json
{
  "args": ["sceneName|sceneId"],
  "code": "errMissingHttpQueryParam",
  "message": "Missing HTTP query parameter: sceneName|sceneId"
}
```

### Bulunmayan scene name

```json
{
  "args": ["__contract_test_nonexistent__"],
  "code": "errFleetNoSuchSceneByName",
  "message": "No such scene: __contract_test_nonexistent__"
}
```

### Bulunmayan scene ID

```json
{
  "args": ["__contract_test_nonexistent__"],
  "code": "errFleetNoSuchSceneById",
  "message": "No such scene: __contract_test_nonexistent__"
}
```

Bu hata body'leri TKG M4 adapter'ında yalnız HTTP status ile değil `code` alanıyla da sınıflandırılmalıdır.

## Authentication bulgusu

- Human M4 Web session read-only API'lere erişebilmektedir.
- M2M entegrasyonu için resmi contract `xyy-app-id` ve `xyy-app-key` header'larıdır.
- Açık kullanıcı onayıyla `tkg-pda-integration-test` Agency account oluşturulmuştur.
- Doğru application headers ile `/api/ping`, scene discovery ve `TransportOrder` query testleri başarılıdır.
- Yanlış application key `401` ile reddedilmiştir.
- `OneDemo` demo project etkinleştirildikten sonra mevcut Agency account listesi değişmiş ve önce oluşturulan `tkg-pda-integration-test` hesabı güncel listede görülmemiştir. Bu nedenle önceki M2M testi contract kanıtı olarak geçerlidir; güncel simulator project için integration credential yeniden doğrulanmalıdır.
- Human admin session TKG backend credential'ı olarak kullanılmayacaktır.
- Application key hiçbir rapora, repository dosyasına veya test çıktısına yazılmamıştır.
- Agency account formunda ayrı bir permission/scope alanı görülmemiştir. Bu nedenle credential operasyonel olarak geniş yetkili kabul edilmeli ve client yalnız onaylı endpoint'lerle sınırlandırılmalıdır.

## Envanter bulgusu

- `OneDemo` simulator scene enabled durumdadır.
- Geçerli robot listesi ve scene schema başarıyla okunmuştur.
- Scene içinde map, point, path ve simulator bin yapıları vardır.
- Bunlar production commissioning verisi değildir ve TKG master data eşlemesi olarak kullanılmayacaktır.
- TransportOrder envanteri boştur.
- Simulator robot envanteri 15'tir.

## Çalıştırılmayan testler

| Test | Neden |
|---|---|
| Valid scene snapshot (`query-all`) | M2M credential mevcut simulator project'e yeniden bağlanmalı ve POST read contract çalıştırılmalı |
| WebSocket `/wsm` authentication/event testi | Authentication ve event capture harness hazırlanmadı |
| Order create/query/cancel lifecycle | Ayrı simulator test scope'u ve doğrulanmış test location seçimi gerekli |
| Callback add/remove | Callback güvenlik ve ingress tasarımı yok |

## Sonraki test kapısı

1. Credential deployment secret store'a alınır; dokümana, loga veya source control'e yazılmaz.
2. Önceki integration hesabı güncel `OneDemo` Agency listesinde görülmediği için simulator-only credential yeniden oluşturulur/doğrulanır.
3. Valid scene snapshot ve WebSocket authentication/event modeli salt okunur olarak kanıtlanır.
4. Simulator içinden iki açık test location seçilir ve no-motion payload doğrulaması yapılır.
5. Açık simulator test kapsamıyla create → query → cancel lifecycle çalıştırılır.
6. Production commissioning bundan ayrı tutulur; simulator isimleri production mapping'e taşınmaz.
