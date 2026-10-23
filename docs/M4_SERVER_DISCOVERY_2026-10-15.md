# M4 Sunucu Keşif Kaydı — 15–16 Ekim 2026

## Kapsam

Bu kayıt, VPN üzerinden `10.4.10.111` adresindeki Windows sunucuda yapılan SSH ve M4 Web keşfinin sonuçlarını içerir. Açık kullanıcı onayıyla yalnız test Agency account kaydı oluşturulmuştur. Hiçbir M4 order oluşturma/iptal, robot hareketi, scene enable/disable, callback kaydı veya başka configuration save işlemi yapılmamıştır.

## Doğrulanan sunucu

| Alan | Doğrulanan değer |
|---|---|
| Hostname | `TKGADAAPP11` |
| İşletim sistemi | Microsoft Windows Server 2022 Standard |
| OS build | `20348` |
| Mimari / platform | 64-bit / VMware virtual machine |
| VPN adresi | `10.4.10.111` |
| SSH | OpenSSH for Windows 9.5, TCP `22` |
| SSH host-key | Sunucu ve istemci tarafında aynı ED25519 fingerprint ile doğrulandı |

## Kurulu ve çalışan SEER bileşenleri

### M4 Boot

| Alan | Doğrulanan değer |
|---|---|
| Ürün | M4 Boot |
| Sürüm | `2.2.1` |
| Kurulum konumu | `C:\Program Files\M4 Boot\` |
| Windows service | `M4 Boot Service` |
| Startup / durum | Automatic / Running |
| Process | `m4-boot-2-single.exe` |
| Yönetim portu | TCP `6800` |
| Yönetim URL | `http://10.4.10.111:6800/` |

`6800`, M4 Fleet API portu değildir. Bu port M4 Boot launcher yönetim arayüzüdür.

### M4 runtime

| Alan | Doğrulanan değer |
|---|---|
| Product version | `6.16.5` |
| Runtime | Java, M4 Boot tarafından başlatılıyor |
| Runtime dizini | `C:\Users\amr\m4\trick-m4-6.16.5\` |
| Java executable | `C:\Users\amr\m4\jdk\bin\java.exe` |
| Project | `C:\Users\amr\m4\projects\TKG\` |
| Web/API portu | TCP `5800` |
| Web URL | `http://10.4.10.111:5800/` |
| Ek runtime portu | TCP `5820`; normal HTTP endpoint değildir |
| Embedded database | Local TCP `1527`; M4 tarafından kullanılıyor |

Sürüm; `trick-m4`, `quick-fleet`, `quick-store`, `trick-base`, `trick-falcon`, `trick-robot` ve `trick-wcs` paket adlarında tutarlı biçimde `6.16.5` olarak görülmüştür.

### RoboShop Pro

| Alan | Doğrulanan değer |
|---|---|
| Executable | `C:\SeerGroup\RoboshopPro\RoboshopPro.exe` |
| Product version | `2.0.0.0` |
| Process durumu | Running |

RoboShop Pro sürümü, M4 Fleet runtime sürümü olarak kullanılmaz.

## Ağ ve servis bulguları

| Port | Process/servis | Amaç / sonuç |
|---:|---|---|
| 22 | OpenSSH Server | VPN üzerinden yönetim erişimi |
| 3389 | Remote Desktop | Windows yönetim erişimi |
| 5000 | TKG PDA Demo API | Aynı sunucudaki offline demo API |
| 5432 | TKG PostgreSQL | Localhost binding |
| 5800 | M4 Java runtime | M4 Web ve HTTP API |
| 5820 | M4 Java runtime | HTTP olmayan ek M4 protokol portu |
| 6800 | M4 Boot | Launcher yönetim arayüzü |

M4 `5800` üzerinde plain HTTP kullanmaktadır. Production kullanımı için VPN/ağ segmentasyonu, erişim listesi ve credential taşıma riski ayrıca kapatılmalıdır.

## API ve authentication kanıtı

- `GET http://127.0.0.1:5800/api/ping` M4 sunucusu üzerinde `401 Unauthorized` döndürmüştür.
- Bu sonuç endpoint'in canlı olduğunu ve authentication zorunlu tuttuğunu kanıtlar.
- M4 Web human login başarılıdır ve arayüz sürümü `6.16.5` göstermektedir.
- Human Web session ile `/api/ping`, scene list ve negatif robot/scene contract testleri başarılıdır.
- Human admin oturumu machine-to-machine credential yerine kullanılmayacaktır.
- Başlangıçta boş olan Agency account tablosuna açık kullanıcı onayıyla `tkg-pda-integration-test` hesabı eklenmiştir.
- `xyy-app-id` ve `xyy-app-key` ile M2M ping, scene discovery ve salt okunur `TransportOrder` sorguları başarılıdır; yanlış key `401` ile reddedilmiştir.
- `OneDemo` demo project etkinleştirildikten sonra Agency account listesi değişmiş ve bu integration hesabı güncel listede görülmemiştir. Credential project geçişinden sonra yeniden oluşturulmalı/doğrulanmalıdır; demo ile gelen paylaşımlı/default hesaplar TKG backend credential'ı yapılmayacaktır.
- Hiçbir parola, session cookie, application key veya token rapora alınmamıştır.

Web yönetiminde varsayılan yönetici kimlik bilgilerinin halen geçerli olduğu doğrulanmıştır. Production açılışından önce güçlü bir credential ile değiştirilmesi ve erişimin yetkili kullanıcılarla sınırlandırılması gerekir.

## Project ve commissioning durumu

İlk keşif ve sonrasında başlatılan One-click Simulation ile doğrulanan mevcut durum:

| Alan | Sonuç |
|---|---|
| Enabled Fleet Scene | `OneDemo` simulator scene |
| Robot toplamı / online | `15 / 15`; tamamı simulated/mock |
| Unfinished/Fault order | `0 / 0` |
| TransportOrder listesi | Boş |
| Agency account listesi | Onaylı test hesabı mevcut; application key rapora alınmadı |
| Area | `2` simulator area |
| Robot group | Box, DN, Jack |
| Point/path/bin | OneDemo schema içinde simulator yapıları mevcut |
| Fleet TCP Server | Disabled |
| RBK 3.5 protocol | Disabled |
| QuickStore outbound zone | Seçilmemiş |
| QuickStore empty-container zone | Seçilmemiş |
| Container outbound Falcon Task | Seçilmemiş |

İlk keşifte scene ve robot envanteri yoktu. Kullanıcının One-click Simulation başlatmasından sonra `OneDemo` enabled hale gelmiş ve 15 mock robot API üzerinden görünmüştür. Simulator schema iki area, üç robot group, üç map ve point/path/bin yapılarını içerir. Bunların hiçbiri production saha mapping'i veya TKG customer master data kanıtı değildir.

Falcon Task Template ekranında yalnızca test/built-in template'ler görülmüştür. Built-in `transferGoodsFromStartBinToEndBin` template'i M4 project dosyalarında da vardır. Bu template TKG operasyonları için onaylı bir `BinTask` veya production taşıma sözleşmesi sayılmaz.

## Project dosya yapısı

Doğrulanan ana dizinler:

```text
C:\Users\amr\m4\projects\TKG\
  config\
  db\
  files\
  fleet\
  logs\
  m4.license
```

`m4.license`, M4 Boot config, session ve credential içerikleri okunmamış veya rapora yazılmamıştır. Embedded database dosyaları değiştirilmemiştir.

## Aynı sunucudaki TKG bileşeni

TCP `5000` üzerinde `TkgPda.Api.exe`, `C:\Program Files\TKG PDA Demo\` altında çalışmaktadır. Bu nedenle:

- offline demo ile gerçek M4 network erişimi ayrı tutulmalıdır;
- production M4 credential'ları demo configuration'a eklenmemelidir;
- M4 callback adresi belirlenirken demo ve production API ayrımı korunmalıdır;
- M4 `5800`, M4 Boot `6800` ve TKG Demo `5000` rolleri birbirine karıştırılmamalıdır.

## Hazır olma değerlendirmesi

| Geçiş kapısı | Durum | Kanıt / eksik |
|---|---|---|
| Runtime Installed | PASS | M4 `6.16.5` çalışıyor |
| Web Access | PASS | Admin Web erişimi ve sürüm doğrulandı |
| API Liveness | PASS | `/api/ping` endpoint'i `401` ile authentication istiyor |
| Machine Authentication | PARTIAL | Contract doğrulandı; `OneDemo` project geçişinden sonra TKG integration hesabı yeniden kurulmalı |
| Scene Discovery | PASS-SIMULATOR | Enabled `OneDemo`; production scene yok |
| Robot Discovery | PASS-SIMULATOR | 15 mock robot online/Idle |
| Point/Bin Discovery | PARTIAL-SIMULATOR | OneDemo schema okunuyor; test endpoint seçimi yapılmadı |
| Physical Evidence | BLOCKED | Occupancy/load kaynağı yapılandırılmamış |
| Order Contract Proof | BLOCKED | Scene, endpoint ve approved BinTask yok |
| Live Dispatch | NOT AUTHORIZED | Ayrı, kesin kapsamlı kullanıcı onayı gerekir |

## Sonraki güvenli adımlar

1. `OneDemo` simulator project için Agency credential ve read-only snapshot/WebSocket testleri tamamlanır.
2. İki simulator test location açıkça seçilir; no-motion order payload doğrulanır.
3. Ayrı onayla simulator create/query/cancel lifecycle çalıştırılır.
4. Gerçek saha aşamasında SEER/RoboShop commissioning ile production map ve robot M4'e bağlanır.
5. Production Fleet Scene oluşturulur veya mevcut scene etkinleştirilir.
6. Production scene, robot, point ve storage-bin listeleri yeniden doğrulanır.
7. Test application credential'ı protected deployment secret store'a alınır; Agency account formunda görünmeyen minimum scope uygulama allowlist'i ile sınırlandırılır.
8. Production authenticated read-only probe seti çalıştırılır:
   - `GET /api/ping`
   - scene snapshot/list
   - `GET /api/fleet/robots/all-all`
   - point/storage-bin discovery
   - bounded `TransportOrder` query
9. TKG logical endpoint eşlemeleri müşteri/SEER onayıyla Contract Matrix'e eklenir.
10. Physical occupancy/load kanıtı bulunmadan production dispatch gate açılmaz.

## Unresolved

- M4 Agency account için minimum yetki/scope mekanizması
- HTTPS/WSS veya ağ seviyesinde telafi kontrolü
- Production `sceneId`/scene name; `OneDemo` yalnız simulator kanıtıdır
- Robot, robot group ve tag envanteri
- Point/storage-bin envanteri
- TKG machine/variant/part endpoint eşlemeleri
- Onaylı pickup/drop `BinTask` adları
- Physical occupancy/load evidence kaynağı ve freshness sınırı
- Callback güvenlik ve network yöntemi
- M4 order retention ve `externalId` uniqueness davranışı
